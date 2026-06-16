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

    public ExecutionStep(CognitiveRouter router, RepoToolFactory toolFactory, Logger logger)
    {
        _router = router;
        _toolFactory = toolFactory;
        _logger = logger;
    }

    public async Task Run(AgentEngineDelegate? next, LlmContext context, CancellationToken cancellationToken)
    {
        var (chat, model) = _router.GetExecutionChat(context.ComplexityScore);
        context.ExecutionModel = model;

        foreach (var tool in _toolFactory.Build(context.RepoPath))
        {
            chat.AddTool(tool);
        }

        chat.AddMessage(Prompt.ExecutionRequestFor(context.RepoPath, context.Diff));

        // Фаза 1: рассуждение с инструментами (ReAct), свободный текст.
        await chat.GetAnswer(cancellationToken);

        // Фаза 2: строго типизированное извлечение находок (без инструментов).
        chat.AddMessage(Prompt.ExecutionSummaryRequest);
        var result = await chat.GetAnswer<AnalysisResult>(cancellationToken);

        var findings = result?.ToFindings("Анализ") ?? [];
        context.Findings.AddRange(findings);

        _logger.Info("Этап анализа ({Model}) нашёл находок: {Count}", model.Name, findings.Count);

        if (next is not null)
        {
            await next(null, context, cancellationToken);
        }
    }
}
