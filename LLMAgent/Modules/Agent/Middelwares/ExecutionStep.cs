using LLMAgent.Models;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Tools;

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

        chat.AddMessage(
            $"""
             Корень репозитория: {context.RepoPath}
             Проанализируй изменения. Используй инструменты read_file, git_log, search_files,
             чтобы проверить затронутые контракты и вызывающий код вне диффа.

             Git-дифф:
             ```diff
             {context.Diff}
             ```
             """);

        // Фаза 1: рассуждение с инструментами (ReAct), свободный текст.
        await chat.GetAnswer(cancellationToken);

        // Фаза 2: строго типизированное извлечение находок (без инструментов).
        chat.AddMessage("Подведи итог анализа: верни все найденные проблемы как список находок.");
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
