using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using ReviewAgent.CodeGraph.Retrieval;

namespace ReviewAgent.CodeGraph;

/// <summary>Что вернул граф на запрос контекста влияния.</summary>
/// <param name="Available">Пришёл ли пригодный к работе контекст.</param>
/// <param name="Chunks">Фрагменты кода: вызывающий код, реализации, регистрации.</param>
/// <param name="Error">
/// Причина отказа — попадает в отчёт, чтобы «связей не найдено» не выглядело как «граф молчит».
/// </param>
public sealed record ImpactResult(bool Available, IReadOnlyList<CodeChunk> Chunks, string? Error)
{
    public static ImpactResult Unavailable(string error) => new(false, [], error);
}

/// <summary>
/// Контекст влияния из графа кода: кто вызывает изменённые символы и что от них зависит.
///
/// До объединения проектов ревьюер получал это по сети: ставил задачу <c>context.search</c>
/// агенту-ретриверу соседнего решения и предъявлял подписанный токен с полномочием
/// <c>context:read</c>. Протокол был содержанием домашнего задания, а не потребностью
/// продукта: обе стороны жили на одной машине, владелец графа был один, и вся церемония
/// сводилась к вызову метода через HTTP с крюком SSE. После объединения это и есть вызов
/// метода — а вместе с транспортом ушли карточки агентов, подписи полномочий и деградация
/// «хост агентов не поднят».
/// </summary>
public sealed class ImpactSearch
{
    private readonly GraphRetriever _retriever;
    private readonly IGraphStore _store;
    private readonly ImpactOptions _options;
    private readonly ILogger<ImpactSearch> _logger;

    public ImpactSearch(
        GraphRetriever retriever,
        IGraphStore store,
        IOptions<ImpactOptions> options,
        ILogger<ImpactSearch> logger)
    {
        _retriever = retriever;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ImpactResult> FetchAsync(
        string repoKey, string query, CancellationToken ct)
    {
        IReadOnlyList<CodeSearchResult> results;
        try
        {
            // Один слот резервируем под блок связей между найденными символами.
            results = await _retriever.SearchAsync(repoKey, query, Math.Max(1, _options.MaxChunks - 1), ct);
        }
        catch (Exception ex) when (ex is Neo4jException or ServiceUnavailableException or IOException)
        {
            // Деградация, а не отказ: недоступный граф лишает анализ контекста влияния,
            // но проверка по диффу остаётся полноценным сценарием.
            _logger.LogWarning(ex, "Граф недоступен ({Message}) — контекст влияния не собран", ex.Message);
            return ImpactResult.Unavailable($"графовая база недоступна: {ex.Message}");
        }

        if (results.Count == 0)
            return ImpactResult.Unavailable("граф не вернул ни одного фрагмента");

        var chunks = new List<CodeChunk>(results.Count + 1);
        chunks.AddRange(results.Select(r => r.ToChunk()));

        var edges = await SafeEdgesAsync(results.Select(r => r.SymbolId).ToList(), ct);
        if (edges.Count > 0)
        {
            // Отдельным фрагментом отдаём связи между уже подключёнными символами: модель
            // видит структуру, а не мешок независимых кусков кода.
            chunks.Add(new CodeChunk(
                SymbolId: "graph:structure",
                Title: "связи между подключёнными символами",
                Text: string.Join('\n', edges),
                Score: results[0].Score,
                Rationale: "рёбра графа между фрагментами выше")
            {
                Kind = "GraphEdges",
                Language = null
            });
        }

        return new ImpactResult(true, chunks, null);
    }

    /// <summary>
    /// Запрос к графу по затронутым символам. Формулировка не косметическая: по словам
    /// «кто вызывает» разбор запроса разворачивает обход по входящим рёбрам CALLS, то есть
    /// ищет вызывающий код, а не сам изменённый метод.
    /// </summary>
    public string BuildQuery(IReadOnlyList<string> symbols, IReadOnlyList<string> files)
    {
        var named = string.Join(", ", symbols.Take(_options.MaxSymbols));
        var query = $"кто вызывает {named} и что сломается при изменении";

        // Пути дают графу дополнительные точки входа, когда имя символа слишком общее.
        var paths = files.Take(3).ToArray();
        if (paths.Length > 0) query += $" — файлы: {string.Join(", ", paths)}";

        return query;
    }

    private async Task<IReadOnlyList<string>> SafeEdgesAsync(
        IReadOnlyCollection<string> symbolIds, CancellationToken ct)
    {
        try
        {
            return await _retriever.DescribeEdgesAsync(symbolIds, ct);
        }
        catch (Exception ex) when (ex is Neo4jException or ServiceUnavailableException or IOException)
        {
            // Блок связей — дополнение к фрагментам, а не они сами: без него контекст беднее,
            // но пригоден. Ронять из-за него уже собранную выдачу нельзя.
            _logger.LogWarning("Блок связей не построен ({Message})", ex.Message);
            return [];
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => _store.IsAvailableAsync(ct);
}
