namespace LLMAgent.Modules.Agent;

public delegate Task AgentEngineDelegate(AgentEngineDelegate? next, LlmContext context, CancellationToken cancellationToken);

public sealed class AgentEngine
{
    private readonly AgentEngineDelegate _pipeline;

    public AgentEngine(IEnumerable<IAgentMiddleware> middlewares)
    {
        var steps = middlewares
            .Select<IAgentMiddleware, AgentEngineDelegate>(x => x.Run)
            .ToList();

        // Сворачиваем конвейер справа налево: каждый шаг получает реальное продолжение `next`.
        AgentEngineDelegate pipeline = static (_, _, _) => Task.CompletedTask;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var current = steps[i];
            var next = pipeline;
            pipeline = (_, context, cancellationToken) => current(next, context, cancellationToken);
        }

        _pipeline = pipeline;
    }

    public Task Run(LlmContext context, CancellationToken cancellationToken)
        => _pipeline(null, context, cancellationToken);
}
