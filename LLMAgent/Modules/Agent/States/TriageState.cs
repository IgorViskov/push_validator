using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Impact;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Safety;
using LLMAgent.Prompts;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 1. Триаж: проверка недоверенного входа, оценка когнитивной сложности изменений
/// и список затронутых символов. Здесь же первое ветвление графа — стоит ли ради этой
/// правки поднимать контекст влияния.
/// </summary>
public sealed class TriageState : IAgentState
{
    private readonly CognitiveRouter _router;
    private readonly ModelGate _gate;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _impact;
    private readonly Logger _logger;

    public string Name => AgentStates.Triage;

    public TriageState(
        CognitiveRouter router,
        ModelGate gate,
        RunMetrics metrics,
        IOptions<ImpactOptions> impact,
        Logger logger)
    {
        _router = router;
        _gate = gate;
        _metrics = metrics;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        // Дифф — это текст, который написал автор проверяемого коммита. Проверяем его
        // до того, как он попадёт в промпт: дальше он уже смешается с инструкциями.
        ScanForInjection(context);

        var (chat, model) = _router.GetChat(CognitiveRoutingType.Orchestration);
        chat.AddMessage(Prompt.OrchestrationRequestFor(context.Diff));

        var result = await _gate.Ask(Name, model, chat,
            ct => chat.GetAnswer<OrchestrationResult>(ct), context.CancellationToken);

        if (result is null)
        {
            _logger.Warn("Триаж без оценки модели — сложность по умолчанию.");
            _metrics.Degrade("триаж работал без оценки модели");
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
    /// Находка о попытке инъекции критическая и вносится предохранителем, а не моделью:
    /// вердикт о безопасности не должна выносить та модель, на которую и направлено
    /// воздействие. Арбитр этап <see cref="Stages.Security"/> не пересматривает.
    /// </summary>
    private void ScanForInjection(LlmContext context)
    {
        var hits = InjectionScanner.Scan(context.Diff);
        if (hits.Count == 0) return;

        var message = InjectionScanner.Describe(hits, "диффе");

        _logger.Warn("Подозрение на инъекцию в промпт: {Count} строк в диффе.", hits.Count);
        _metrics.AddInjection(hits.Count, $"дифф: {hits.Count} строк, адресованных модели");

        context.ReplaceFindings(Stages.Security, [
            new Finding(Severity.Critical, Stages.Security,
                $"{message}. Такие строки адресованы анализирующей модели, а не человеку, " +
                "и в исходном коде им не место — пуш остановлен до ручной проверки.")
        ]);
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
