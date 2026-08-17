using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Git;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Review;
using ReviewAgent.Core.Review.States;
using ReviewAgent.Core.Safety;
using ReviewAgent.Core.Tools;

namespace ReviewAgent.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Движок проверки пуша. Границы времени жизни здесь содержательны, а не декоративны:
    ///
    /// singleton — то, что читает конфигурацию и не помнит прогонов (роутер моделей, git);
    /// scoped — всё, что считает один прогон: расход, предохранители, квоты инструментов
    /// и сами состояния. Одна проверка = одна область DI. В консольной версии всё это было
    /// singleton, потому что процесс жил ровно одну проверку и умирал; для сервиса, который
    /// обслуживает несколько репозиториев, такая регистрация складывала бы бюджеты разных
    /// пушей в один счётчик.
    /// </summary>
    public static IServiceCollection AddReviewEngine(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));
        services.Configure<GuardrailOptions>(config.GetSection(GuardrailOptions.Section));
        services.Configure<HookOptions>(config.GetSection(HookOptions.Section));

        services.AddCodeGraph(config);

        services.AddSingleton<CognitiveRouter>();
        services.AddSingleton<GitService>();
        services.AddSingleton<GitHookInstaller>();

        // ── Состояние одного прогона ──────────────────────────────────────────────────
        services.AddScoped<RunMetrics>();
        services.AddScoped<RunGuard>();
        services.AddScoped<ModelGate>();
        services.AddScoped<FindingsGuard>();
        services.AddScoped<ToolQuota>();
        services.AddScoped<ImpactGateway>();
        services.AddScoped<RepoToolFactory>();
        services.AddScoped<ReviewEngine>();
        services.AddScoped<ReviewService>();

        services.AddStates();

        return services;
    }

    /// <summary>
    /// Регистрирует узлы графа выполнения. Порядок регистрации ничего не значит:
    /// маршрут задают переходы, которые возвращают сами состояния, а движок находит
    /// следующее состояние по имени.
    /// </summary>
    private static IServiceCollection AddStates(this IServiceCollection services)
    {
        services.AddScoped<IReviewState, TriageState>();
        services.AddScoped<IReviewState, ImpactState>();
        services.AddScoped<IReviewState, ReviewState>();
        services.AddScoped<IReviewState, ValidateState>();
        services.AddScoped<IReviewState, ArbiterState>();
        services.AddScoped<IReviewState, ReportState>();

        return services;
    }
}
