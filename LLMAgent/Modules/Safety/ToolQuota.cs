using System.IO.Enumeration;
using LLMAgent.Models;
using LLMAgent.Modules.Logging;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Safety;

/// <summary>
/// Ограничения на инструменты. Инструмент — это то, чем модель действует наружу, поэтому
/// у него две проблемы: он может вызываться бесконечно (петля ReAct стоит денег и времени)
/// и может прочитать то, чего модели видеть не следует.
/// </summary>
public sealed class ToolQuota
{
    private readonly ToolLimitOptions _options;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public ToolQuota(IOptions<GuardrailOptions> options, RunMetrics metrics, Logger logger)
    {
        _options = options.Value.Tools;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Учитывает вызов инструмента и говорит, разрешён ли он. При отказе возвращается текст,
    /// который уходит модели вместо результата: она должна узнать про исчерпанную квоту
    /// и закончить анализ тем, что уже собрала, а не молча получить пустоту.
    /// </summary>
    public bool TryUse(string tool, out string refusal)
    {
        var limit = LimitFor(tool);
        var used = _metrics.ToolCallCount(tool);

        if (limit > 0 && used >= limit)
        {
            refusal = $"Квота инструмента {tool} исчерпана ({limit} вызовов за прогон). " +
                      "Заверши анализ на основе уже собранных данных.";

            _logger.Warn("Квота инструмента {Tool} исчерпана ({Limit}).", tool, limit);
            _metrics.AddGuardrailEvent("tool-quota", $"{tool}: отказано, исчерпан лимит {limit}");
            return false;
        }

        _metrics.AddToolCall(tool);
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Запрещён ли файл к чтению. Списки ключей и профили запуска лежат внутри репозитория
    /// и формально доступны инструменту; попав в промпт, они попадут и в текст находки,
    /// и в журнал прогона.
    /// </summary>
    public bool IsDenied(string path, out string reason)
    {
        var name = Path.GetFileName(path);

        foreach (var pattern in _options.DeniedFiles)
        {
            if (!FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) continue;

            reason = $"файл {name} закрыт для инструментов (маска {pattern})";
            _logger.Warn("Чтение запрещено: {Reason}", reason);
            _metrics.AddGuardrailEvent("denied-file", reason);
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private int LimitFor(string tool) => tool switch
    {
        "read_file" => _options.ReadFile,
        "search_files" => _options.SearchFiles,
        "git_log" => _options.GitLog,
        "graph_impact" => _options.GraphImpact,
        _ => 0
    };
}
