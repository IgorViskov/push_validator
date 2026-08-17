using Microsoft.Extensions.Logging;
using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review;

/// <summary>
/// Единственная точка обращения к графу кода из сценария проверки. Обращений два —
/// состояние <c>impact</c> и инструмент <c>graph_impact</c> внутри ReAct, — и потолок
/// на их число должен быть общим: иначе петля «арбитр просит контекст → анализ снова
/// спрашивает граф» упирается только в терпение пользователя.
///
/// Раньше эту роль исполнял выход в чужую сеть агентов: там же считались делегирования
/// и выдавались подписанные токены. Сеть исчезла вместе со вторым приложением, потолок
/// остался — он ограничивал не сеть, а цикл рассуждения.
/// </summary>
public sealed class ImpactGateway
{
    private readonly ImpactSearch _search;
    private readonly RunGuard _guard;
    private readonly RunMetrics _metrics;
    private readonly ILogger<ImpactGateway> _logger;

    public ImpactGateway(
        ImpactSearch search,
        RunGuard guard,
        RunMetrics metrics,
        ILogger<ImpactGateway> logger)
    {
        _search = search;
        _guard = guard;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<ImpactResult> FetchAsync(ReviewContext context, string query)
    {
        if (!_guard.CanQueryGraph(out var limit))
        {
            _metrics.AddGuardrailEvent("graph-limit", $"запрос к графу отменён — {limit}");
            return ImpactResult.Unavailable(limit ?? "лимит обращений к графу исчерпан");
        }

        _metrics.AddGraphQuery();
        _logger.LogInformation("Запрос к графу #{Number}: «{Query}»", _metrics.GraphQueries, query);
        context.Progress.Graph($"запрос к графу #{_metrics.GraphQueries}: {query}");

        return await _search.FetchAsync(context.RepoKey, query, context.CancellationToken);
    }

    public string BuildQuery(IReadOnlyList<string> symbols, IReadOnlyList<string> files) =>
        _search.BuildQuery(symbols, files);
}
