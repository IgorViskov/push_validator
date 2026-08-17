using System.Text.RegularExpressions;

namespace ReviewAgent.Core.Git;

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

    /// <summary>
    /// Менялась ли в диффе объявленная публично сигнатура: удалена или добавлена строка
    /// с объявлением <c>public</c>/<c>protected</c> члена либо типа.
    ///
    /// Появилось по итогам замеров на локальных моделях. Все они оценивают добавление
    /// параметра в один метод как сложность 2–3 — и по-своему правы: разобраться в самом
    /// изменении просто. Но порог обращения к графу стоял на этой же шкале, и правка,
    /// ломающая четырёх вызывающих, уходила по быстрому пути без проверки влияния.
    ///
    /// Сложность анализа и риск для вызывающего кода — разные оси. Здесь измеряется вторая:
    /// изменение публичного контракта отправляет сценарий в граф независимо от оценки.
    /// </summary>
    public static bool TouchesPublicSignature(string diff)
    {
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Заголовки файлов начинаются с тех же символов, но объявлений не содержат.
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length == 0 || line[0] is not ('+' or '-')) continue;
            if (PublicDeclarationRegex().IsMatch(line[1..])) return true;
        }

        return false;
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

    // Объявление публичного члена или типа: модификатор доступа, за которым следует
    // либо ключевое слово типа, либо сигнатура со скобками. Инициализация поля
    // (public const decimal X = 1;) сюда тоже попадает — и правильно: константа,
    // на которую ссылаются извне, такой же контракт.
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|protected internal|protected)\s+"
                    + @"(?:[\w<>\[\],\?\.\s]*\b(?:class|interface|record|struct|enum)\b"
                    + @"|[\w<>\[\],\?\.]+(?:\s+\w+\s*[\(=;{]|\s*\w+\s*\())")]
    private static partial Regex PublicDeclarationRegex();

    [GeneratedRegex(@"^@@[^@]*@@\s*(.+)$")]
    private static partial Regex HunkTailRegex();

    [GeneratedRegex(@"^\+\+\+ (?:b/)?(.+)$")]
    private static partial Regex FileHeaderRegex();
}
