using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Parsing;
using LLMAgent.Modules.Router;

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
        chat.AddMessage($"Проверь по тексту следующий git-дифф:\n```diff\n{context.Diff}\n```");

        var answer = await chat.GetAnswer(cancellationToken);
        var findings = LlmJson.ParseFindings(answer, "Валидация");
        context.Findings.AddRange(findings);

        _logger.Info("Этап валидации нашёл находок: {Count}", findings.Count);

        if (next is not null)
        {
            await next(null, context, cancellationToken);
        }
    }
}
