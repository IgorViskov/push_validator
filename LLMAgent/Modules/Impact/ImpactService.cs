using LLMAgent.Models;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Safety;
using LLmSeracher.Core.A2A;
using LLmSeracher.Core.Agents;
using LLmSeracher.Core.Context;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Impact;

/// <summary>Что вернула сеть агентов на запрос контекста влияния.</summary>
/// <param name="Available">Пришёл ли пригодный к работе контекст.</param>
/// <param name="Chunks">Фрагменты кода: вызывающий код, реализации, регистрации.</param>
/// <param name="Error">Причина отказа — попадает в отчёт, чтобы «граф молчит» не выглядело как «связей нет».</param>
public sealed record ImpactResult(bool Available, IReadOnlyList<ContextChunk> Chunks, string? Error)
{
    public static ImpactResult Unavailable(string error) => new(false, [], error);
}

/// <summary>
/// Обращение к чужой сети агентов (LLmSeracher) за контекстом влияния.
///
/// Ревьюер не ходит в граф кода сам: он ставит задачу агенту <c>retriever</c> — владельцу
/// базы знаний — и предъявляет подписанный токен с полномочием <c>context:read</c>.
/// Транспорт, формат задачи и подпись переиспользуются из LLmSeracher.Core: дублировать
/// контракт значило бы получить два расходящихся протокола.
/// </summary>
public sealed class ImpactService
{
    private readonly IAgentClient _agents;
    private readonly DelegationService _delegation;
    private readonly RunGuard _guard;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _options;
    private readonly Logger _logger;

    public ImpactService(
        IAgentClient agents,
        DelegationService delegation,
        RunGuard guard,
        RunMetrics metrics,
        IOptions<ImpactOptions> options,
        Logger logger)
    {
        _agents = agents;
        _delegation = delegation;
        _guard = guard;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public string Endpoint => _agents.Endpoint;

    /// <summary>Отвечает ли хост агентов — проверяется по карточке ретривера.</summary>
    public async Task<bool> IsAvailable(CancellationToken cancellationToken)
    {
        var card = await _agents.GetCardAsync(AgentIds.Retriever, cancellationToken);
        return card is not null;
    }

    public async Task<ImpactResult> Fetch(string query, string conversationId, CancellationToken cancellationToken)
    {
        // Единственная точка выхода к чужим агентам — сюда же ставится потолок на число
        // делегирований. Без него петля «арбитр просит контекст → анализ снова спрашивает
        // граф» упирается только в терпение пользователя.
        if (!_guard.CanDelegate(out var limit))
        {
            _metrics.AddGuardrailEvent("delegation-limit", $"задача retriever отменена — {limit}");
            return ImpactResult.Unavailable(limit ?? "лимит делегирований исчерпан");
        }

        var task = AgentTask.Create(Skills.ContextSearch, query, conversationId);
        task = task with
        {
            Delegation = _delegation.Issue(AgentIds.Retriever, task, Scopes.ContextRead)
        };

        _metrics.AddDelegation();
        _logger.Info("Делегирование {Number}: retriever ← {Skill} [{Scope}] по запросу «{Query}»",
            _metrics.Delegations, Skills.ContextSearch, Scopes.ContextRead, query);

        var chunks = new List<ContextChunk>();
        string? error = null;

        try
        {
            await foreach (var evt in _agents.SendAsync(AgentIds.Retriever, task, cancellationToken))
            {
                switch (evt)
                {
                    case ContextAttachedEvent attached:
                        chunks.AddRange(attached.Chunks);
                        break;
                    case FailedEvent failed:
                        error = failed.Message;
                        break;
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Недоступный хост агентов не должен ронять проверку коммита: анализ по диффу
            // остаётся полноценным сценарием, просто без контекста влияния.
            error = e.Message;
        }

        if (error is not null) return ImpactResult.Unavailable(error);

        return chunks.Count > 0
            ? new ImpactResult(true, chunks, null)
            : ImpactResult.Unavailable("граф не вернул ни одного фрагмента");
    }

    /// <summary>
    /// Запрос к графу по затронутым символам. Формулировка не косметическая: по словам
    /// «кто вызывает» ретривер разворачивает обход по входящим рёбрам CALLS, то есть
    /// ищет вызывающий код, а не сам изменённый метод.
    /// </summary>
    public string BuildQuery(IReadOnlyList<string> symbols, IReadOnlyList<string> files)
    {
        var named = string.Join(", ", symbols.Take(_options.MaxSymbols));
        var query = $"кто вызывает {named} и что сломается при изменении";

        // Пути дают графу дополнительные точки входа, когда имя символа слишком общее.
        var paths = files.Take(3).ToArray();
        if (paths.Length > 0) query += $" — файлы: {string.Join(", ", paths)}";

        return query;
    }
}
