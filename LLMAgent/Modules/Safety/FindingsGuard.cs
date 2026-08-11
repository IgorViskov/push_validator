using System.Text.RegularExpressions;
using LLMAgent.Models;
using LLMAgent.Modules.Logging;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Safety;

/// <summary>
/// Строгая приёмка того, что вернула модель. Схема гарантирует форму ответа, но не его
/// осмысленность: модель отдаёт выдуманные пути к файлам, дубликаты одной мысли, пустые
/// строки и «простыни» вместо описания. Всё это идёт в отчёт, по которому человек решает
/// судьбу пуша, — поэтому находки проходят проверку до того, как попадут в контекст.
/// </summary>
public sealed partial class FindingsGuard
{
    private readonly OutputOptions _options;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public FindingsGuard(IOptions<GuardrailOptions> options, RunMetrics metrics, Logger logger)
    {
        _options = options.Value.Output;
        _metrics = metrics;
        _logger = logger;
    }

    public IReadOnlyList<Finding> Validate(string stage, IReadOnlyList<Finding> findings, string repoPath)
    {
        var accepted = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        var pathsCleared = 0;

        foreach (var finding in findings)
        {
            var message = Normalize(finding.Message);
            if (message.Length == 0)
            {
                dropped++;
                continue;
            }

            if (message.Length > _options.MaxMessageChars)
                message = message[.._options.MaxMessageChars] + "…";

            // Дубликат — это одна и та же мысль, переписанная другими словами лишь отчасти;
            // ловим точные повторы, их модель выдаёт чаще всего.
            if (!seen.Add($"{finding.Severity}|{message}"))
            {
                dropped++;
                continue;
            }

            var file = VerifyFile(finding.File, repoPath, ref pathsCleared);

            if (accepted.Count >= _options.MaxFindingsPerStage)
            {
                dropped++;
                continue;
            }

            accepted.Add(finding with { Message = message, File = file });
        }

        if (dropped > 0)
        {
            _logger.Warn("Проверка вывода ({Stage}): отброшено находок {Count} (пустые, дубликаты, сверх лимита).",
                stage, dropped);
            _metrics.AddGuardrailEvent("output-schema",
                $"{stage}: отброшено {dropped} находок (пустые/дубликаты/сверх лимита {_options.MaxFindingsPerStage})");
        }

        if (pathsCleared > 0)
        {
            _logger.Warn("Проверка вывода ({Stage}): у {Count} находок указан несуществующий файл — ссылка снята.",
                stage, pathsCleared);
            _metrics.AddGuardrailEvent("output-schema",
                $"{stage}: снято {pathsCleared} ссылок на несуществующие файлы");
        }

        return accepted;
    }

    /// <summary>
    /// Путь принимается, только если файл действительно есть в репозитории. Выдуманная
    /// ссылка хуже её отсутствия: по ней человек идёт проверять и не находит ничего.
    /// </summary>
    private string? VerifyFile(string? file, string repoPath, ref int cleared)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (!_options.VerifyFilePaths) return file;

        var relative = file.Trim().Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(repoPath, relative));

        // Выход за пределы репозитория — либо ошибка модели, либо попытка увести внимание.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        var inside = full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (inside && File.Exists(full)) return relative;

        cleared++;
        return null;
    }

    private static string Normalize(string message) =>
        WhitespaceRegex().Replace(message ?? string.Empty, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
