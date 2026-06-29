using Microsoft.Extensions.DependencyInjection;

namespace LLMAgent.Modules.Agent;

public delegate Task AgentEngineDelegate(LlmContext context);

public sealed class AgentEngine
{
    private readonly IServiceProvider _services;
    private readonly List<Func<AgentEngineDelegate, IAgentMiddleware>> _factories = [];
    private AgentEngineDelegate? _pipeline;

    public AgentEngine(IServiceProvider services)
    {
        _services = services;
    }

    public AgentEngine Use<TMiddleware>() where TMiddleware : IAgentMiddleware
    {
        Func<AgentEngineDelegate, TMiddleware> factory = _services.GetRequiredService<Func<AgentEngineDelegate, TMiddleware>>();
        _factories.Add(x => factory(x));
        return this;
    }

    public Task Run(LlmContext context)
    {
        _factories.Reverse();
        _pipeline = _ => Task.CompletedTask;
        foreach (var factory in _factories)
        {
            _pipeline = factory(_pipeline).Run;
        }
        
        return _pipeline(context);
    }
}
