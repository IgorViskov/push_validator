using LLMAgent.Models;
using LLMAgent.Modules.Chats;
using LLMAgent.Modules.Impact;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Safety;
using LLMAgent.Modules.Tools;
using LLMAgent.Prompts;
using Microsoft.Extensions.Options;

namespace LLMAgent.Modules.Agent.States;

/// <summary>
/// Шаг 3. Ревью: когнитивный роутер выбирает модель по сложности, модель анализирует дифф
/// с доступом к инструментам (ReAct) и с уже подключённым контекстом влияния из графа.
/// </summary>
public sealed class ReviewState : IAgentState
{
    private readonly CognitiveRouter _router;
    private readonly RepoToolFactory _toolFactory;
    private readonly ModelGate _gate;
    private readonly FindingsGuard _findingsGuard;
    private readonly RunMetrics _metrics;
    private readonly ImpactOptions _impact;
    private readonly Logger _logger;

    public string Name => AgentStates.Review;

    public ReviewState(
        CognitiveRouter router,
        RepoToolFactory toolFactory,
        ModelGate gate,
        FindingsGuard findingsGuard,
        RunMetrics metrics,
        IOptions<ImpactOptions> impact,
        Logger logger)
    {
        _router = router;
        _toolFactory = toolFactory;
        _gate = gate;
        _findingsGuard = findingsGuard;
        _metrics = metrics;
        _impact = impact.Value;
        _logger = logger;
    }

    public async Task<AgentTransition> Run(LlmContext context)
    {
        var (chat, model) = _router.GetExecutionChat(context.ComplexityScore);
        context.ExecutionModel = model;

        // Инструмент графа не даём, если граф уже не ответил: модель потратит виток ReAct
        // на вызов, который заведомо вернёт отказ.
        var graphUsable = _impact.Enabled && (context.HasImpact || !context.ImpactRequested);

        // Фрагменты, которые модель запросит у графа сама, попадают в тот же список:
        // иначе отчёт покажет «контекст влияния не запрашивался», хотя запрос был.
        var tools = _toolFactory.Build(
            context.RepoPath, context.ConversationId, graphUsable,
            chunks => context.AddImpactChunks(chunks));

        foreach (var tool in tools)
        {
            chat.AddTool(tool);
        }

        var impactBlock = ImpactRenderer.Render(context.ImpactChunks, _impact.MaxContextChars);
        chat.AddMessage(Prompt.ExecutionRequestFor(context.RepoPath, context.Diff, impactBlock));

        var progress = new ChatActivityRenderer();

        // Фаза 1: рассуждение с инструментами (ReAct), свободный текст.
        // Ответ читается потоково: рендерер печатает статусы — чем занята модель.
        var reasoning = await _gate.Ask(Name, model, chat,
            async ct => await chat.GetAnswer(progress.OnUpdate, ct), context.CancellationToken);
        progress.Complete();

        // Фаза 2: строго типизированное извлечение находок (без инструментов).
        AnalysisResult? result = null;
        if (reasoning is not null)
        {
            chat.AddMessage(Prompt.ExecutionSummaryRequest);
            result = await _gate.Ask($"{Name}:summary", model, chat,
                ct => chat.GetAnswer<AnalysisResult>(ct), context.CancellationToken);
        }

        IReadOnlyList<Finding> findings;
        if (result is not null)
        {
            findings = _findingsGuard.Validate(Name, result.ToFindings(Stages.Review), context.RepoPath);
        }
        else
        {
            // Fail-closed: отсутствие разборчивого ответа — это не «замечаний нет».
            _metrics.Degrade("этап анализа не дал разборчивого результата");
            findings =
            [
                new Finding(Severity.Critical, Stages.Review,
                    "Этап анализа не дал разборчивого результата — пуш блокируется до ручной проверки.")
            ];
        }

        // Именно замена, а не добавление: на второй проход состояние входит повторно,
        // и прошлые находки этого же этапа должны быть вытеснены.
        context.ReplaceFindings(Stages.Review, findings);

        _logger.Info("Этап анализа ({Model}): находок {Count}{Impact}.",
            model.Name, findings.Count,
            context.HasImpact ? $", контекст влияния {context.ImpactChunks.Count} фрагм." : string.Empty);

        // ── Ветвление: второй проход не нуждается в текстовой валидации ───────────────
        // Дифф между проходами не менялся, значит и её вердикт не изменится.
        return context.ArbiterRetries > 0
            ? AgentTransition.To(AgentStates.Arbiter, "повторный анализ по уточнённому контексту — сразу к арбитру")
            : AgentTransition.To(AgentStates.Validate, "первичный анализ завершён");
    }
}
