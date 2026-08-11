using System.Text.Json.Serialization;

namespace LLMAgent.Models;

/// <summary>Структурированный ответ триажа.</summary>
public sealed class OrchestrationResult
{
    public int ComplexityScore { get; set; }
    public string? Reasoning { get; set; }

    /// <summary>
    /// Типы и методы, затронутые изменениями. По ним запрашивается контекст влияния
    /// в графе кода; текстовый разбор диффа даёт то же самое, но грубее.
    /// </summary>
    public List<string> ChangedSymbols { get; set; } = [];
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

/// <summary>
/// Ответ арбитра: что делать с каждой спорной находкой и хватило ли ему фактов.
/// </summary>
public sealed class ArbitrationResult
{
    public List<VerdictItem> Verdicts { get; set; } = [];

    /// <summary>Арбитру не хватает вызывающего кода — сценарий уходит на второй проход.</summary>
    public bool NeedsContext { get; set; }

    /// <summary>Что именно спросить у графа кода, если <see cref="NeedsContext"/>.</summary>
    public string? ContextQuery { get; set; }
}

public sealed class VerdictItem
{
    /// <summary>Номер находки в списке, который получил арбитр.</summary>
    public int Number { get; set; }

    /// <summary>confirmed — находка остаётся критической; rejected — снимается до Info.</summary>
    public string? Decision { get; set; }

    public string? Reason { get; set; }

    // Вычисляемое свойство не должно попасть в JSON-схему структурированного ответа:
    // иначе модель обязана заполнять поле, которое на разборе всё равно игнорируется.
    [JsonIgnore]
    public bool IsRejected =>
        Decision?.Trim().ToLowerInvariant() is "rejected" or "снято" or "отклонено";
}
