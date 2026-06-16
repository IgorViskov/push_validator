using LLMAgent.Modules.Git;
using LLMAgent.Modules.Logging;

namespace LLMAgent.Modules.Agent;

public sealed class Agent
{
    private readonly AgentEngine _engine;
    private readonly GitService _git;
    private readonly Logger _logger;

    public Agent(AgentEngine engine, GitService git, Logger logger)
    {
        _engine = engine;
        _git = git;
        _logger = logger;
    }

    /// <summary>
    /// Прогоняет проверку коммита. Возвращает код выхода: 0 — пуш разрешён, 1 — приостановлен.
    /// </summary>
    public async Task<int> Run(string repoPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repoPath))
        {
            _logger.Warn("Путь к репозиторию не существует: {Path}", repoPath);
            return 1;
        }

        if (!await _git.IsGitRepository(repoPath, cancellationToken))
        {
            _logger.Warn("Каталог не является git-репозиторием: {Path}", repoPath);
            return 1;
        }

        var context = new LlmContext { RepoPath = repoPath };
        context.Diff = await _git.GetLatestChanges(repoPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(context.Diff))
        {
            _logger.Info("Изменений для анализа не найдено — пуш разрешён.");
            return 0;
        }

        await _engine.Run(context, cancellationToken);
        return context.AllowPush ? 0 : 1;
    }
}
