using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using ReviewAgent.CodeGraph.Model;

namespace ReviewAgent.CodeGraph;

/// <summary>
/// Реализация хранилища поверх Bolt. Тот же код работает с Memgraph — протокол и диалект
/// Cypher совместимы; для FalkorDB меняется только транспорт.
///
/// Драйвер не принимает <see cref="CancellationToken"/> ни в одном методе сессии: отмена
/// на стороне сервера выражается таймаутом транзакции (задан в docker-compose). Поэтому
/// токен проверяется здесь, на границах батчей, — этого достаточно, чтобы отмена прерывала
/// индексацию, не оставляя графа в промежуточном состоянии дольше одного батча.
/// </summary>
public sealed class Neo4jGraphStore : IGraphStore
{
    private const int BatchSize = 1000;

    private readonly IDriver _driver;
    private readonly GraphOptions _options;
    private readonly ILogger<Neo4jGraphStore> _logger;

    public Neo4jGraphStore(IOptions<GraphOptions> options, ILogger<Neo4jGraphStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _driver = GraphDatabase.Driver(
            _options.Uri, AuthTokens.Basic(_options.User, _options.Password));
    }

    // ── Схема ────────────────────────────────────────────────────────────────────────

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var statements = new List<string>
        {
            // Без уникального constraint каждый MERGE вырождается в полный скан.
            "CREATE CONSTRAINT symbol_id IF NOT EXISTS FOR (s:Symbol) REQUIRE s.id IS UNIQUE",
            "CREATE INDEX symbol_repo IF NOT EXISTS FOR (s:Symbol) ON (s.repo)",
            // Составной индекс: инкрементальное удаление всегда идёт «репозиторий + файлы».
            "CREATE INDEX symbol_repo_source IF NOT EXISTS FOR (s:Symbol) ON (s.repo, s.sourceFile)",
            "CREATE INDEX symbol_kind IF NOT EXISTS FOR (s:Symbol) ON (s.kind)",
            "CREATE INDEX symbol_name IF NOT EXISTS FOR (s:Symbol) ON (s.name)",
            "CREATE INDEX symbol_fqn IF NOT EXISTS FOR (s:Symbol) ON (s.fqn)",
            $"""
             CREATE FULLTEXT INDEX {GraphOptions.FullTextIndex} IF NOT EXISTS
             FOR (s:Symbol) ON EACH [s.name, s.nameTokens, s.fqn, s.signature, s.docComment, s.filePath]
             """
        };

        // Индекс по свойству ребра — иначе удаление «всех рёбер файла X» идёт полным перебором.
        statements.AddRange(EdgeKinds.All.Select(kind =>
            $"CREATE INDEX rel_{kind.ToLowerInvariant()}_source IF NOT EXISTS " +
            $"FOR ()-[r:{EdgeKinds.Validated(kind)}]-() ON (r.repo, r.sourceFile)"));

