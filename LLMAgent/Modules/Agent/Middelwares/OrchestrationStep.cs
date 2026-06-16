using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Parsing;
using LLMAgent.Modules.Router;

namespace LLMAgent.Modules.Agent.Middelwares;

/// <summary>
/// Шаг 1. Оркестратор оценивает когнитивную сложность изменений (1..10).
/// </summary>
public sealed class OrchestrationStep : IAgentMiddleware
{
    private readonly CognitiveRouter _router;
    private readonly Logger _logger;

    public OrchestrationStep(CognitiveRouter router, Logger logger)
    {
        _router = router;
        _logger = logger;
    }

    public async Task Run(AgentEngineDelegate? next, LlmContext context, CancellationToken cancellationToken)
    {
        var chat = _router.GetChat(CognitiveRoutingType.Orchestration);
        chat.AddMessage($"Git-дифф последних изменений:\n```diff\n{context.Diff}\n```");

        var answer = await chat.GetAnswer(cancellationToken);
        context.ComplexityScore = LlmJson.ParseComplexity(answer);

        _logger.Info("Оркестратор оценил сложность изменений: {Score}/10", context.ComplexityScore);

        if (next is not null)
        {
            await next(null, context, cancellationToken);
        }
    }
}
