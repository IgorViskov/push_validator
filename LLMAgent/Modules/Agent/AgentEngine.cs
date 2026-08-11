using LLMAgent.Modules.ErrorsModule;
using LLMAgent.Modules.Logging;

namespace LLMAgent.Modules.Agent;

/// <summary>
/// Машина состояний: держит узлы графа и водит по ним контекст, пока очередное
/// состояние не вернёт завершение.
///
/// Раньше здесь была линейная цепочка middleware. Цепочка не умеет двух вещей, нужных
/// сценарию: выбрать, к какому из нескольких шагов перейти, и вернуться к уже пройденному
/// (арбитр отправляет анализ на второй проход). Поэтому переход задаёт само состояние.
/// </summary>
public sealed class AgentEngine
{
    /// <summary>
    /// Предохранитель от зацикливания. Граф допускает возвраты назад, и ошибка в условии
    /// перехода без ограничения означала бы бесконечный прогон с обращениями к моделям.
    /// </summary>
    private const int MaxTransitions = 12;

    private readonly IReadOnlyDictionary<string, IAgentState> _states;
    private readonly Logger _logger;

    public AgentEngine(IEnumerable<IAgentState> states, Logger logger)
    {
        _states = states.ToDictionary(state => state.Name, StringComparer.OrdinalIgnoreCase);
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
                return;
            }

            if (!_states.TryGetValue(current, out var state))
            {
                Errors.Rise<object>($"Состояние '{current}' не зарегистрировано в графе выполнения.");
            }

            var transition = await state.Run(context);

            context.Trace.Add(new TraceEntry(state.Name, transition.Next, transition.Reason));
            _logger.Info("Переход: {From} → {To} ({Reason})",
                state.Name, transition.Next ?? "конец", transition.Reason);

            current = transition.Next;
        }
    }
}

/// <summary>Пройденный шаг графа — из чего складывается схема реального прогона в отчёте.</summary>
public sealed record TraceEntry(string State, string? Next, string Reason);
