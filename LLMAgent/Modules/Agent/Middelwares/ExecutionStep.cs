using LLMAgent.Extensions;
using LLMAgent.Models;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Tools;
using LLMAgent.Prompts;

namespace LLMAgent.Modules.Agent.Middelwares;

/// <summary>
/// Шаг 2. Когнитивный роутер выбирает Execution-модель по сложности,
/// модель анализирует дифф с доступом к инструментам (ReAct).
/// </summary>
public sealed class ExecutionStep : IAgentMiddleware
{
    private readonly CognitiveRouter _router;
    private readonly RepoToolFactory _toolFactory;
    private readonly Logger _logger;
    private readonly AgentEngineDelegate _next; 

    public ExecutionStep(AgentEngineDelegate next, CognitiveRouter router, RepoToolFactory toolFactory, Logger logger)
    {
        _router = router;
        _toolFactory = toolFactory;
        _logger = logger;
        _next = next;
    }

    public async Task Run(LlmContext context)
    {
        var (chat, model) = _router.GetExecutionChat(context.ComplexityScore);
        context.ExecutionModel = model;

        foreach (var tool in _toolFactory.Build(context.RepoPath))
        {
            chat.AddTool(tool);
        }

        chat.AddMessage(Prompt.ExecutionRequestFor(context.RepoPath, context.Diff));

        AnalysisResult? result;
        try
        {
            // Фаза 1: рассуждение с инструментами (ReAct), свободный текст.
            await chat.GetAnswer(context.CancellationToken);

            // Фаза 2: строго типизированное извлечение находок (без инструментов).
            chat.AddMessage(Prompt.ExecutionSummaryRequest);
            result = await chat.GetAnswer<AnalysisResult>(context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warn("Этап анализа ({Model}): обращение к модели не удалось — {Error}.", model.Name, e.Message);
            result = null;
        }

        IReadOnlyList<Finding> findings = result is not null
            ? result.ToFindings("Анализ")
            : [new Finding(Severity.Critical, "Анализ",
                "Этап анализа не дал разборчивого результата — пуш блокируется до ручной проверки.")];
        context.Findings.AddRange(findings);

        _logger.Info("Этап анализа ({Model}): находок {Count}.", model.Name, findings.Count);

        await _next(context);
    }
}
