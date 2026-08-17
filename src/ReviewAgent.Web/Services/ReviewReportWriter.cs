using System.Text;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Review;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Web.Services;

/// <summary>
/// Текстовый отчёт, который хук печатает разработчику в терминал.
///
/// Раньше эту печать делало состояние <c>report</c> прямо в консоль — агент и был консольной
/// утилитой. Теперь отчёт собирается на сервере и уезжает телом ответа: тот, кто делает пуш,
/// должен увидеть ровно то же, что видит админка, не открывая браузер.
///
/// Последняя строка — <c>REVIEW_DECISION=allowed|blocked</c>: это единственное, что хук
/// разбирает машинно. Всё остальное — для человека.
/// </summary>
public static class ReviewReportWriter
{
    public static string Build(ReviewResult result, string repositoryName, string? branch)
    {
        var report = new StringBuilder();

        report.AppendLine();
        report.AppendLine("════════════════ ReviewAgent: проверка перед пушем ════════════════");
        report.AppendLine($"Репозиторий: {repositoryName}{(branch is null ? "" : $"   Ветка: {branch}")}");
        report.AppendLine($"Сложность изменений: {result.ComplexityScore}/10");
        report.AppendLine($"Модель анализа: {result.ExecutionModel ?? "—"}");
        report.AppendLine($"Контекст влияния: {ImpactLabel(result)}");

        if (result.GraphStatus is { Length: > 0 } graph)
            report.AppendLine($"Граф кода: {graph}");

        report.AppendLine();
        AppendMetrics(report, result);
        AppendRoute(report, result);
        AppendFindings(report, result);

        report.AppendLine("══════════════════════════════════════════════════════════════════");

        if (result.Decision == ReviewDecision.Allowed)
        {
            report.AppendLine("Критических находок нет — пуш разрешён.");
        }
        else
        {
            report.AppendLine($"Критических находок: {result.CriticalCount}. Пуш остановлен.");
        }

        report.AppendLine();
        report.AppendLine($"REVIEW_DECISION={(result.Decision == ReviewDecision.Allowed ? "allowed" : "blocked")}");

        return report.ToString();
    }

    /// <summary>
    /// Три метрики прогона и сработавшие предохранители. Без них отчёт говорит только
    /// о коммите, но ничего — о самом агенте: во что обошлась проверка и сколько раз
    /// он споткнулся по дороге.
    /// </summary>
    private static void AppendMetrics(StringBuilder report, ReviewResult result)
    {
        var tokens = $"{result.InputTokens + result.OutputTokens} токенов";
        var cost = result.CostUsd > 0 ? $"${result.CostUsd:F4}, {tokens}" : $"{tokens}, цена не задана";

        report.AppendLine($"Результат прогона: {OutcomeLabel(result.Outcome)}");
        report.AppendLine($"Время: {result.Elapsed.TotalSeconds:F1} с   Стоимость: {cost}   " +
                          $"Обращений к моделям: {result.ModelCalls}   К графу: {result.GraphQueries}");

        if (result.AbortReason is { } abort)
            report.AppendLine($"Аварийная остановка: {abort}");

        foreach (var degradation in result.Degradations)
            report.AppendLine($"  · запасной путь: {degradation}");

        if (result.Guardrails.Count > 0)
        {
            var byKind = result.Guardrails
                .GroupBy(e => e.Kind)
                .Select(g => $"{g.Key}×{g.Count()}");

            report.AppendLine($"Предохранители: {string.Join(", ", byKind)}");
        }

        if (result.ToolCalls.Count > 0)
            report.AppendLine($"Инструменты: {string.Join(", ", result.ToolCalls.Select(t => $"{t.Key}×{t.Value}"))}");

        report.AppendLine();
    }

    /// <summary>
    /// Маршрут печатается всегда: по нему видно, какие ветки сработали в этом прогоне —
    /// пошёл ли сценарий в граф кода, был ли второй проход по требованию арбитра.
    /// </summary>
    private static void AppendRoute(StringBuilder report, ReviewResult result)
    {
        if (result.Route.Count == 0) return;

        report.AppendLine("Маршрут по графу состояний:");
        foreach (var entry in result.Route)
            report.AppendLine($"  {entry.State} → {entry.Next ?? "конец"}: {entry.Reason}");

        report.AppendLine();
    }

    private static void AppendFindings(StringBuilder report, ReviewResult result)
    {
        if (result.Findings.Count == 0)
        {
            report.AppendLine("Замечаний нет.");
            return;
        }

        foreach (var finding in result.Findings)
        {
            var location = string.IsNullOrWhiteSpace(finding.File) ? string.Empty : $"{finding.File}: ";
            report.AppendLine($"{Icon(finding.Severity)} [{finding.Stage}] {location}{finding.Message}");
        }
    }

    private static string OutcomeLabel(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Success => "успех — все этапы отработали штатно",
        RunOutcome.Degraded => "с деградацией — часть этапов работала на запасном пути",
        _ => "остановлен предохранителем"
    };

    private static string ImpactLabel(ReviewResult result) => result switch
    {
        { ImpactChunks: > 0, ImpactRequested: true } => $"{result.ImpactChunks} фрагм. из графа кода",
        // Быстрый путь мимо состояния impact ещё не значит, что графа не было:
        // ревьюер мог сходить туда сам инструментом graph_impact.
        { ImpactChunks: > 0 } => $"{result.ImpactChunks} фрагм. из графа кода (по запросу модели)",
        { ImpactRequested: true } => "запрошен, но не получен",
        _ => "не запрашивался"
    };

    private static string Icon(Severity severity) => severity switch
    {
        Severity.Critical => "[СТОП]",
        Severity.Warning => "[ВНИМ]",
        _ => "[инфо]"
    };
}
