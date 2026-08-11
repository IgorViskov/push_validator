using System.Text;
using LLmSeracher.Core.Context;

namespace LLMAgent.Modules.Impact;

/// <summary>
/// Превращает фрагменты из графа в блок промпта. Кроме кода в блок попадает
/// <see cref="ContextChunk.Rationale"/> — путь в графе, по которому фрагмент подключён:
/// без него модель видит набор чужих методов и не понимает, при чём они здесь.
/// </summary>
public static class ImpactRenderer
{
    public static string Render(IReadOnlyList<ContextChunk> chunks, int maxChars)
    {
        if (chunks.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("=== КОНТЕКСТ ВЛИЯНИЯ (граф кода, агент retriever) ===");

        var number = 0;
        foreach (var chunk in chunks)
        {
            var fragment = Fragment(++number, chunk);

            // Бюджет проверяем до записи: обрезанный посередине фрагмент кода
            // модель читает как синтаксическую ошибку.
            if (builder.Length + fragment.Length > maxChars)
            {
                builder.AppendLine($"…ещё {chunks.Count - number + 1} фрагм. не поместились в бюджет контекста");
                break;
            }

            builder.Append(fragment);
        }

        return builder.ToString();
    }

    /// <summary>Короткая сводка для консоли — по одной строке на фрагмент.</summary>
    public static IEnumerable<string> Summarize(IReadOnlyList<ContextChunk> chunks) =>
        chunks.Select(chunk => chunk.Location is { } location
            ? $"{chunk.Title} — {location}{Because(chunk)}"
            : $"{chunk.Title}{Because(chunk)}");

    private static string Fragment(int number, ContextChunk chunk)
    {
        var builder = new StringBuilder();

        builder.Append($"[{number}] {chunk.Title}");
        if (chunk.Location is { } location) builder.Append($" — {location}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(chunk.Rationale))
            builder.AppendLine($"    подключён: {chunk.Rationale}");

        // Блок связей — это текст «A CALLS B», а не код: в фенсе он читается хуже.
        if (chunk.Kind == "GraphEdges")
        {
            builder.AppendLine(chunk.Text.Trim());
        }
        else
        {
            builder.AppendLine($"```{chunk.Language ?? "csharp"}");
            builder.AppendLine(chunk.Text.Trim());
            builder.AppendLine("```");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string Because(ContextChunk chunk) =>
        string.IsNullOrWhiteSpace(chunk.Rationale) ? string.Empty : $" ({chunk.Rationale})";
}
