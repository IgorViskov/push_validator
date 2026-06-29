namespace LLMAgent.Modules.Agent;

public interface IAgentMiddleware
{
    Task Run(LlmContext context);
}