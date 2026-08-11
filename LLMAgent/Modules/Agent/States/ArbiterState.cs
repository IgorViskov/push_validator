using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Impact;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Prompts;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 5. Арбитраж. Ревьюер и валидатор работают независимо, и каждая их критическая
/// находка блокирует пуш. Складывать оба списка по «ИЛИ» — значит отдать решение тому,
/// кто мнительнее; поэтому спорные находки снимает или подтверждает отдельная роль.
///
/// Отсюда выходят два ребра графа: в отчёт с вердиктом и назад в <see cref="ImpactState"/>,
/// когда арбитру не хватает фактов о вызывающем коде.
/// </summary>
public sealed class ArbiterState : IAgentState
{
    private readonly CognitiveRouter _router;
    private readonly ImpactOptions _impact;
    private readonly Logger _logger;

    public string Name => AgentStates.Arbiter;

    public ArbiterState(CognitiveRouter router, IOptions<ImpactOptions> impact, Logger logger)
    {
        _router = router;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        var disputed = context.Findings
            .Where(finding => finding.Severity == Severity.Critical)
            .ToList();

        // ── Ветвление: спорить не о чем ───────────────────────────────────────────────
        if (disputed.Count == 0)
        {
            _logger.Info("Арбитраж: критических находок нет, оба этапа согласны.");
            return AgentTransition.To(AgentStates.Report, "критических находок нет — консенсус");
        }

        // Роль арбитра исполняет модель уровня Orchestration: это решение о решениях,
        // отдельной роли в конфиге для него заводить не пришлось.
        var chat = _router.GetChat(CognitiveRoutingType.Orchestration, Prompt.Arbitration);
        chat.AddMessage(Prompt.ArbitrationRequestFor(
            context.Diff,
            ImpactRenderer.Render(context.ImpactChunks, _impact.MaxContextChars),
            disputed));

        ArbitrationResult? verdict = null;
        try
        {
            verdict = await chat.GetAnswer<ArbitrationResult>(context.CancellationToken);

            // Ответ пришёл, но не лёг в схему вердикта. Отличать этот случай от отказа
            // модели важно: лечится он сменой модели роли, а не доступностью endpoint.
            if (verdict is null)
                _logger.Warn("Арбитр вернул неразборчивый ответ — модель роли Orchestration не держит структурный вывод.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn("Арбитр недоступен ({Error}).", e.Message);
        }

        if (verdict is null)
        {
            // Fail-closed: без вердикта критические находки остаются в силе.
            _logger.Warn("Арбитраж не состоялся — {Count} критических находок остаются в силе.", disputed.Count);
            return AgentTransition.To(AgentStates.Report, "арбитр не ответил — находки остаются в силе");
        }

        // ── Ветвление: арбитру не хватает вызывающего кода ────────────────────────────
        if (verdict.NeedsContext && CanAskGraph(context))
        {
            context.ArbiterRetries++;
            context.ImpactQuery = string.IsNullOrWhiteSpace(verdict.ContextQuery) ? null : verdict.ContextQuery.Trim();

            _logger.Info("Арбитраж: не хватает фактов, повторный запрос к графу — «{Query}»",
                context.ImpactQuery ?? "запрос по затронутым символам");

            return AgentTransition.To(AgentStates.Impact, "арбитр запросил вызывающий код — второй проход");
        }

        var (confirmed, rejected) = Apply(verdict, disputed, context);

        _logger.Info("Арбитраж: подтверждено {Confirmed}, снято {Rejected} из {Total}.",
            confirmed, rejected, disputed.Count);

        return AgentTransition.To(AgentStates.Report,
            $"вердикт вынесен: подтверждено {confirmed}, снято {rejected}");
    }

    private bool CanAskGraph(LlmContext context) =>
        _impact.Enabled && context.ArbiterRetries < _impact.MaxArbiterRetries;

    /// <summary>
    /// Снятые находки не удаляются, а понижаются до Info с обоснованием: пользователю
    /// нужно видеть, что именно арбитр отклонил, иначе решение непроверяемо.
    /// </summary>
    private static (int Confirmed, int Rejected) Apply(
        ArbitrationResult verdict, IReadOnlyList<Finding> disputed, LlmContext context)
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
