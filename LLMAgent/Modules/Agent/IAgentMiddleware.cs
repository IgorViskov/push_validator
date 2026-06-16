namespace LLMAgent.Modules.Agent;

public interface IAgentMiddleware
{
    Task Run(AgentEngineDelegate? next, LlmContext context, CancellationToken cancellationToken);
}