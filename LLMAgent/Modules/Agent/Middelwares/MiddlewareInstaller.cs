using Microsoft.Extensions.DependencyInjection;

namespace LLMAgent.Modules.Agent.Middelwares;

public static class MiddlewareInstaller
{
    /// <summary>
    /// Регистрирует фабрики middleware для пайплайна. Каждый шаг создаётся через
    /// ActivatorUtilities с пробросом следующего звена (AgentEngineDelegate next),
    /// поэтому из DI запрашивается именно Func&lt;AgentEngineDelegate, TStep&gt;.
    /// </summary>
    public static IServiceCollection AddMiddlewares(this IServiceCollection services)
    {
        Register<OrchestrationStep>(services);
        Register<ExecutionStep>(services);
        Register<ValidationStep>(services);
        Register<ReportStep>(services);

        return services;
    }

    private static void Register<TMiddleware>(IServiceCollection services)
        where TMiddleware : class, IAgentMiddleware =>
        services.AddSingleton<Func<AgentEngineDelegate, TMiddleware>>(sp =>
            next => ActivatorUtilities.CreateInstance<TMiddleware>(sp, next));
}
