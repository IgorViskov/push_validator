using Microsoft.Extensions.Logging;
using ReviewAgent.Core.Git;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review.States;

/// <summary>
/// Шаг 2. Контекст влияния: у графа кода спрашивается, кто вызывает изменённые символы
/// и что от них зависит.
///
/// Состояние входное для двух рёбер графа: сюда приходят из триажа и повторно — из арбитра,
/// когда тому не хватило фактов о вызывающем коде.
/// </summary>
public sealed class ImpactState : IReviewState
{
    private readonly ImpactGateway _impact;
    private readonly RunMetrics _metrics;
    private readonly ILogger<ImpactState> _logger;

    public string Name => ReviewStates.Impact;

    public ImpactState(ImpactGateway impact, RunMetrics metrics, ILogger<ImpactState> logger)
    {
        _impact = impact;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<StateTransition> Run(ReviewContext context)
    {
        context.ImpactRequested = true;

        // Уточнение от арбитра одноразовое: иначе второй проход ушёл бы с тем же запросом,
        // что и первый, и вернул бы тот же контекст.
        var query = context.ImpactQuery
                    ?? _impact.BuildQuery(context.ChangedSymbols, DiffSymbols.Files(context.Diff));
        context.ImpactQuery = null;

        var result = await _impact.FetchAsync(context, query);

        if (!result.Available)
        {
            // Деградация, а не отказ: недоступный граф лишает анализ контекста влияния,
            // но проверка по диффу остаётся полноценной. Info-находка нужна, чтобы
            // «связей не найдено» в отчёте не путали с «граф молчит».
            _logger.LogWarning("Контекст влияния не получен ({Error}) — анализ пойдёт по одному диффу", result.Error);
            context.Progress.Warn($"граф не дал контекста влияния: {result.Error}");
            _metrics.Degrade($"контекст влияния не получен: {result.Error}");

            context.ReplaceFindings(Stages.Impact, [
                new Finding(Severity.Info, Stages.Impact,
                    $"Граф кода не ответил ({result.Error}) — влияние изменений на вызывающий код не проверено.")
            ]);

            return StateTransition.To(ReviewStates.Review, "граф недоступен — деградация до анализа по диффу");
        }

        // На втором проходе фрагменты добавляются к уже собранным: исходные улики
        // терять нельзя, арбитр уточнял запрос, а не отменял прошлый.
        var added = context.AddImpactChunks(result.Chunks);
        context.ReplaceFindings(Stages.Impact, []);

        _logger.LogInformation("Контекст влияния: получено {Total} фрагм., новых {Added}",
            result.Chunks.Count, added);

        foreach (var line in ImpactRenderer.Summarize(result.Chunks))
        {
            context.Progress.Graph($"← {line}");
        }

        return StateTransition.To(ReviewStates.Review,
            $"подключено фрагментов влияния: {context.ImpactChunks.Count}");
    }
}
