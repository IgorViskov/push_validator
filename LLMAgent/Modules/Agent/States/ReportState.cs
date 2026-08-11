using LLMAgent.Models;
using LLMAgent.Modules.Safety;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 6. Отчёт: пройденный путь по графу, находки всех этапов и гейт пуша.
/// Последнее слово остаётся за человеком — критические находки приостанавливают пуш
/// до явного подтверждения.
/// </summary>
public sealed class ReportState : IAgentState
{
    private readonly RunMetrics _metrics;

    public string Name => AgentStates.Report;

    public ReportState(RunMetrics metrics)
    {
        _metrics = metrics;
    }

    public Task<AgentTransition> Run(LlmContext context)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════ РЕЗУЛЬТАТ ПРОВЕРКИ КОММИТА ════════════════");
        Console.WriteLine($"Сложность изменений: {context.ComplexityScore}/10");
        Console.WriteLine($"Модель анализа: {context.ExecutionModel?.Name ?? "—"}");
        Console.WriteLine($"Контекст влияния: {ImpactLabel(context)}");
        Console.WriteLine();

        PrintMetrics();
        PrintRoute(context);

        if (context.Findings.Count == 0)
        {
            Console.WriteLine("✅ Замечаний нет.");
        }
        else
        {
            foreach (var finding in context.Findings.OrderByDescending(f => f.Severity))
            {
                Console.WriteLine($"{Icon(finding.Severity)} [{finding.Stage}] {Location(finding)}{finding.Message}");
            }
        }

        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (!context.HasCritical)
        {
            context.AllowPush = true;
            Console.WriteLine("✅ Критических ошибок нет — пуш разрешён.");
            return Task.FromResult(AgentTransition.Finish("пуш разрешён"));
        }

        var criticalCount = context.Findings.Count(f => f.Severity == Severity.Critical);
        Console.WriteLine($"⛔ Найдено критических ошибок: {criticalCount}. Пуш приостановлен.");
        Console.Write("Разрешить пуш несмотря на критические ошибки? (y/N): ");
        var answer = Console.ReadLine()?.Trim();
        context.AllowPush = answer is not null &&
                            (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                             answer.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                             answer.Equals("да", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(context.AllowPush
            ? "⚠️  Пуш разрешён пользователем вручную."
            : "⛔ Пуш отклонён.");

        return Task.FromResult(AgentTransition.Finish(
            context.AllowPush ? "пуш разрешён пользователем вручную" : "пуш отклонён"));
    }

    /// <summary>
    /// Три метрики прогона и сработавшие предохранители. Без них отчёт говорит только
    /// о коммите, но ничего — о самом агенте: во что обошлась проверка и сколько раз
    /// он споткнулся по дороге.
    /// </summary>
    private void PrintMetrics()
    {
        var usage = $"{_metrics.TotalTokens} токенов";
        var cost = _metrics.CostUsd > 0 ? $"${_metrics.CostUsd:F4}, {usage}" : $"{usage}, цена не задана";

        Console.WriteLine($"Результат прогона: {OutcomeLabel(_metrics.Outcome)}");
        Console.WriteLine($"Время: {_metrics.Elapsed.TotalSeconds:F1} с   Стоимость: {cost}   " +
                          $"Обращений к моделям: {_metrics.ModelCallCount}");

        if (_metrics.AbortReason is { } abort)
        {
            Console.WriteLine($"Аварийная остановка: {abort}");
        }

        foreach (var degradation in _metrics.Degradations)
        {
            Console.WriteLine($"  · запасной путь: {degradation}");
        }

        if (_metrics.GuardrailEvents.Count > 0)
        {
            var byKind = _metrics.GuardrailEvents
                .GroupBy(e => e.Kind)
                .Select(g => $"{g.Key}×{g.Count()}");

            Console.WriteLine($"Предохранители: {string.Join(", ", byKind)}");
        }

        if (_metrics.ToolCalls.Count > 0)
        {
            Console.WriteLine($"Инструменты: {string.Join(", ", _metrics.ToolCalls.Select(t => $"{t.Key}×{t.Value}"))}");
        }

        Console.WriteLine();
    }

    private static string OutcomeLabel(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Success => "успех — все этапы отработали штатно",
        RunOutcome.Degraded => "с деградацией — часть этапов работала на запасном пути",
        _ => "остановлен предохранителем"
    };

    /// <summary>
    /// Маршрут печатается всегда: по нему видно, какие ветки сработали в этом прогоне —
    /// пошёл ли сценарий в граф кода, был ли второй проход по требованию арбитра.
    /// </summary>
    private static void PrintRoute(LlmContext context)
    {
        if (context.Trace.Count == 0) return;

        Console.WriteLine("Маршрут по графу состояний:");
        foreach (var entry in context.Trace)
        {
            Console.WriteLine($"  {entry.State} → {entry.Next ?? "конец"}: {entry.Reason}");
        }

        Console.WriteLine();
    }

    private static string ImpactLabel(LlmContext context) => context switch
    {
        { HasImpact: true, ImpactRequested: true } => $"{context.ImpactChunks.Count} фрагм. из графа кода",
        // Быстрый путь мимо состояния impact ещё не значит, что графа не было:
        // ревьюер мог сходить туда сам инструментом graph_impact.
        { HasImpact: true } => $"{context.ImpactChunks.Count} фрагм. из графа кода (по запросу модели)",
        { ImpactRequested: true } => "запрошен, но не получен",
        _ => "не запрашивался"
    };

    private static string Icon(Severity severity) => severity switch
    {
        Severity.Critical => "⛔",
        Severity.Warning => "⚠️ ",
        _ => "ℹ️ "
    };

    private static string Location(Finding finding)
        => string.IsNullOrWhiteSpace(finding.File) ? string.Empty : $"{finding.File}: ";
}
