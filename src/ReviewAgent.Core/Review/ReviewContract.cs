using ReviewAgent.Core.Models;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review;

/// <summary>Что просит проверить хук <c>pre-push</c>.</summary>
public sealed record ReviewRequest
{
    /// <summary>Ключ репозитория: им размечены узлы графа и по нему находится запись реестра.</summary>
    public required string RepoKey { get; init; }

    /// <summary>Путь к рабочему дереву, видимый агенту (внутри контейнера — точка монтирования).</summary>
    public required string RepoPath { get; init; }

    /// <summary>
    /// Дифф проверяемых изменений. Присылает хук: git стоит на машине разработчика, и именно
    /// его вывод — истина о том, что уходит в пуш. Если пусто, агент попробует собрать дифф
    /// сам по рабочему дереву — это путь для проверки из админки.
    /// </summary>
    public string Diff { get; init; } = string.Empty;

    /// <summary>Ветка, в которую идёт пуш, — только для отчёта и журнала.</summary>
    public string? Branch { get; init; }

    /// <summary>Файл решения, если он задан явно в реестре.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Обновлять ли граф кода перед анализом.</summary>
    public bool RefreshGraph { get; init; } = true;
}

public enum ReviewDecision
{
    /// <summary>Критических находок нет — пуш можно выполнять.</summary>
    Allowed,

    /// <summary>Есть критические находки — пуш останавливается до решения человека.</summary>
    Blocked
}

/// <summary>
/// Итог проверки. Раньше на этом месте был код выхода процесса и печать в консоль:
/// агент был консольной утилитой, которую запускал сам хук. Теперь агент — сервис, отчёт
/// уезжает по HTTP, а решение о пуше принимает хук по этому объекту.
/// </summary>
public sealed record ReviewResult
{
    public required ReviewDecision Decision { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<TraceEntry> Route { get; init; }

    public int ComplexityScore { get; init; }
    public string? ExecutionModel { get; init; }
    public IReadOnlyList<string> ChangedSymbols { get; init; } = [];

    /// <summary>Сколько фрагментов графа попало в анализ и запрашивались ли они вообще.</summary>
    public int ImpactChunks { get; init; }

    public bool ImpactRequested { get; init; }

    // ── Метрики прогона ──────────────────────────────────────────────────────────────

    public RunOutcome Outcome { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public int ModelCalls { get; init; }
    public int GraphQueries { get; init; }
    public string? AbortReason { get; init; }
    public IReadOnlyList<string> Degradations { get; init; } = [];
    public IReadOnlyList<GuardrailEvent> Guardrails { get; init; } = [];
    public IReadOnlyList<ModelCallMetric> ModelCallDetails { get; init; } = [];
    public IReadOnlyDictionary<string, int> ToolCalls { get; init; } = new Dictionary<string, int>();

    /// <summary>Что случилось с графом перед анализом — попадает в отчёт хука.</summary>
    public string? GraphStatus { get; init; }

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);

    /// <summary>Изменений не нашлось — проверять нечего, пуш разрешён без обращения к моделям.</summary>
    public static ReviewResult NothingToReview(string reason) => new()
    {
        Decision = ReviewDecision.Allowed,
        Findings = [],
        Route = [],
        Outcome = RunOutcome.Success,
        GraphStatus = reason
    };
}

/// <summary>Пройденный шаг графа — из чего складывается схема реального прогона в отчёте.</summary>
public sealed record TraceEntry(string State, string? Next, string Reason);
