using Microsoft.Extensions.DependencyInjection;

namespace LLMAgent.Modules.Agent.States;

public static class StateInstaller
{
    /// <summary>
    /// Регистрирует узлы графа выполнения. Порядок регистрации ничего не значит:
    /// маршрут задают переходы, которые возвращают сами состояния, а движок находит
    /// следующее состояние по имени.
    /// </summary>
    public static IServiceCollection AddStates(this IServiceCollection services)
    {
        services.AddSingleton<IAgentState, TriageState>();
        services.AddSingleton<IAgentState, ImpactState>();
        services.AddSingleton<IAgentState, ReviewState>();
        services.AddSingleton<IAgentState, ValidateState>();
        services.AddSingleton<IAgentState, ArbiterState>();
        services.AddSingleton<IAgentState, ReportState>();

        return services;
    }
}
