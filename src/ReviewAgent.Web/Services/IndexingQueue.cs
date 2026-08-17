using System.Collections.Concurrent;
using System.Threading.Channels;
using ReviewAgent.CodeGraph;
using ReviewAgent.CodeGraph.Indexing;
using ReviewAgent.Core.Review;
using ReviewAgent.Data;

namespace ReviewAgent.Web.Services;

/// <param name="State">queued | running | done | failed.</param>
public sealed record IndexingStatus(string RepositoryKey, string State, string Message, DateTimeOffset At);

/// <summary>
/// Фоновая полная индексация репозитория.
///
/// Полный разбор решения Roslyn'ом занимает от секунд до минут: держать на нём HTTP-запрос
/// из админки значит показывать пользователю крутилку и таймаут прокси. Поэтому подключение
/// репозитория возвращается сразу, а индексация уходит в очередь — и её ход виден в UI.
///
/// Очередь одна и обрабатывается по одному: MSBuildWorkspace держит открытым целое решение,
/// и два параллельных разбора крупных решений упираются в память раньше, чем в процессор.
/// </summary>
public sealed class IndexingQueue : BackgroundService
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<string, IndexingStatus> _statuses = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopes;
    private readonly RunActivityBus _bus;
    private readonly ILogger<IndexingQueue> _logger;

    public IndexingQueue(
        IServiceScopeFactory scopes, RunActivityBus bus, ILogger<IndexingQueue> logger)
    {
        _scopes = scopes;
        _bus = bus;
        _logger = logger;
    }

    public event Action? Changed;

    public void Enqueue(WatchedRepository repository)
    {
        Set(repository.Key, "queued", "поставлено в очередь на индексацию");
        _queue.Writer.TryWrite(repository.Id);
    }

    public IndexingStatus? StatusOf(string repositoryKey) =>
        _statuses.TryGetValue(repositoryKey, out var status) ? status : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var repositoryId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await IndexAsync(repositoryId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Сбой одной индексации не должен уносить обработчик очереди: следующий
                // репозиторий в очереди к нему отношения не имеет.
                _logger.LogError(ex, "Индексация репозитория {Id} сорвалась", repositoryId);
            }
        }
    }

    private async Task IndexAsync(int repositoryId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        var registry = scope.ServiceProvider.GetRequiredService<RepositoryRegistry>();
        var repository = await registry.FindAsync(repositoryId, ct);
        if (repository is null) return;

        var indexer = scope.ServiceProvider.GetRequiredService<RepositoryIndexer>();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        Set(repository.Key, "running", "разбираю решение и наполняю граф");
        _bus.Publish(new RunActivity(repository.Key,
            new ReviewProgressEvent(ReviewProgressKind.Graph, "полная индексация: разбираю решение")));

        var result = await indexer.IndexAllAsync(
            repository.Key, repository.Path, repository.SolutionPath, ct);

        if (!result.Ok)
        {
            repository.LastIndexError = result.Error;
            await registry.SaveAsync(ct);

            Set(repository.Key, "failed", result.Error ?? "индексация не удалась");
            _bus.Publish(new RunActivity(repository.Key,
                new ReviewProgressEvent(ReviewProgressKind.Warning, $"индексация не удалась: {result.Error}")));
            return;
        }

        var stats = await SafeStatsAsync(graph, repository.Key, ct);

        repository.GraphNodes = stats.Nodes;
        repository.GraphEdges = stats.Edges;
        repository.LastIndexedAt = DateTimeOffset.Now;
        repository.LastIndexError = result.Warnings.Count > 0 ? string.Join("; ", result.Warnings) : null;
        await registry.SaveAsync(ct);

        var message = $"граф готов: {stats.Nodes} узлов, {stats.Edges} рёбер " +
                      $"({result.Projects} проектов, {result.Documents} файлов, " +
                      $"{result.Elapsed.TotalSeconds:0.0} с)";

        Set(repository.Key, "done", message);
        _bus.Publish(new RunActivity(repository.Key,
            new ReviewProgressEvent(ReviewProgressKind.Graph, message)));
    }

    private static async Task<CodeGraph.Model.GraphStats> SafeStatsAsync(
        IGraphStore graph, string repoKey, CancellationToken ct)
    {
        try
        {
            return await graph.GetStatsAsync(repoKey, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return CodeGraph.Model.GraphStats.Empty;
        }
    }

    private void Set(string repositoryKey, string state, string message)
    {
        _statuses[repositoryKey] = new IndexingStatus(repositoryKey, state, message, DateTimeOffset.Now);
        Changed?.Invoke();
    }
}
