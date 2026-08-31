using System.IO.Enumeration;
using ReviewAgent.Core.Models;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace ReviewAgent.Core.Safety;

/// <summary>
/// Ограничения на инструменты. Инструмент — это то, чем модель действует наружу, поэтому
/// у него две проблемы: он может вызываться бесконечно (петля ReAct стоит денег и времени)
/// и может прочитать то, чего модели видеть не следует.
/// </summary>
public sealed class ToolQuota
{
    private readonly ToolLimitOptions _options;
    private readonly RunMetrics _metrics;
    private readonly ILogger<ToolQuota> _logger;

    public ToolQuota(IOptions<GuardrailOptions> options, RunMetrics metrics, ILogger<ToolQuota> logger)
    {
        _options = options.Value.Tools;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Исчерпан общий потолок вызовов инструментов за прогон. Дальше инструменты обязаны
    /// бросать исключение, а не отвечать текстом.
    /// </summary>
    public sealed class ExhaustedException(string message) : Exception(message);

    /// <summary>
    /// Учитывает вызов инструмента и говорит, разрешён ли он. При отказе по квоте одного
    /// инструмента возвращается текст, который уходит модели вместо результата: она должна
    /// узнать про исчерпанный лимит и закончить анализ тем, что уже собрала.
    /// </summary>
    /// <exception cref="ExhaustedException">
    /// Исчерпан общий потолок вызовов. Вежливый отказ строкой здесь уже не работает: модель
    /// читает его как повод попробовать ещё раз, и на проверке это дало 328 обращений подряд.
    /// Исключение же считается ошибкой инструмента, а три ошибки подряд завершают цикл ReAct
    /// силами самого FunctionInvokingChatClient.
    /// </exception>
    public bool TryUse(string tool, out string refusal)
    {
        var total = _metrics.ToolCalls.Values.Sum();

        if (_options.MaxTotalToolCalls > 0 && total >= _options.MaxTotalToolCalls)
        {
            var message = $"Исчерпан общий лимит вызовов инструментов за прогон " +
                          $"({_options.MaxTotalToolCalls}). Анализ должен быть завершён.";

            // Событие пишем один раз: иначе журнал прогона состоит из одной этой строки,
            // повторённой сотни раз, и в нём не видно ничего другого.
            if (!_totalReported)
            {
                _totalReported = true;
                _logger.LogWarning("Общий лимит вызовов инструментов исчерпан ({Limit})",
                    _options.MaxTotalToolCalls);
                _metrics.AddGuardrailEvent("tool-budget", message);
            }

            throw new ExhaustedException(message);
        }

        var limit = LimitFor(tool);
        var used = _metrics.ToolCallCount(tool);

        if (limit > 0 && used >= limit)
        {
            refusal = $"Квота инструмента {tool} исчерпана ({limit} вызовов за прогон). " +
                      "Заверши анализ на основе уже собранных данных.";

            // Тоже один раз на инструмент: сам факт важен, а его повторы — шум.
            if (_reported.Add(tool))
            {
                _logger.LogWarning("Квота инструмента {Tool} исчерпана ({Limit})", tool, limit);
                _metrics.AddGuardrailEvent("tool-quota", $"{tool}: отказано, исчерпан лимит {limit}");
            }

            return false;
        }

        _metrics.AddToolCall(tool);
        refusal = string.Empty;
        return true;
    }

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private bool _totalReported;

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
            _logger.LogWarning("Чтение запрещено: {Reason}", reason);
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
