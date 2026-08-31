using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;

namespace ReviewAgent.Core.Safety;

/// <summary>
/// Единственная дверь к моделям. Через неё проходят все обращения из состояний графа,
/// поэтому ретраи, предохранитель, бюджет и учёт расхода описаны один раз, а не
/// повторяются в каждом состоянии — там их рано или поздно забыли бы включить.
/// </summary>
public sealed class ModelGate
{
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);

    private readonly RetryOptions _retry;
    private readonly CircuitBreakerOptions _breaker;
    private readonly RunGuard _guard;
    private readonly RunMetrics _metrics;
    private readonly ILogger<ModelGate> _logger;

    public ModelGate(
        IOptions<GuardrailOptions> options,
        RunGuard guard,
        RunMetrics metrics,
        ILogger<ModelGate> logger)
    {
        _retry = options.Value.Retry;
        _breaker = options.Value.CircuitBreaker;
        _guard = guard;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет обращение к модели под защитой. Возвращает <c>null</c>, если ответа нет
    /// по любой причине: вызывающее состояние обязано трактовать это как отказ и уйти
    /// на свой запасной путь (fail-closed), а не считать, что замечаний не нашлось.
    /// </summary>
    public async Task<T?> Ask<T>(
        string stage,
        Chat chat,
        Func<CancellationToken, Task<T?>> call,
        CancellationToken cancellationToken) where T : class
    {
        var model = chat.Model;

        if (IsOpen(model.Name))
        {
            _logger.LogWarning("Предохранитель модели {Model} разомкнут — этап {Stage} пропускает обращение",
                model.Name, stage);
            _metrics.AddGuardrailEvent("circuit-breaker",
                $"{stage}: обращение к {model.Name} не выполнялось, предохранитель разомкнут");
            _metrics.AddModelCall(new ModelCallMetric(stage, model.Name, 0, 0, 0, 0, 0, "skipped",
                "предохранитель разомкнут"));
            return null;
        }

        if (!_guard.CanCallModel(out var limit))
        {
            _metrics.AddGuardrailEvent("budget", $"{stage}: обращение к {model.Name} отменено — {limit}");
            _metrics.AddModelCall(new ModelCallMetric(stage, model.Name, 0, 0, 0, 0, 0, "skipped", limit));
            return null;
        }

        var attempts = 0;
        var started = Stopwatch.GetTimestamp();
        var inputBefore = chat.InputTokens;
        var outputBefore = chat.OutputTokens;

        while (true)
        {
            attempts++;
            try
            {
                var result = await call(cancellationToken);

                // Пустой результат без исключения — модель ответила, но ответ не лёг в схему.
                // Повторять бессмысленно и дорого: та же модель на том же промпте ответит так же.
                var outcome = result is null ? "invalid" : "ok";
                Record(stage, model, attempts, started, chat, inputBefore, outputBefore, outcome,
                    result is null ? "ответ не соответствует схеме" : null);

                if (result is null)
                {
                    _metrics.AddGuardrailEvent("output-schema",
                        $"{stage}: {model.Name} вернула ответ, не разбираемый по схеме");
                    RegisterFailure(model.Name);
                }
                else
                {
                    _consecutiveFailures.Remove(model.Name);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                var canRetry = IsTransient(e) && attempts <= _retry.MaxAttempts;

                if (!canRetry)
                {
                    Record(stage, model, attempts, started, chat, inputBefore, outputBefore, "failed", e.Message);
                    RegisterFailure(model.Name);
                    _logger.LogWarning("Этап {Stage}: модель {Model} не ответила ({Error})",
                        stage, model.Name, e.Message);
                    return null;
                }

                var delay = _retry.BaseDelayMs * (1 << (attempts - 1));
                _metrics.AddGuardrailEvent("retry",
                    $"{stage}: попытка {attempts} к {model.Name} не удалась ({Short(e.Message)}), повтор через {delay} мс");
                _logger.LogWarning("Этап {Stage}: попытка {Attempt} не удалась ({Error}) — повтор через {Delay} мс",
                    stage, attempts, Short(e.Message), delay);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private void Record(
        string stage, ModelOptions model, int attempts, long started, Chat chat,
        long inputBefore, long outputBefore, string outcome, string? error)
    {
        var input = chat.InputTokens - inputBefore;
        var output = chat.OutputTokens - outputBefore;

        _metrics.AddModelCall(new ModelCallMetric(
            stage, model.Name, attempts,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            input, output, model.CostOf(input, output),
            outcome, error));
    }

    /// <summary>
    /// Разомкнут ли предохранитель модели. Полуоткрытого состояния нет намеренно:
    /// прогон живёт минуты, и «подождать восстановления» здесь не у кого — упавшая
    /// модель в пределах одного прогона считается упавшей до конца.
    /// </summary>
    private bool IsOpen(string model) =>
        _breaker.FailureThreshold > 0 &&
        _consecutiveFailures.GetValueOrDefault(model) >= _breaker.FailureThreshold;

    private void RegisterFailure(string model)
    {
        var failures = _consecutiveFailures.GetValueOrDefault(model) + 1;
        _consecutiveFailures[model] = failures;

        if (failures == _breaker.FailureThreshold)
        {
            _logger.LogWarning("Предохранитель разомкнут для модели {Model}: неудач подряд {Count}", model, failures);
            _metrics.AddGuardrailEvent("circuit-breaker", $"{model}: разомкнут после {failures} неудач подряд");
        }
    }

    /// <summary>
    /// Повторять имеет смысл только сетевые сбои и временные отказы сервиса.
    /// На 401/403/404 ретрай — это трата времени и денег на заведомо тот же ответ.
    /// </summary>
    private static bool IsTransient(Exception e) => e switch
    {
        ClientResultException { Status: 0 or 408 or 409 or 425 or 429 or >= 500 } => true,
        ClientResultException => false,
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true,
        _ => e.InnerException is not null && IsTransient(e.InnerException)
    };

    private static string Short(string message) =>
        message.Length <= 120 ? message : message[..120] + "…";
}
