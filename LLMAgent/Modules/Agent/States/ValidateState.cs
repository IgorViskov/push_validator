using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Safety;
using LLMAgent.Prompts;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 4. Валидация: простые проверки по тексту — типы, соответствие аргументов, опечатки
/// в именах. Выполняется отдельной моделью и не видит находок ревьюера: два независимых
/// мнения нужны, чтобы арбитру было что сверять.
/// </summary>
public sealed class ValidateState : IAgentState
{
    private readonly CognitiveRouter _router;
    private readonly ModelGate _gate;
    private readonly FindingsGuard _findingsGuard;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public string Name => AgentStates.Validate;

    public ValidateState(
        CognitiveRouter router,
        ModelGate gate,
        FindingsGuard findingsGuard,
        RunMetrics metrics,
        Logger logger)
    {
        _router = router;
        _gate = gate;
        _findingsGuard = findingsGuard;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        var (chat, model) = _router.GetChat(CognitiveRoutingType.Validation);
        chat.AddMessage(Prompt.ValidationRequestFor(context.Diff));

        var result = await _gate.Ask(Name, model, chat,
            ct => chat.GetAnswer<AnalysisResult>(ct), context.CancellationToken);

        IReadOnlyList<Finding> findings;
        if (result is not null)
        {
            findings = _findingsGuard.Validate(Name, result.ToFindings(Stages.Validate), context.RepoPath);
        }
        else
        {
            _metrics.Degrade("этап валидации не дал разборчивого результата");
            findings =
            [
                new Finding(Severity.Critical, Stages.Validate,
                    "Этап валидации не дал разборчивого результата — пуш блокируется до ручной проверки.")
            ];
        }

        context.ReplaceFindings(Stages.Validate, findings);

        _logger.Info("Этап валидации: находок {Count}.", findings.Count);

        return AgentTransition.To(AgentStates.Arbiter, "оба мнения собраны");
    }
}
