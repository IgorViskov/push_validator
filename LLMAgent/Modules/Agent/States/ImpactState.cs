using LLMAgent.Models;
using LLMAgent.Modules.Impact;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Safety;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 2. Передача задачи в чужую сеть агентов: у агента <c>retriever</c> запрашивается
/// контекст влияния — кто вызывает изменённые символы и что от них зависит.
///
/// Состояние входное для двух рёбер графа: сюда приходят из триажа и повторно — из арбитра,
/// когда тому не хватило фактов о вызывающем коде.
/// </summary>
public sealed class ImpactState : IAgentState
{
    private readonly ImpactService _impact;
    private readonly RunMetrics _metrics;
    private readonly Logger _logger;

    public string Name => AgentStates.Impact;

    public ImpactState(ImpactService impact, RunMetrics metrics, Logger logger)
    {
        _impact = impact;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        context.ImpactRequested = true;

        // Уточнение от арбитра одноразовое: иначе второй проход ушёл бы с тем же запросом,
        // что и первый, и вернул бы тот же контекст.
        var query = context.ImpactQuery ?? _impact.BuildQuery(context.ChangedSymbols, DiffSymbols.Files(context.Diff));
        context.ImpactQuery = null;

        var result = await _impact.Fetch(query, context.ConversationId, context.CancellationToken);

        if (!result.Available)
        {
            // Деградация, а не отказ: недоступный граф лишает анализ контекста влияния,
            // но проверка по диффу остаётся полноценной. Info-находка нужна, чтобы
            // «связей не найдено» в отчёте не путали с «граф молчит».
            _logger.Warn("Контекст влияния не получен ({Error}) — анализ пойдёт по одному диффу.", result.Error);
            _metrics.Degrade($"контекст влияния не получен: {result.Error}");
            context.ReplaceFindings(Stages.Impact, [
                new Finding(Severity.Info, Stages.Impact,
                    $"Граф кода не ответил ({result.Error}) — влияние изменений на вызывающий код не проверено.")
            ]);

            return AgentTransition.To(AgentStates.Review, "граф недоступен — деградация до анализа по диффу");
        }

        // На втором проходе фрагменты добавляются к уже собранным: исходные улики
        // терять нельзя, арбитр уточнял запрос, а не отменял прошлый.
        var added = context.AddImpactChunks(result.Chunks);

        context.ReplaceFindings(Stages.Impact, []);

        _logger.Info("Контекст влияния: получено {Total} фрагм. от {Endpoint}, новых {Added}",
            result.Chunks.Count, _impact.Endpoint, added);

        foreach (var line in ImpactRenderer.Summarize(result.Chunks))
        {
            _logger.Info("  ← {Fragment}", line);
        }

        return AgentTransition.To(AgentStates.Review, $"подключено фрагментов влияния: {context.ImpactChunks.Count}");
    }
}
