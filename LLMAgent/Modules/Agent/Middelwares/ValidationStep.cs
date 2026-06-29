using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Prompts;

namespace LLMAgent.Modules.Agent.Middelwares;

/// <summary>
/// Шаг 3. Валидация: простые проверки по тексту — типы, соответствие аргументов, опечатки в именах.
/// </summary>
public sealed class ValidationStep : IAgentMiddleware
{
    private readonly CognitiveRouter _router;
    private readonly Logger _logger;
    private readonly AgentEngineDelegate _next; 

    public ValidationStep(AgentEngineDelegate next, CognitiveRouter router, Logger logger)
    {
        _router = router;
        _logger = logger;
        _next = next;
    }

    public async Task Run(LlmContext context)
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
            ? result.ToFindings("Валидация")
            : [new Finding(Severity.Critical, "Валидация",
                "Этап валидации не дал разборчивого результата — пуш блокируется до ручной проверки.")];
        context.Findings.AddRange(findings);

        _logger.Info("Этап валидации: находок {Count}.", findings.Count);

        await _next(context);
    }
}
