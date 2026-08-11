using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
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
    private readonly Logger _logger;

    public string Name => AgentStates.Validate;

    public ValidateState(CognitiveRouter router, Logger logger)
    {
        _router = router;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        var chat = _router.GetChat(CognitiveRoutingType.Validation);
        chat.AddMessage(Prompt.ValidationRequestFor(context.Diff));

        AnalysisResult? result;
        try
        {
            result = await chat.GetAnswer<AnalysisResult>(context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn("Этап валидации: обращение к модели не удалось — {Error}.", e.Message);
            result = null;
        }

        IReadOnlyList<Finding> findings = result is not null
            ? result.ToFindings(Stages.Validate)
            : [new Finding(Severity.Critical, Stages.Validate,
                "Этап валидации не дал разборчивого результата — пуш блокируется до ручной проверки.")];

        context.ReplaceFindings(Stages.Validate, findings);

        _logger.Info("Этап валидации: находок {Count}.", findings.Count);

        return AgentTransition.To(AgentStates.Arbiter, "оба мнения собраны");
    }
}
