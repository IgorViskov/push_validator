using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Prompts;

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
        chat.AddMessage(Prompt.OrchestrationRequestFor(context.Diff));

        var result = await chat.GetAnswer<OrchestrationResult>(cancellationToken);
        context.ComplexityScore = Math.Clamp(result?.ComplexityScore ?? 5, 1, 10);

        _logger.Info("Оркестратор оценил сложность изменений: {Score}/10", context.ComplexityScore);

        if (next is not null)
        {
            await next(null, context, cancellationToken);
        }
    }
}
