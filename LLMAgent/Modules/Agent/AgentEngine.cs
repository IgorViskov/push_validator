using System.Diagnostics;
using LLMAgent.Models;
using LLMAgent.Modules.ErrorsModule;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Safety;

namespace LLMAgent.Modules.Agent;

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
public sealed class AgentEngine
{
    /// <summary>
    /// Предохранитель от зацикливания. Граф допускает возвраты назад, и ошибка в условии
    /// перехода без ограничения означала бы бесконечный прогон с обращениями к моделям.
    /// </summary>
    private const int MaxTransitions = 12;

    private readonly IReadOnlyDictionary<string, IAgentState> _states;
    private readonly RunGuard _guard;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public AgentEngine(IEnumerable<IAgentState> states, RunGuard guard, RunMetrics metrics, Logger logger)
    {
        _states = states.ToDictionary(state => state.Name, StringComparer.OrdinalIgnoreCase);
        _guard = guard;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task Run(string startState, LlmContext context)
    {
        var current = startState;
        var transitions = 0;

        while (current is not null)
        {
            if (++transitions > MaxTransitions)
            {
                _logger.Warn("Достигнут предел переходов ({Limit}) — сценарий останавливается на '{State}'.",
                    MaxTransitions, current);
                _metrics.AddGuardrailEvent("transition-limit", $"достигнут предел переходов {MaxTransitions}");
                _metrics.Abort($"достигнут предел переходов ({MaxTransitions})");
                return;
            }

            if (!_states.TryGetValue(current, out var state))
            {
                Errors.Rise<object>($"Состояние '{current}' не зарегистрировано в графе выполнения.");
            }

            var started = Stopwatch.GetTimestamp();
            var transition = await Execute(state, context);
            _metrics.AddState(state.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            context.Trace.Add(new TraceEntry(state.Name, transition.Next, transition.Reason));
            _logger.Info("Переход: {From} → {To} ({Reason})",
                state.Name, transition.Next ?? "конец", transition.Reason);

            current = Redirect(state.Name, transition.Next, context);
        }
    }

    /// <summary>
    /// Выполняет состояние, не давая его сбою уронить прогон. Необработанное исключение
    /// внутри состояния означало бы выход без отчёта, без журнала и без объяснения, —
    /// а решение о пуше всё равно нужно принять, и принять его надо закрыто.
    /// </summary>
    private async Task<AgentTransition> Execute(IAgentState state, LlmContext context)
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
            _logger.Error(e, $"Состояние '{state.Name}' завершилось ошибкой.");
            _metrics.AddGuardrailEvent("state-failure", $"{state.Name}: {e.GetType().Name}: {e.Message}");
            _metrics.Degrade($"состояние {state.Name} завершилось ошибкой");

            context.ReplaceFindings(Stages.Engine, [
                new Finding(Severity.Critical, Stages.Engine,
                    $"Состояние «{state.Name}» завершилось ошибкой ({e.Message}). " +
                    "Проверка неполная — пуш блокируется до ручного разбора.")
            ]);

            // Сбой отчёта уже некуда сворачивать: печатать результат больше нечем.
            return state.Name == AgentStates.Report
                ? AgentTransition.Finish("отчёт не сформирован из-за ошибки")
                : AgentTransition.To(AgentStates.Report, "состояние завершилось ошибкой — переходим к отчёту");
        }
    }

    /// <summary>
    /// Пропускает переход дальше либо разворачивает его в отчёт, если предохранитель
    /// сработал. Отчёт печатается всегда: остановленный прогон — тоже результат,
    /// и человек должен увидеть, на чём он остановился.
    /// </summary>
    private string? Redirect(string from, string? next, LlmContext context)
    {
        if (next is null || next == AgentStates.Report) return next;
        if (!_guard.ShouldAbort(out var reason)) return next;

        _logger.Warn("Аварийная остановка: {Reason}. Маршрут свёрнут к отчёту.", reason);
        context.Trace.Add(new TraceEntry(from, AgentStates.Report, $"аварийная остановка: {reason}"));

        return AgentStates.Report;
    }
}

/// <summary>Пройденный шаг графа — из чего складывается схема реального прогона в отчёте.</summary>
public sealed record TraceEntry(string State, string? Next, string Reason);
