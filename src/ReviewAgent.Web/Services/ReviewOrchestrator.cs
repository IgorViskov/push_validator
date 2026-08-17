using Microsoft.Extensions.Options;
using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Review;
using ReviewAgent.Data;

namespace ReviewAgent.Web.Services;

/// <param name="Run">Запись журнала — по её идентификатору открывается карточка прогона.</param>
public sealed record OrchestratedReview(ReviewResult Result, ReviewRunRecord Run);

/// <summary>
/// Обвязка одной проверки: своя область DI, журнал, обновление состава графа в реестре.
///
/// Область нужна не для красоты. Счётчики расхода, предохранители и квоты инструментов
/// живут ровно один прогон; в консольной версии этим занимался процесс, который умирал
/// после проверки, — здесь его роль исполняет <see cref="IServiceScope"/>. Без него
/// два одновременных пуша складывали бы бюджеты в один счётчик, и второй упирался бы
/// в предохранитель первого.
/// </summary>
public sealed class ReviewOrchestrator
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGraphStore _graph;
    private readonly ImpactOptions _impact;
    private readonly ILogger<ReviewOrchestrator> _logger;

    /// <summary>
    /// Один репозиторий — одна проверка за раз. Индексация графа пишет в общие узлы, и два
    /// параллельных прогона по одному репозиторию мешали бы друг другу на записи.
    /// </summary>
    private readonly SemaphoreSlim _globalGate = new(2, 2);

    private readonly Dictionary<string, SemaphoreSlim> _perRepository = new(StringComparer.Ordinal);

    public ReviewOrchestrator(
        IServiceScopeFactory scopes,
        IGraphStore graph,
        IOptions<ImpactOptions> impact,
        ILogger<ReviewOrchestrator> logger)
    {
        _scopes = scopes;
        _graph = graph;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<OrchestratedReview> RunAsync(
        WatchedRepository repository,
        string diff,
        string? branch,
        string source,
        IReviewProgress progress,
        CancellationToken ct)
    {
        var request = new ReviewRequest
        {
            RepoKey = repository.Key,
            RepoPath = repository.Path,
            Diff = diff,
            Branch = branch,
            SolutionPath = repository.SolutionPath,
            RefreshGraph = _impact.RefreshGraphBeforeReview
        };

        var repositoryGate = GateFor(repository.Key);

        await _globalGate.WaitAsync(ct);
        try
        {
            await repositoryGate.WaitAsync(ct);
            try
            {
                return await ExecuteAsync(repository, request, source, progress, ct);
            }
            finally
            {
                repositoryGate.Release();
            }
        }
        finally
        {
            _globalGate.Release();
        }
    }

    private async Task<OrchestratedReview> ExecuteAsync(
        WatchedRepository repository,
        ReviewRequest request,
        string source,
        IReviewProgress progress,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        var review = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var journal = scope.ServiceProvider.GetRequiredService<ReviewJournal>();
        var registry = scope.ServiceProvider.GetRequiredService<RepositoryRegistry>();

        var result = await review.RunAsync(request, progress, ct);

        // Запись реестра берём из области прогона: сущность из чужого контекста
        // EF не отследит, и обновление состава графа молча не сохранится.
        var tracked = await registry.FindByKeyAsync(repository.Key, ct) ?? repository;
        var run = await journal.WriteAsync(tracked, request, result, source, ct);

        await UpdateGraphStateAsync(registry, tracked, ct);

        _logger.LogInformation(
            "Проверка {Repo}: {Decision}, находок {Findings} (критических {Critical}), " +
            "{Seconds:0.0} c, {Tokens} токенов",
            repository.Name, result.Decision, result.Findings.Count, result.CriticalCount,
            result.Elapsed.TotalSeconds, result.InputTokens + result.OutputTokens);

        return new OrchestratedReview(result, run);
    }

    /// <summary>Состав графа после прогона — в админке это единственный признак «граф живой».</summary>
    private async Task UpdateGraphStateAsync(
        RepositoryRegistry registry, WatchedRepository repository, CancellationToken ct)
    {
        try
        {
            var stats = await _graph.GetStatsAsync(repository.Key, ct);
            repository.GraphNodes = stats.Nodes;
            repository.GraphEdges = stats.Edges;
            repository.LastIndexedAt = DateTimeOffset.Now;
            await registry.SaveAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug("Состав графа после прогона не прочитан: {Message}", e.Message);
        }
    }

    private SemaphoreSlim GateFor(string repoKey)
    {
        lock (_perRepository)
        {
            if (!_perRepository.TryGetValue(repoKey, out var gate))
                _perRepository[repoKey] = gate = new SemaphoreSlim(1, 1);

            return gate;
        }
    }
}
