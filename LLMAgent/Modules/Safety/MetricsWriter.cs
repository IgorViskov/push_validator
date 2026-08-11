using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMAgent.Models;
using LLMAgent.Modules.Agent;
using LLMAgent.Modules.Logging;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Safety;

/// <summary>
/// Журнал прогона. Консоль показывает результат человеку здесь и сейчас, а файл нужен,
/// чтобы сравнивать прогоны между собой: подорожал ли анализ, стало ли больше отказов
/// моделей, как часто срабатывают предохранители.
/// </summary>
public sealed class MetricsWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Кириллица в журнале должна читаться глазами, а не как \u04xx.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly GuardrailOptions _options;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public MetricsWriter(IOptions<GuardrailOptions> options, RunMetrics metrics, Logger logger)
    {
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public string? Write(LlmContext context, int exitCode)
    {
        var record = new
        {
            conversationId = _metrics.ConversationId,
            repo = _metrics.RepoPath,
            startedAt = _metrics.StartedAt,

            // ── Три метрики, ради которых журнал и заводится ──
            outcome = _metrics.Outcome.ToString(),
            durationMs = Math.Round(_metrics.Elapsed.TotalMilliseconds),
            cost = new
            {
                usd = decimal.Round(_metrics.CostUsd, 6),
                inputTokens = _metrics.InputTokens,
                outputTokens = _metrics.OutputTokens
            },

            exitCode,
            allowPush = context.AllowPush,
            abortReason = _metrics.AbortReason,
            degradations = _metrics.Degradations,

            complexity = context.ComplexityScore,
            executionModel = context.ExecutionModel?.Name,
            impactChunks = context.ImpactChunks.Count,
            delegations = _metrics.Delegations,

            route = context.Trace.Select(t => new { t.State, next = t.Next ?? "конец", t.Reason }),
            states = _metrics.States.Select(s => new { s.State, elapsedMs = Math.Round(s.ElapsedMs) }),
            modelCalls = _metrics.ModelCalls.Select(c => new
            {
                c.Stage,
                c.Model,
                c.Attempts,
                elapsedMs = Math.Round(c.ElapsedMs),
                c.InputTokens,
                c.OutputTokens,
                costUsd = decimal.Round(c.CostUsd, 6),
                c.Outcome,
                c.Error
            }),
            toolCalls = _metrics.ToolCalls,
            guardrails = _metrics.GuardrailEvents.Select(e => new { e.Kind, e.Detail, atMs = Math.Round(e.AtMs) }),

            findings = new
            {
                critical = context.Findings.Count(f => f.Severity == Severity.Critical),
                warning = context.Findings.Count(f => f.Severity == Severity.Warning),
                info = context.Findings.Count(f => f.Severity == Severity.Info),
                items = context.Findings.Select(f => new { severity = f.Severity.ToString(), f.Stage, f.File, f.Message })
            }
        };

        try
        {
            var directory = Path.IsPathRooted(_options.MetricsPath)
                ? _options.MetricsPath
                : Path.Combine(AppContext.BaseDirectory, _options.MetricsPath);

            Directory.CreateDirectory(directory);

            var name = $"{_metrics.StartedAt:yyyyMMdd-HHmmss}-{_metrics.ConversationId}.json";
            var path = Path.Combine(directory, name);

            File.WriteAllText(path, JsonSerializer.Serialize(record, Json));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Журнал — не причина ронять проверку коммита.
            _logger.Warn("Журнал прогона не записан ({Error}).", e.Message);
            return null;
        }
    }
}
