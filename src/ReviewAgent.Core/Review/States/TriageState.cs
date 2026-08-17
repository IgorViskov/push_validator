using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Git;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Prompts;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review.States;

/// <summary>
/// Шаг 1. Триаж: проверка недоверенного входа, оценка когнитивной сложности изменений
/// и список затронутых символов. Здесь же первое ветвление графа — стоит ли ради этой
/// правки поднимать контекст влияния.
/// </summary>
public sealed class TriageState : IReviewState
{
    private readonly CognitiveRouter _router;
    private readonly ModelGate _gate;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _impact;
    private readonly ILogger<TriageState> _logger;

    public string Name => ReviewStates.Triage;

    public TriageState(
        CognitiveRouter router,
        ModelGate gate,
        RunMetrics metrics,
        IOptions<ImpactOptions> impact,
        ILogger<TriageState> logger)
    {
        _router = router;
        _gate = gate;
        _metrics = metrics;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<StateTransition> Run(ReviewContext context)
    {
        // Дифф — это текст, который написал автор проверяемого коммита. Проверяем его
        // до того, как он попадёт в промпт: дальше он уже смешается с инструкциями.
        ScanForInjection(context);

        var chat = _router.GetChat(CognitiveRole.Orchestration);
        chat.AddMessage(Prompt.OrchestrationRequestFor(context.Diff));

        var result = await _gate.Ask(Name, chat,
            ct => chat.GetAnswer<OrchestrationResult>(ct), context.CancellationToken);

        if (result is null)
        {
            _logger.LogWarning("Триаж без оценки модели — сложность по умолчанию");
            context.Progress.Warn("триаж: модель не ответила, сложность берётся по умолчанию");
            _metrics.Degrade("триаж работал без оценки модели");
        }

        context.ComplexityScore = Math.Clamp(result?.ComplexityScore ?? 5, 1, 10);
        CollectSymbols(context, result);

        var symbols = context.ChangedSymbols.Count > 0
            ? string.Join(", ", context.ChangedSymbols)
            : "не определены";

        _logger.LogInformation("Триаж: сложность {Score}/10, затронутые символы: {Symbols}",
            context.ComplexityScore, symbols);
        context.Progress.Info($"сложность {context.ComplexityScore}/10, символы: {symbols}");

        // ── Ветвление: нужен ли внешний контекст влияния ──────────────────────────────
        if (!_impact.Enabled)
            return StateTransition.To(ReviewStates.Review, "оценка влияния выключена в конфигурации");

        if (context.ChangedSymbols.Count == 0)
            return StateTransition.To(ReviewStates.Review, "затронутых символов не найдено — спрашивать граф не о чем");

        // Изменение публичного контракта идёт в граф независимо от оценки сложности:
        // разобраться в добавленном параметре просто (модели ставят 2–3), а сломать он
        // может всех вызывающих, которых в диффе нет. Это две разные оси, и порог
        // сложности вторую не измеряет.
        if (DiffSymbols.TouchesPublicSignature(context.Diff))
            return StateTransition.To(ReviewStates.Impact,
                "изменён публичный контракт — вызывающий код нужно проверить независимо от сложности");

        if (context.ComplexityScore < _impact.ComplexityThreshold)
            return StateTransition.To(ReviewStates.Review,
                $"сложность {context.ComplexityScore} < порога {_impact.ComplexityThreshold} — быстрый путь без графа");

        return StateTransition.To(ReviewStates.Impact,
            $"сложность {context.ComplexityScore} ≥ {_impact.ComplexityThreshold} — нужен контекст влияния");
    }

    /// <summary>
    /// Находка о попытке инъекции критическая и вносится предохранителем, а не моделью:
    /// вердикт о безопасности не должна выносить та модель, на которую и направлено
    /// воздействие. Арбитр этап <see cref="Stages.Security"/> не пересматривает.
    /// </summary>
    private void ScanForInjection(ReviewContext context)
    {
        var hits = InjectionScanner.Scan(context.Diff);
        if (hits.Count == 0) return;

        var message = InjectionScanner.Describe(hits, "диффе");

        _logger.LogWarning("Подозрение на инъекцию в промпт: {Count} строк в диффе", hits.Count);
        context.Progress.Warn($"в диффе {hits.Count} строк, обращённых к анализирующей модели");
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
    private static void CollectSymbols(ReviewContext context, OrchestrationResult? result)
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
