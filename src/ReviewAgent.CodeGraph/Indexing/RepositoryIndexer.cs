using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReviewAgent.CodeGraph.Model;

namespace ReviewAgent.CodeGraph.Indexing;

/// <param name="Ok">Дошла ли индексация до записи в граф.</param>
/// <param name="Mode">full — весь репозиторий, incremental — только изменённые файлы.</param>
/// <param name="Error">Причина отказа: нет решения, нет SDK, недоступна база.</param>
public sealed record IndexResult(
    bool Ok,
    string Mode,
    int Projects,
    int Documents,
    int Nodes,
    int Edges,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    string? Error = null)
{
    public static IndexResult Failed(string mode, string error, TimeSpan elapsed) =>
        new(false, mode, 0, 0, 0, 0, elapsed, [], error);
}

/// <summary>
/// Наполнение графа кода по репозиторию на диске.
///
/// До объединения проектов это была отдельная консольная утилита, которую человек запускал
/// руками перед демонстрацией. Требование «агент сам поддерживает граф в актуальном
/// состоянии» превращает её в сервис: полная индексация — при подключении репозитория,
/// инкрементальная — на каждый пуш, по списку файлов из диффа.
/// </summary>
public sealed class RepositoryIndexer
{
    private readonly IGraphStore _store;
    private readonly IServiceProvider _services;
    private readonly ILogger<RepositoryIndexer> _logger;

    public RepositoryIndexer(
        IGraphStore store, IServiceProvider services, ILogger<RepositoryIndexer> logger)
    {
        _store = store;
        _services = services;
        _logger = logger;
    }

    /// <summary>Полная переиндексация: граф репозитория очищается и наполняется заново.</summary>
    public Task<IndexResult> IndexAllAsync(
        string repoKey, string repoPath, string? solutionPath, CancellationToken ct) =>
        RunAsync(repoKey, repoPath, solutionPath, changedFiles: null, ct);

    /// <summary>
    /// Инкрементальное обновление по изменённым файлам. Файлы указываются относительно
    /// корня репозитория — ровно так, как их отдаёт разбор диффа.
    /// </summary>
    public async Task<IndexResult> IndexChangedAsync(
        string repoKey,
        string repoPath,
        string? solutionPath,
        IReadOnlyCollection<string> changedFiles,
        CancellationToken ct)
    {
        var sources = changedFiles
            .Select(Normalize)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sources.Count == 0)
        {
            _logger.LogInformation("Инкрементальная индексация не нужна: в диффе нет файлов .cs");
            return new IndexResult(true, "skipped", 0, 0, 0, 0, TimeSpan.Zero, []);
        }

        // Пустой граф инкрементально не наполнить: рёбра к неразобранным файлам осядут
        // заглушками, и «кто вызывает» вернёт узлы без тел.
        var stats = await SafeStatsAsync(repoKey, ct);
        if (stats.IsEmpty)
        {
            _logger.LogInformation("Граф репозитория {Repo} пуст — вместо инкрементального прогона полный", repoKey);
            return await IndexAllAsync(repoKey, repoPath, solutionPath, ct);
        }

        return await RunAsync(repoKey, repoPath, solutionPath, sources, ct);
    }

    /// <summary>Ищет файл решения, а при его отсутствии — единственный проект.</summary>
    public static string? FindSolution(string repoPath)
    {
        if (!Directory.Exists(repoPath)) return null;

        var solutions = Directory
            .EnumerateFiles(repoPath, "*.sln*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsUnderIgnoredDirectory(repoPath, f))
            // Решение в корне важнее вложенного: у вложенного обычно урезанный состав проектов.
            .OrderBy(f => f.Count(c => c is '/' or '\\'))
            .ToList();

        if (solutions.Count > 0) return solutions[0];

        return Directory
            .EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(f => !IsUnderIgnoredDirectory(repoPath, f));
    }

    private async Task<IndexResult> RunAsync(
        string repoKey,
        string repoPath,
        string? solutionPath,
        IReadOnlySet<string>? changedFiles,
        CancellationToken ct)
    {
        var mode = changedFiles is null ? "full" : "incremental";
        var started = Stopwatch.GetTimestamp();

        if (!MSBuildBootstrapper.IsReady)
            return IndexResult.Failed(mode,
                "MSBuild не зарегистрирован: в образе нет .NET SDK — разбор кода недоступен",
                Stopwatch.GetElapsedTime(started));

        if (!await _store.IsAvailableAsync(ct))
            return IndexResult.Failed(mode, "графовая база недоступна", Stopwatch.GetElapsedTime(started));

        var solution = solutionPath is { Length: > 0 } && File.Exists(solutionPath)
            ? solutionPath
            : FindSolution(repoPath);

        if (solution is null)
            return IndexResult.Failed(mode,
                $"в {repoPath} не найдено ни .sln, ни .csproj", Stopwatch.GetElapsedTime(started));

        try
        {
            var result = await ExtractAsync(repoKey, repoPath, solution, changedFiles, ct);
            _logger.LogInformation(
                "Индексация {Repo} ({Mode}) за {Seconds:0.0} c: {Projects} проектов, {Documents} файлов, " +
                "{Nodes} узлов, {Edges} рёбер",
                repoKey, mode, result.Elapsed.TotalSeconds,
                result.Projects, result.Documents, result.Nodes, result.Edges);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Индексация {Repo} сорвалась", repoKey);
            return IndexResult.Failed(mode, ex.Message, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Работа с Roslyn спрятана за методом с запретом инлайна: иначе JIT подтянет сборки
    /// MSBuild при компиляции вызывающего кода, и регистрация локатора опоздает.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<IndexResult> ExtractAsync(
        string repoKey,
        string repoPath,
        string solution,
        IReadOnlySet<string>? changedFiles,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        await _store.EnsureSchemaAsync(ct);

        if (changedFiles is null)
        {
            // Полный прогон начинается с очистки: иначе удалённые из репозитория символы
            // остались бы в графе навсегда и продолжали бы попадать в контекст влияния.
            await _store.ResetAsync(repoKey, ct);
        }

        // Экземпляр одноразовый — см. комментарий у CSharpExtractor.
        var extractor = _services.GetRequiredService<CSharpExtractor>();

        var report = await extractor.ExtractAsync(
            repoKey, solution, repoPath,
            async (batch, token) =>
            {
                // Инкрементальность: сперва снимаем всё, что порождали эти файлы раньше,
                // затем пишем заново. Полная индексация и обновление одного файла — один путь.
                await _store.DeleteBySourceFilesAsync(repoKey, batch.TouchedFiles, token);
                await _store.UpsertAsync(batch, token);
            },
            changedFiles, ct);

        // Полнотекстовый индекс наполняется асинхронно — без ожидания первый поиск пуст.
        await _store.EnsureSchemaAsync(ct);

        return new IndexResult(
            Ok: true,
            Mode: changedFiles is null ? "full" : "incremental",
            Projects: report.Projects,
            Documents: report.Documents,
            Nodes: report.Nodes,
            Edges: report.Edges,
            Elapsed: Stopwatch.GetElapsedTime(started),
            Warnings: report.Errors);
    }

    private async Task<GraphStats> SafeStatsAsync(string repoKey, CancellationToken ct)
    {
        try
        {
            return await _store.GetStatsAsync(repoKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Состав графа не прочитан ({Message})", ex.Message);
            return GraphStats.Empty;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static readonly string[] IgnoredDirectories =
        ["bin", "obj", "node_modules", ".git", "packages", "TestResults"];

    private static bool IsUnderIgnoredDirectory(string root, string file) =>
        Path.GetRelativePath(root, file)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
