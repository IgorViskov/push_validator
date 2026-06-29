using LLMAgent.Models;

namespace LLMAgent.Modules.Agent;

/// <summary>
/// Шина данных, которую middleware пайплайна последовательно наполняют:
/// дифф → оценка сложности → выбранная модель → найденные ошибки → решение.
/// </summary>
public sealed class LlmContext
{
    public required string RepoPath { get; init; }

    /// <summary>Git-дифф последних изменений, анализируемых агентом.</summary>
    public string Diff { get; set; } = string.Empty;

    /// <summary>Оценка когнитивной сложности изменений (1..10), выставляется оркестратором.</summary>
    public int ComplexityScore { get; set; }

    /// <summary>Модель, выбранная когнитивным роутером для этапа анализа.</summary>
    public ModelSetting? ExecutionModel { get; set; }

    /// <summary>Все находки со всех этапов.</summary>
    public List<Finding> Findings { get; } = [];

    /// <summary>Итоговое разрешение на пуш (false — приостановлен пользователем).</summary>
    public bool AllowPush { get; set; } = true;

    public bool HasCritical => Findings.Any(f => f.Severity == Severity.Critical);
    
    public CancellationToken CancellationToken { get; init;}
}
