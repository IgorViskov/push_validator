using Neo4j.Driver;
using ReviewAgent.CodeGraph.Model;

namespace ReviewAgent.CodeGraph;

/// <summary>
/// Доступ к графу. Cypher за этим интерфейсом остаётся, но точка подключения одна —
/// переезд на Memgraph (тот же Bolt) или FalkorDB меняет только реализацию.
///
/// Все операции записи и статистики принимают <c>repoKey</c>: агент обслуживает несколько
/// репозиториев одновременно, и переиндексация одного не должна задевать граф другого.
/// </summary>
public interface IGraphStore : IAsyncDisposable
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Отвечает ли база. Ложь означает деградацию, а не отказ проверки.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>Полная очистка графа одного репозитория — перед холодной индексацией.</summary>
    Task ResetAsync(string repoKey, CancellationToken ct);

    /// <summary>
    /// Снимает всё, что было порождено перечисленными файлами: рёбра — целиком,
    /// узлы — только оставшиеся без связей. Первый шаг инкрементального обновления.
    /// </summary>
    Task<int> DeleteBySourceFilesAsync(
        string repoKey, IReadOnlyCollection<string> files, CancellationToken ct);

    Task UpsertAsync(GraphBatch batch, CancellationToken ct);

    Task<IReadOnlyList<T>> ReadAsync<T>(
        string cypher, object? parameters, Func<IRecord, T> map, CancellationToken ct);

    Task<GraphStats> GetStatsAsync(string repoKey, CancellationToken ct);
}
