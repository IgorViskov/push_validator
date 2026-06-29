using LLMAgent.Models;

namespace LLMAgent.Modules.Agent.Middelwares;

/// <summary>
/// Шаг 4. Показывает все находки пользователю. Если есть критические —
/// пуш приостанавливается до явного решения пользователя.
/// </summary>
public sealed class ReportStep : IAgentMiddleware
{
    private AgentEngineDelegate _next;
    
    public ReportStep(AgentEngineDelegate next)
    {
        _next = next;
    }

    public Task Run(LlmContext context)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════ РЕЗУЛЬТАТ ПРОВЕРКИ КОММИТА ════════════════");
        Console.WriteLine($"Сложность изменений: {context.ComplexityScore}/10");
        Console.WriteLine($"Модель анализа: {context.ExecutionModel?.Name ?? "—"}");
        Console.WriteLine();

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
            return _next(context);
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

        return _next(context);
    }

    private static string Icon(Severity severity) => severity switch
    {
        Severity.Critical => "⛔",
        Severity.Warning => "⚠️ ",
        _ => "ℹ️ "
    };

    private static string Location(Finding finding)
        => string.IsNullOrWhiteSpace(finding.File) ? string.Empty : $"{finding.File}: ";
}
