using LLMAgent.Models;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 6. Отчёт: пройденный путь по графу, находки всех этапов и гейт пуша.
/// Последнее слово остаётся за человеком — критические находки приостанавливают пуш
/// до явного подтверждения.
/// </summary>
public sealed class ReportState : IAgentState
{
    public string Name => AgentStates.Report;

    public Task<AgentTransition> Run(LlmContext context)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════ РЕЗУЛЬТАТ ПРОВЕРКИ КОММИТА ════════════════");
        Console.WriteLine($"Сложность изменений: {context.ComplexityScore}/10");
        Console.WriteLine($"Модель анализа: {context.ExecutionModel?.Name ?? "—"}");
        Console.WriteLine($"Контекст влияния: {ImpactLabel(context)}");
        Console.WriteLine();

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
