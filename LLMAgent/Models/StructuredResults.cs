namespace LLMAgent.Models;

/// <summary>Структурированный ответ оркестратора.</summary>
public sealed class OrchestrationResult
{
    public int ComplexityScore { get; set; }
    public string? Reasoning { get; set; }
}

/// <summary>Структурированный ответ этапа анализа/валидации — список находок.</summary>
public sealed class AnalysisResult
{
    public List<FindingItem> Findings { get; set; } = [];

    public IReadOnlyList<Finding> ToFindings(string stage) =>
        Findings
            .Where(item => !string.IsNullOrWhiteSpace(item.Message))
            .Select(item => new Finding(ParseSeverity(item.Severity), stage, item.Message!, item.File))
            .ToList();

    // severity приходит строкой, чтобы переносить отклонения регистра/языка без падения десериализации.
    private static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "critical" or "критично" or "критическая" => Severity.Critical,
        "warning" or "предупреждение" => Severity.Warning,
        _ => Severity.Info
    };
}

public sealed class FindingItem
{
    public string? Severity { get; set; }
    public string? Message { get; set; }
    public string? File { get; set; }
}
