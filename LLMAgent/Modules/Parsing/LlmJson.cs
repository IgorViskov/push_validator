using System.Text.Json;
using LLMAgent.Models;

namespace LLMAgent.Modules.Parsing;

/// <summary>
/// Толерантный парсер JSON-ответов LLM: вытаскивает объект даже из markdown-обёртки,
/// не падает на отклонениях формата.
/// </summary>
public static class LlmJson
{
    public static int ParseComplexity(string text, int fallback = 5)
    {
        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("complexityScore", out var score))
            {
                var value = score.ValueKind == JsonValueKind.Number
                    ? score.GetInt32()
                    : int.TryParse(score.GetString(), out var parsed) ? parsed : fallback;
                return Math.Clamp(value, 1, 10);
            }
        }
        catch (JsonException)
        {
            // формат не распознан — используем fallback
        }

        return fallback;
    }

    public static IReadOnlyList<Finding> ParseFindings(string text, string stage)
    {
        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("findings", out var findings) ||
                findings.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<Finding>();
            foreach (var item in findings.EnumerateArray())
            {
                var message = item.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var severity = ParseSeverity(item.TryGetProperty("severity", out var s) ? s.GetString() : null);
                var file = item.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()
                    : null;

                result.Add(new Finding(severity, stage, message, file));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "critical" or "критично" or "критическая" => Severity.Critical,
        "warning" or "предупреждение" => Severity.Warning,
        _ => Severity.Info
    };

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
