using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review;

/// <summary>
/// Машина состояний: держит узлы графа и водит по ним контекст, пока очередное
/// состояние не вернёт завершение.
///
/// Раньше здесь была линейная цепочка middleware. Цепочка не умеет двух вещей, нужных
/// сценарию: выбрать, к какому из нескольких шагов перейти, и вернуться к уже пройденному
/// (арбитр отправляет анализ на второй проход). Поэтому переход задаёт само состояние.
///
/// Здесь же живёт аварийная остановка: сработавший предохранитель перебивает выбранный
/// переход и сворачивает маршрут к отчёту. Проверка стоит именно в движке, а не в каждом
/// состоянии, — по той же причине, по которой все обращения к моделям идут через один шлюз.
/// </summary>
public sealed class ReviewEngine
{
    private readonly IReadOnlyDictionary<string, IReviewState> _states;
    private readonly RunGuard _guard;
    private readonly RunMetrics _metrics;
    private readonly GuardrailOptions _options;
    private readonly ILogger<ReviewEngine> _logger;

    public ReviewEngine(
        IEnumerable<IReviewState> states,
        RunGuard guard,
        RunMetrics metrics,
        IOptions<GuardrailOptions> options,
        ILogger<ReviewEngine> logger)
    {
        _states = states.ToDictionary(state => state.Name, StringComparer.OrdinalIgnoreCase);
        _guard = guard;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Run(string startState, ReviewContext context)
    {
        var current = startState;
        var transitions = 0;
        var limit = Math.Max(3, _options.MaxTransitions);

        while (current is not null)
        {
            if (++transitions > limit)
            {
                _logger.LogWarning("Достигнут предел переходов ({Limit}) — сценарий останавливается на '{State}'",
                    limit, current);
                _metrics.AddGuardrailEvent("transition-limit", $"достигнут предел переходов {limit}");
                _metrics.Abort($"достигнут предел переходов ({limit})");
                return;
            }

            if (!_states.TryGetValue(current, out var state))
                throw new InvalidOperationException($"Состояние '{current}' не зарегистрировано в графе выполнения.");

            context.Progress.Stage($"этап {state.Name}");

            var started = Stopwatch.GetTimestamp();
            var transition = await Execute(state, context);
            _metrics.AddState(state.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            context.Trace.Add(new TraceEntry(state.Name, transition.Next, transition.Reason));
            _logger.LogInformation("Переход: {From} → {To} ({Reason})",
                state.Name, transition.Next ?? "конец", transition.Reason);

            context.Progress.Report(new ReviewProgressEvent(ReviewProgressKind.Transition,
                $"{state.Name} → {transition.Next ?? "конец"}: {transition.Reason}"));

            current = Redirect(state.Name, transition.Next, context);
        }
    }

    /// <summary>
    /// Выполняет состояние, не давая его сбою уронить прогон. Необработанное исключение
    /// внутри состояния означало бы ответ хуку без отчёта, без журнала и без объяснения, —
    /// а решение о пуше всё равно нужно принять, и принять его надо закрыто.
    /// </summary>
    private async Task<StateTransition> Execute(IReviewState state, ReviewContext context)
    {
        try
        {
            return await state.Run(context);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Состояние '{State}' завершилось ошибкой", state.Name);
            _metrics.AddGuardrailEvent("state-failure", $"{state.Name}: {e.GetType().Name}: {e.Message}");
            _metrics.Degrade($"состояние {state.Name} завершилось ошибкой");

            context.ReplaceFindings(Stages.Engine, [
                new Finding(Severity.Critical, Stages.Engine,
                    $"Состояние «{state.Name}» завершилось ошибкой ({e.Message}). " +
                    "Проверка неполная — пуш блокируется до ручного разбора.")
            ]);

            // Сбой отчёта уже некуда сворачивать: собирать результат больше нечем.
            return state.Name == ReviewStates.Report
                ? StateTransition.Finish("отчёт не сформирован из-за ошибки")
                : StateTransition.To(ReviewStates.Report, "состояние завершилось ошибкой — переходим к отчёту");
        }
    }

    /// <summary>
    /// Пропускает переход дальше либо разворачивает его в отчёт, если предохранитель
    /// сработал. Отчёт формируется всегда: остановленный прогон — тоже результат,
    /// и человек должен увидеть, на чём он остановился.
    /// </summary>
    private string? Redirect(string from, string? next, ReviewContext context)
    {
        if (next is null || next == ReviewStates.Report) return next;
        if (!_guard.ShouldAbort(out var reason)) return next;

        _logger.LogWarning("Аварийная остановка: {Reason}. Маршрут свёрнут к отчёту", reason);
        context.Progress.Warn($"аварийная остановка: {reason}");
        context.Trace.Add(new TraceEntry(from, ReviewStates.Report, $"аварийная остановка: {reason}"));

        return ReviewStates.Report;
    }
}
