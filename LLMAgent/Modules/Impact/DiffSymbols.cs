using System.Text.RegularExpressions;

namespace LLMAgent.Modules.Impact;

/// <summary>
/// Достаёт из диффа имена затронутых типов и методов — то, о чём осмысленно спрашивать
/// граф кода. Разбор текстовый и намеренно грубый: он нужен как опора на случай, когда
/// модель триажа недоступна, а её ответ дополняет этот список, а не заменяет.
/// </summary>
public static partial class DiffSymbols
{
    private const int Limit = 8;

    public static IReadOnlyList<string> Extract(string diff)
    {
        var found = new List<string>();

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Заголовки файлов дают пути, а не символы, — их разбирает Files.
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = Payload(line);
            if (payload is null) continue;

            foreach (Match match in TypeRegex().Matches(payload))
            {
                Add(found, match.Groups[1].Value);
            }

            foreach (Match match in MemberRegex().Matches(payload))
            {
                Add(found, match.Groups[1].Value);
            }

            if (found.Count >= Limit) break;
        }

        return found;
    }

    /// <summary>Пути файлов, затронутых диффом.</summary>
    public static IReadOnlyList<string> Files(string diff)
    {
        var files = new List<string>();

        foreach (var raw in diff.Split('\n'))
        {
            var match = FileHeaderRegex().Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;

            var path = match.Groups[1].Value.Trim();
            if (path is "/dev/null" || files.Contains(path, StringComparer.Ordinal)) continue;

            files.Add(path);
        }

        return files;
    }

    /// <summary>
    /// Часть строки, в которой имеет смысл искать объявления: содержимое изменённой строки
    /// либо хвост заголовка ханка — git дописывает туда сигнатуру объемлющего метода,
    /// и это единственный способ узнать её, не читая файл.
    /// </summary>
    private static string? Payload(string line)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            var match = HunkTailRegex().Match(line);
            return match.Success ? match.Groups[1].Value : null;
        }

        return line.Length > 0 && line[0] is '+' or '-' ? line[1..] : null;
    }

    private static void Add(List<string> found, string symbol)
    {
        if (symbol.Length < 3 || found.Count >= Limit) return;
        if (found.Contains(symbol, StringComparer.Ordinal)) return;

        found.Add(symbol);
    }

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeRegex();

    // Объявление отличаем от вызова по модификатору перед сигнатурой: без этого условия
    // в список лезет каждый Math.Clamp( из тела метода.
    [GeneratedRegex(@"\b(?:public|private|protected|internal|static|async|override|virtual|sealed|partial)"
                    + @"[\w\s<>,\[\]\?\.]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MemberRegex();

    [GeneratedRegex(@"^@@[^@]*@@\s*(.+)$")]
    private static partial Regex HunkTailRegex();

    [GeneratedRegex(@"^\+\+\+ (?:b/)?(.+)$")]
    private static partial Regex FileHeaderRegex();
}
