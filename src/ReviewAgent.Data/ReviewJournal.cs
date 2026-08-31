using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Review;

namespace ReviewAgent.Data;

/// <summary>
/// Журнал прогонов: записывает результат проверки и отдаёт историю админке.
/// Три метрики, ради которых он существует, — исход, время и стоимость: по ним прогоны
/// сравниваются между собой, и без них об агенте можно судить только по последнему вердикту.
/// </summary>
public sealed class ReviewJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Кириллица в журнале должна читаться глазами, а не как \u04xx.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AgentDbContext _db;

    public ReviewJournal(AgentDbContext db) => _db = db;

    public async Task<ReviewRunRecord> WriteAsync(
        WatchedRepository repository,
        ReviewRequest request,
        ReviewResult result,
        string source,
        CancellationToken ct)
    {
        var record = new ReviewRunRecord
        {
            RepositoryId = repository.Id,
            StartedAt = DateTimeOffset.Now - result.Elapsed,
            DurationMs = (long)result.Elapsed.TotalMilliseconds,
            Decision = result.Decision.ToString(),
            Outcome = result.Outcome.ToString(),
            Branch = Trim(request.Branch, 200),
            Source = source,
            Complexity = result.ComplexityScore,
            ExecutionModel = Trim(result.ExecutionModel, 200),
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            CostUsd = result.CostUsd,
            ModelCalls = result.ModelCalls,
            GraphQueries = result.GraphQueries,
            ImpactChunks = result.ImpactChunks,
            DiffLines = CountLines(request.Diff),
            AbortReason = Trim(result.AbortReason, 500),
            GraphStatus = Trim(result.GraphStatus, 2000),
            FindingCount = result.Findings.Count,
            CriticalCount = result.CriticalCount,
            WarningCount = result.WarningCount,
            RouteJson = JsonSerializer.Serialize(result.Route, Json),
            GuardrailsJson = JsonSerializer.Serialize(result.Guardrails, Json),
            ModelCallsJson = JsonSerializer.Serialize(result.ModelCallDetails, Json),
            ToolCallsJson = JsonSerializer.Serialize(result.ToolCalls, Json),
            DegradationsJson = JsonSerializer.Serialize(result.Degradations, Json),
            Findings = result.Findings.Select(f => new ReviewFindingRecord
            {
                Severity = f.Severity.ToString(),
                Stage = Trim(f.Stage, 64) ?? string.Empty,
                File = Trim(f.File, 1000),
                Message = Trim(f.Message, 2000) ?? string.Empty
            }).ToList()
        };

        _db.Runs.Add(record);
        await _db.SaveChangesAsync(ct);

        return record;
    }

    public Task<List<ReviewRunRecord>> RecentAsync(int take, CancellationToken ct) =>
        _db.Runs
            .Include(run => run.Repository)
            .OrderByDescending(run => run.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<List<ReviewRunRecord>> ForRepositoryAsync(int repositoryId, int take, CancellationToken ct) =>
        _db.Runs
            .Where(run => run.RepositoryId == repositoryId)
            .OrderByDescending(run => run.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<ReviewRunRecord?> GetAsync(int id, CancellationToken ct) =>
        _db.Runs
            .Include(run => run.Findings)
            .Include(run => run.Repository)
            .FirstOrDefaultAsync(run => run.Id == id, ct);

    /// <summary>Разбор JSON-полей карточки прогона обратно в типы движка.</summary>
    public static IReadOnlyList<T> Read<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];

    public static IReadOnlyDictionary<string, int> ReadCounters(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, int>()
            : JsonSerializer.Deserialize<Dictionary<string, int>>(json, Json) ?? [];

    public static Severity ParseSeverity(string value) =>
        Enum.TryParse<Severity>(value, ignoreCase: true, out var parsed) ? parsed : Severity.Info;

    private static int CountLines(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.AsSpan().Count('\n') + 1;

    private static string? Trim(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
