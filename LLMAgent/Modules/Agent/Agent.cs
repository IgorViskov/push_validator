using LLMAgent.Modules.Agent.Middelwares;
using LLMAgent.Modules.ErrorsModule;
using LLMAgent.Modules.ErrorsModule.Exceptions;
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
        EnsureDirectoryExist(repoPath);
        await EnsureIsGitRepository(repoPath, cancellationToken);

        var context = new LlmContext
        {
            RepoPath = repoPath,
            Diff = await _git.GetLatestChanges(repoPath, cancellationToken),
            CancellationToken = cancellationToken
        };

        if (string.IsNullOrWhiteSpace(context.Diff))
        {
            _logger.Info("Изменений для анализа не найдено — пуш разрешён.");
            return 0;
        }

        await _engine
            .Use<OrchestrationStep>()
            .Use<ExecutionStep>()
            .Use<ValidationStep>()
            .Use<ReportStep>()
            .Run(context);
        
        
        return context.AllowPush ? 0 : 1;
    }

    private void EnsureDirectoryExist(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.Warn("Путь к репозиторию не существует: {Path}", path);
            Errors.Throw<ExitException>(1);
        }
    }
    
    private async Task EnsureIsGitRepository(string path, CancellationToken cancellationToken)
    {
        if (!await _git.IsGitRepository(path, cancellationToken))
        {
            _logger.Warn("Каталог не является git-репозиторием: {Path}", path);
            Errors.Throw<ExitException>(1);
        }
    }
}
