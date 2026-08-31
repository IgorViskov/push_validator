using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.CodeGraph;
using ReviewAgent.CodeGraph.Indexing;
using ReviewAgent.Core.Git;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review;

/// <summary>
/// Точка входа одной проверки: собрать дифф, привести граф кода в актуальное состояние,
/// прогнать сценарий по графу состояний, упаковать результат.
///
/// Живёт в области DI одного прогона: счётчики расхода, предохранители и квоты
/// инструментов — scoped, и два одновременных пуша не мешают друг другу.
/// </summary>
public sealed class ReviewService
{
    private readonly ReviewEngine _engine;
    private readonly GitService _git;
    private readonly RepositoryIndexer _indexer;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _impact;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        ReviewEngine engine,
        GitService git,
        RepositoryIndexer indexer,
        RunMetrics metrics,
        IOptions<ImpactOptions> impact,
        ILogger<ReviewService> logger)
    {
        _engine = engine;
        _git = git;
        _indexer = indexer;
        _metrics = metrics;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<ReviewResult> RunAsync(
        ReviewRequest request,
        IReviewProgress progress,
        CancellationToken cancellationToken)
    {
        var diff = request.Diff;

        // Пустой дифф в запросе — это проверка из админки, а не из хука: дифф собираем сами.
        if (string.IsNullOrWhiteSpace(diff))
        {
            if (!Directory.Exists(request.RepoPath))
                return ReviewResult.NothingToReview($"путь {request.RepoPath} недоступен агенту");

            if (!await _git.IsGitRepository(request.RepoPath, cancellationToken))
                return ReviewResult.NothingToReview($"{request.RepoPath} не является git-репозиторием");

            diff = await _git.GetLatestChanges(request.RepoPath, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(diff))
            return ReviewResult.NothingToReview("изменений для анализа не найдено — пуш разрешён");

        var context = new ReviewContext
        {
            Request = request,
            Diff = diff,
            CancellationToken = cancellationToken,
            Progress = progress
        };

        context.GraphStatus = await RefreshGraphAsync(context);

        // Дальше маршрут выбирают сами состояния: триаж решает, идти ли за контекстом
        // влияния, арбитр — нужен ли второй проход. Здесь задаётся только точка входа.
        await _engine.Run(ReviewStates.Triage, context);

        return Pack(context);
    }

    /// <summary>
    /// Приведение графа в актуальное состояние перед анализом. Без этого «кто вызывает»
    /// отвечает по коду, каким он был до проверяемых изменений, — и вывод о совместимости
    /// делается по устаревшим связям.
    ///
    /// Отказ индексации не отменяет проверку: анализ по диффу остаётся полноценным
    /// сценарием, просто без контекста влияния.
    /// </summary>
    private async Task<string?> RefreshGraphAsync(ReviewContext context)
    {
        if (!_impact.Enabled) return "граф кода выключен в настройках";
        if (!_impact.RefreshGraphBeforeReview || !context.Request.RefreshGraph)
            return "обновление графа отключено — контекст влияния может отставать от кода";

        if (!Directory.Exists(context.RepoPath))
            return $"путь {context.RepoPath} недоступен агенту — граф не обновлён";

        var changed = DiffSymbols.Files(context.Diff);
        if (changed.Count == 0) return "в диффе не распознаны пути файлов — граф не обновлён";

        context.Progress.Graph($"обновляю граф кода по {changed.Count} изменённым файлам");

        var result = await _indexer.IndexChangedAsync(
            context.RepoKey, context.RepoPath, context.Request.SolutionPath, changed,
            context.CancellationToken);

        if (!result.Ok)
        {
            _logger.LogWarning("Граф не обновлён: {Error}", result.Error);
            context.Progress.Warn($"граф не обновлён: {result.Error}");
            _metrics.Degrade($"граф кода не обновлён: {result.Error}");
            return $"граф не обновлён: {result.Error}";
        }

        if (result.Mode == "skipped")
            return "в диффе нет файлов .cs — граф обновлять не потребовалось";

        var status = $"граф обновлён ({result.Mode}): {result.Documents} файлов, " +
                     $"{result.Nodes} узлов, {result.Edges} рёбер за {result.Elapsed.TotalSeconds:0.0} с";

        context.Progress.Graph(status);

        foreach (var warning in result.Warnings)
        {
            context.Progress.Warn($"индексация: {warning}");
        }

        return status;
    }

    private ReviewResult Pack(ReviewContext context) => new()
    {
        Decision = context.AllowPush ? ReviewDecision.Allowed : ReviewDecision.Blocked,
        Findings = context.Findings.OrderByDescending(f => f.Severity).ToList(),
        Route = context.Trace,

        ComplexityScore = context.ComplexityScore,
        ExecutionModel = context.ExecutionModel?.Name,
        ChangedSymbols = context.ChangedSymbols,
        ImpactChunks = context.ImpactChunks.Count,
        ImpactRequested = context.ImpactRequested,
        GraphStatus = context.GraphStatus,

        Outcome = _metrics.Outcome,
        Elapsed = _metrics.Elapsed,
        InputTokens = _metrics.InputTokens,
        OutputTokens = _metrics.OutputTokens,
        CostUsd = _metrics.CostUsd,
        ModelCalls = _metrics.ModelCallCount,
        GraphQueries = _metrics.GraphQueries,
        AbortReason = _metrics.AbortReason,
        Degradations = _metrics.Degradations,
        Guardrails = _metrics.GuardrailEvents,
        ModelCallDetails = _metrics.ModelCalls,
        ToolCalls = _metrics.ToolCalls
    };
}
