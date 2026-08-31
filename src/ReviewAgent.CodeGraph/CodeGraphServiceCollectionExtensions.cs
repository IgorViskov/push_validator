using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReviewAgent.CodeGraph.Indexing;
using ReviewAgent.CodeGraph.Retrieval;

namespace ReviewAgent.CodeGraph;

public static class CodeGraphServiceCollectionExtensions
{
    /// <summary>Граф кода: доступ к базе, поиск влияния, индексация репозитория.</summary>
    public static IServiceCollection AddCodeGraph(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GraphOptions>(config.GetSection(GraphOptions.Section));
        services.Configure<RetrievalOptions>(config.GetSection(RetrievalOptions.Section));
        services.Configure<ImpactOptions>(config.GetSection(ImpactOptions.Section));

        services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        services.AddSingleton<GraphRetriever>();
        services.AddSingleton<ImpactSearch>();
        services.AddSingleton<RepositoryIndexer>();

        // Transient: экземпляр копит увиденные идентификаторы за один прогон индексации.
        services.AddTransient<CSharpExtractor>();

        return services;
    }
}