        await using var session = OpenSession();
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            await WriteAsync(session, statement, null);
        }

        // Полнотекстовый индекс наполняется асинхронно: без ожидания первый же поиск пуст.
        await WriteAsync(session, "CALL db.awaitIndexes(300)", null);

        _logger.LogInformation("Схема графа готова ({Count} объектов)", statements.Count);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            await _driver.VerifyConnectivityAsync();
            return true;
        }
        catch (Exception ex) when (ex is Neo4jException or ServiceUnavailableException
                                      or AuthenticationException or IOException)
        {
            _logger.LogDebug(ex, "Граф недоступен: {Message}", ex.Message);
            return false;
        }
    }

    public async Task ResetAsync(string repoKey, CancellationToken ct)
    {
        await using var session = OpenSession();

        // Батчами, чтобы не собирать всю транзакцию в памяти на большом графе.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var deleted = await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync("""
                    MATCH (s:Symbol {repo: $repo})
                    WITH s LIMIT 20000
                    DETACH DELETE s
                    RETURN count(s) AS n
                    """, new { repo = repoKey });

                return await ReadCountAsync(cursor);
            });

            if (deleted == 0) break;
        }

        _logger.LogInformation("Граф репозитория {Repo} очищен", repoKey);
    }

    // ── Инкрементальное обновление ───────────────────────────────────────────────────

    public async Task<int> DeleteBySourceFilesAsync(
        string repoKey, IReadOnlyCollection<string> files, CancellationToken ct)
    {
        if (files.Count == 0) return 0;

        var list = files.ToList();
        var removed = 0;

        await using var session = OpenSession();

        // 1. Рёбра, порождённые этими файлами. Удаляются только они: узел, объявленный
        //    в файле F, может быть целью ребра из файла G — то ребро остаётся за G.
        foreach (var kind in EdgeKinds.All)
        {
            ct.ThrowIfCancellationRequested();

            var cypher = $"""
                MATCH ()-[r:{EdgeKinds.Validated(kind)}]->()
                WHERE r.repo = $repo AND r.sourceFile IN $files
                DELETE r
                RETURN count(r) AS n
                """;

            removed += await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, new { repo = repoKey, files = list });
                return await ReadCountAsync(cursor);
            });
        }

        // 2. Узлы этих файлов, оставшиеся без связей.
        await WriteAsync(session, """
            MATCH (s:Symbol)
            WHERE s.repo = $repo AND s.sourceFile IN $files AND NOT (s)--()
            DELETE s
            """, new { repo = repoKey, files = list });

        return removed;
    }

    public async Task UpsertAsync(GraphBatch batch, CancellationToken ct)
    {
        await using var session = OpenSession();

        // Настоящие узлы затирают свойства; заглушки — только создают, чтобы не перебить
        // объявление символа данными, вычитанными из места его использования.
        var real = batch.Nodes.Where(n => !n.IsStub).Select(n => n.ToParameters(batch.RepoKey)).ToList();
        var stubs = batch.Nodes.Where(n => n.IsStub).Select(n => n.ToParameters(batch.RepoKey)).ToList();

        await RunBatchedAsync(session, real,
            "UNWIND $rows AS row MERGE (s:Symbol {id: row.id}) SET s += row.props", ct);

        await RunBatchedAsync(session, stubs,
            "UNWIND $rows AS row MERGE (s:Symbol {id: row.id}) ON CREATE SET s += row.props", ct);

        foreach (var group in batch.Edges.GroupBy(e => e.Kind, StringComparer.Ordinal))
        {
            var cypher = $$"""
                UNWIND $rows AS row
                MATCH (a:Symbol {id: row.from})
                MATCH (b:Symbol {id: row.to})
                MERGE (a)-[r:{{EdgeKinds.Validated(group.Key)}} {sourceFile: row.sourceFile}]->(b)
                SET r += row.props
                """;

            var rows = group.Select(e => e.ToParameters(batch.RepoKey)).ToList();
            await RunBatchedAsync(session, rows, cypher, ct);
        }
    }

    private static async Task RunBatchedAsync(
        IAsyncSession session, List<Dictionary<string, object>> rows, string cypher, CancellationToken ct)
    {
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var slice = rows.GetRange(offset, Math.Min(BatchSize, rows.Count - offset));
            await WriteAsync(session, cypher, new { rows = slice });
        }
    }

    // ── Чтение ───────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<T>> ReadAsync<T>(
        string cypher, object? parameters, Func<IRecord, T> map, CancellationToken ct)
    {
        await using var session = OpenSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = parameters is null
                ? await tx.RunAsync(cypher)
                : await tx.RunAsync(cypher, parameters);

            var result = new List<T>();
            while (await cursor.FetchAsync())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(map(cursor.Current));
            }

            return result;
        });
    }

    public async Task<GraphStats> GetStatsAsync(string repoKey, CancellationToken ct)
    {
        var nodes = await ReadAsync(
            """
            MATCH (s:Symbol {repo: $repo})
            RETURN s.kind AS kind, count(*) AS n ORDER BY n DESC
            """,
            new { repo = repoKey },
            r => (Kind: r["kind"].As<string?>() ?? "?", N: r["n"].As<long>()), ct);

        var edges = await ReadAsync(
            """
            MATCH ()-[r]->()
            WHERE r.repo = $repo
            RETURN type(r) AS kind, count(*) AS n ORDER BY n DESC
            """,
            new { repo = repoKey },
            r => (Kind: r["kind"].As<string>(), N: r["n"].As<long>()), ct);

        return new GraphStats(
            nodes.Sum(x => x.N),
            edges.Sum(x => x.N),
            nodes.ToDictionary(x => x.Kind, x => x.N),
            edges.ToDictionary(x => x.Kind, x => x.N));
    }

    // ── Служебное ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Пишущий запрос с обязательным <c>ConsumeAsync</c>: без него транзакция закрывается
    /// раньше, чем сервер досчитает результат, и часть записей может не примениться.
    /// </summary>
    private static Task WriteAsync(IAsyncSession session, string cypher, object? parameters) =>
        session.ExecuteWriteAsync(async tx =>
        {
            var cursor = parameters is null
                ? await tx.RunAsync(cypher)
                : await tx.RunAsync(cypher, parameters);

            await cursor.ConsumeAsync();
        });

    private static async Task<int> ReadCountAsync(IResultCursor cursor)
    {
        var count = 0;
        while (await cursor.FetchAsync())
            count += cursor.Current["n"].As<int>();

        return count;
    }

    private IAsyncSession OpenSession() =>
        _driver.AsyncSession(o => o.WithDatabase(_options.Database));

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();
}
