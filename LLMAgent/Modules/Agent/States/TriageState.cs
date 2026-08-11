using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Impact;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Prompts;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 1. Триаж: оценка когнитивной сложности изменений и список затронутых символов.
/// Здесь же первое ветвление графа — стоит ли ради этой правки поднимать контекст влияния.
/// </summary>
public sealed class TriageState : IAgentState
{
    private readonly CognitiveRouter _router;
    private readonly ImpactOptions _impact;
    private readonly Logger _logger;

    public string Name => AgentStates.Triage;

    public TriageState(CognitiveRouter router, IOptions<ImpactOptions> impact, Logger logger)
    {
        _router = router;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        var chat = _router.GetChat(CognitiveRoutingType.Orchestration);
        chat.AddMessage(Prompt.OrchestrationRequestFor(context.Diff));

        OrchestrationResult? result = null;
        try
        {
            result = await chat.GetAnswer<OrchestrationResult>(context.CancellationToken);

            // Пустой результат без исключения — модель ответила, но её ответ не лёг в схему.
            // Без отдельного сообщения это неотличимо от «модель оценила сложность в 5».
            if (result is null)
                _logger.Warn("Триаж: модель роли Orchestration вернула неразборчивый ответ — сложность по умолчанию.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn("Триаж недоступен ({Error}) — берём сложность по умолчанию.", e.Message);
        }

        context.ComplexityScore = Math.Clamp(result?.ComplexityScore ?? 5, 1, 10);
        CollectSymbols(context, result);

        _logger.Info("Триаж: сложность {Score}/10, затронутые символы: {Symbols}",
            context.ComplexityScore,
            context.ChangedSymbols.Count > 0 ? string.Join(", ", context.ChangedSymbols) : "не определены");

        // ── Ветвление: нужен ли внешний контекст влияния ──────────────────────────────
        if (!_impact.Enabled)
            return AgentTransition.To(AgentStates.Review, "оценка влияния выключена в конфигурации");

        if (context.ComplexityScore < _impact.ComplexityThreshold)
            return AgentTransition.To(AgentStates.Review,
                $"сложность {context.ComplexityScore} < порога {_impact.ComplexityThreshold} — быстрый путь без графа");

        if (context.ChangedSymbols.Count == 0)
            return AgentTransition.To(AgentStates.Review, "затронутых символов не найдено — спрашивать граф не о чем");

        return AgentTransition.To(AgentStates.Impact,
            $"сложность {context.ComplexityScore} ≥ {_impact.ComplexityThreshold} — нужен контекст влияния");
    }

    /// <summary>
    /// Символы собираются из двух источников: разбор диффа работает всегда, ответ модели
    /// точнее — она отличает изменённый контракт от случайно задетой строки.
    /// </summary>
    private static void CollectSymbols(LlmContext context, OrchestrationResult? result)
    {
        IEnumerable<string> candidates = DiffSymbols.Extract(context.Diff);
        if (result is not null) candidates = candidates.Concat(result.ChangedSymbols);

        foreach (var symbol in candidates)
        {
            var trimmed = symbol?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (context.ChangedSymbols.Contains(trimmed, StringComparer.Ordinal)) continue;

            context.ChangedSymbols.Add(trimmed);
        }
    }
}
