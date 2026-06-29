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
    private readonly AgentEngineDelegate _next; 

    public OrchestrationStep(AgentEngineDelegate next, CognitiveRouter router, Logger logger)
    {
        _router = router;
        _logger = logger;
        _next = next;
    }

    public async Task Run(LlmContext context)
    {
        var chat = _router.GetChat(CognitiveRoutingType.Orchestration);
        chat.AddMessage(Prompt.OrchestrationRequestFor(context.Diff));

        OrchestrationResult? result = null;
        try
        {
            result = await chat.GetAnswer<OrchestrationResult>(context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn("Оркестратор недоступен ({Error}) — берём сложность по умолчанию.", e.Message);
        }

        context.ComplexityScore = Math.Clamp(result?.ComplexityScore ?? 5, 1, 10);

        _logger.Info("Оркестратор оценил сложность изменений: {Score}/10", context.ComplexityScore);

        await _next(context);
    }
}
