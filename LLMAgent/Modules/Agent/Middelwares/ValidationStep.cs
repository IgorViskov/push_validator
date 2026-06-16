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

    public ValidationStep(CognitiveRouter router, Logger logger)
    {
        _router = router;
        _logger = logger;
    }

    public async Task Run(AgentEngineDelegate? next, LlmContext context, CancellationToken cancellationToken)
    {
        var chat = _router.GetChat(CognitiveRoutingType.Validation);
        chat.AddMessage(Prompt.ValidationRequestFor(context.Diff));

        var result = await chat.GetAnswer<AnalysisResult>(cancellationToken);
        var findings = result?.ToFindings("Валидация") ?? [];
        context.Findings.AddRange(findings);

        _logger.Info("Этап валидации нашёл находок: {Count}", findings.Count);

        if (next is not null)
        {
            await next(null, context, cancellationToken);
        }
    }
}
