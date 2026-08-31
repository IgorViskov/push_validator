using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Prompts;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review.States;

/// <summary>
/// Шаг 5. Арбитраж. Ревьюер и валидатор работают независимо, и каждая их критическая
/// находка блокирует пуш. Складывать оба списка по «ИЛИ» — значит отдать решение тому,
/// кто мнительнее; поэтому спорные находки снимает или подтверждает отдельная роль.
///
/// Отсюда выходят два ребра графа: в отчёт с вердиктом и назад в <see cref="ImpactState"/>,
/// когда арбитру не хватает фактов о вызывающем коде.
/// </summary>
public sealed class ArbiterState : IReviewState
{
    private readonly CognitiveRouter _router;
    private readonly ModelGate _gate;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _impact;
    private readonly ILogger<ArbiterState> _logger;

    public string Name => ReviewStates.Arbiter;

    public ArbiterState(
        CognitiveRouter router,
        ModelGate gate,
        RunMetrics metrics,
        IOptions<ImpactOptions> impact,
        ILogger<ArbiterState> logger)
    {
        _router = router;
        _gate = gate;
        _metrics = metrics;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<StateTransition> Run(ReviewContext context)
    {
        // Находки предохранителей арбитражу не подлежат: их вынесла не модель, а проверка,
        // и снимать их предложением из того же недоверенного текста — замкнутый круг.
        var disputed = context.Findings
            .Where(finding => finding.Severity == Severity.Critical)
            .Where(finding => finding.Stage != Stages.Security)
            .ToList();

        // ── Ветвление: спорить не о чем ───────────────────────────────────────────────
        if (disputed.Count == 0)
        {
            _logger.LogInformation("Арбитраж: критических находок нет, оба этапа согласны");
            context.Progress.Info("арбитраж: критических находок нет — консенсус");
            return StateTransition.To(ReviewStates.Report, "критических находок нет — консенсус");
        }

        // Роль арбитра исполняет модель уровня Orchestration: это решение о решениях,
        // отдельной роли в конфиге для него заводить не пришлось.
        var chat = _router.GetChat(CognitiveRole.Orchestration, Prompt.WithTrustBoundary(Prompt.Arbitration));
        chat.AddMessage(Prompt.ArbitrationRequestFor(
            context.Diff,
            ImpactRenderer.Render(context.ImpactChunks, _impact.MaxContextChars),
            disputed));

        context.Progress.Info($"арбитраж {disputed.Count} спорных находок, модель {chat.Model.Name}");

        var verdict = await _gate.Ask(Name, chat,
            ct => chat.GetAnswer<ArbitrationResult>(ct), context.CancellationToken);

        if (verdict is null)
        {
            // Fail-closed: без вердикта критические находки остаются в силе.
            _logger.LogWarning("Арбитраж не состоялся — {Count} критических находок остаются в силе", disputed.Count);
            context.Progress.Warn("арбитр не ответил — критические находки остаются в силе");
            _metrics.Degrade("арбитраж не состоялся");
            return StateTransition.To(ReviewStates.Report, "арбитр не ответил — находки остаются в силе");
        }

        // ── Ветвление: арбитру не хватает вызывающего кода ────────────────────────────
        if (verdict.NeedsContext && CanAskGraph(context))
        {
            context.ArbiterRetries++;
            context.ImpactQuery = string.IsNullOrWhiteSpace(verdict.ContextQuery)
                ? null
                : verdict.ContextQuery.Trim();

            _logger.LogInformation("Арбитраж: не хватает фактов, повторный запрос к графу — «{Query}»",
                context.ImpactQuery ?? "запрос по затронутым символам");

            return StateTransition.To(ReviewStates.Impact, "арбитр запросил вызывающий код — второй проход");
        }

        var (confirmed, rejected) = Apply(verdict, disputed, context);

        _logger.LogInformation("Арбитраж: подтверждено {Confirmed}, снято {Rejected} из {Total}",
            confirmed, rejected, disputed.Count);
        context.Progress.Info($"арбитраж: подтверждено {confirmed}, снято {rejected}");

        return StateTransition.To(ReviewStates.Report,
            $"вердикт вынесен: подтверждено {confirmed}, снято {rejected}");
    }

    private bool CanAskGraph(ReviewContext context) =>
        _impact.Enabled && context.ArbiterRetries < _impact.MaxArbiterRetries;

    /// <summary>
    /// Снятые находки не удаляются, а понижаются до Info с обоснованием: пользователю
    /// нужно видеть, что именно арбитр отклонил, иначе решение непроверяемо.
    /// </summary>
    private static (int Confirmed, int Rejected) Apply(
        ArbitrationResult verdict, IReadOnlyList<Finding> disputed, ReviewContext context)
    {
        var rejected = 0;

        foreach (var item in verdict.Verdicts)
        {
            var index = item.Number - 1;
            if (index < 0 || index >= disputed.Count || !item.IsRejected) continue;

            var position = context.Findings.IndexOf(disputed[index]);
            if (position < 0) continue;

            var reason = string.IsNullOrWhiteSpace(item.Reason) ? "без обоснования" : item.Reason.Trim();
            context.Findings[position] = disputed[index] with
            {
                Severity = Severity.Info,
                Message = $"{disputed[index].Message} — снято арбитром: {reason}"
            };

            rejected++;
        }

        return (disputed.Count - rejected, rejected);
    }
}
