using Microsoft.Extensions.Logging;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Prompts;
using ReviewAgent.Core.Safety;

namespace ReviewAgent.Core.Review.States;

/// <summary>
/// Шаг 4. Валидация: простые проверки по тексту — типы, соответствие аргументов, опечатки
/// в именах. Выполняется отдельной моделью и не видит находок ревьюера: два независимых
/// мнения нужны, чтобы арбитру было что сверять.
/// </summary>
public sealed class ValidateState : IReviewState
{
    private readonly CognitiveRouter _router;
    private readonly ModelGate _gate;
    private readonly FindingsGuard _findingsGuard;
    private readonly RunMetrics _metrics;
    private readonly ILogger<ValidateState> _logger;

    public string Name => ReviewStates.Validate;

    public ValidateState(
        CognitiveRouter router,
        ModelGate gate,
        FindingsGuard findingsGuard,
        RunMetrics metrics,
        ILogger<ValidateState> logger)
    {
        _router = router;
        _gate = gate;
        _findingsGuard = findingsGuard;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<StateTransition> Run(ReviewContext context)
    {
        var chat = _router.GetChat(CognitiveRole.Validation);
        chat.AddMessage(Prompt.ValidationRequestFor(context.Diff));

        context.Progress.Info($"валидация: модель {chat.Model.Name}");

        var result = await _gate.Ask(Name, chat,
            ct => chat.GetAnswer<AnalysisResult>(ct), context.CancellationToken);

        IReadOnlyList<Finding> findings;
        if (result is not null)
        {
            findings = _findingsGuard.Validate(Name, result.ToFindings(Stages.Validate), context.RepoPath);
        }
        else
        {
            _metrics.Degrade("этап валидации не дал разборчивого результата");
            findings =
            [
                new Finding(Severity.Critical, Stages.Validate,
                    "Этап валидации не дал разборчивого результата — пуш блокируется до ручной проверки.")
            ];
        }

        context.ReplaceFindings(Stages.Validate, findings);

        _logger.LogInformation("Этап валидации: находок {Count}", findings.Count);
        context.Progress.Info($"валидация завершена: находок {findings.Count}");

        return StateTransition.To(ReviewStates.Arbiter, "оба мнения собраны");
    }
}
