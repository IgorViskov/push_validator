using LLMAgent.Modules.ErrorsModule;
using LLMAgent.Modules.ErrorsModule.Exceptions;
using LLMAgent.Modules.Git;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Safety;

namespace LLMAgent.Modules.Agent;

public sealed class Agent
{
    private readonly AgentEngine _engine;
    private readonly GitService _git;
    private readonly RunMetrics _metrics;
    private readonly MetricsWriter _metricsWriter;
    private readonly Logger _logger;

    public Agent(
        AgentEngine engine,
        GitService git,
        RunMetrics metrics,
        MetricsWriter metricsWriter,
        Logger logger)
    {
        _engine = engine;
        _git = git;
        _metrics = metrics;
        _metricsWriter = metricsWriter;
        _logger = logger;
    }

    /// <summary>
    /// Прогоняет проверку коммита по графу состояний. Возвращает код выхода:
    /// 0 — пуш разрешён, 1 — приостановлен.
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

        _metrics.ConversationId = context.ConversationId;
        _metrics.RepoPath = repoPath;

        if (string.IsNullOrWhiteSpace(context.Diff))
        {
            _logger.Info("Изменений для анализа не найдено — пуш разрешён.");
            return 0;
        }

        // Дальше маршрут выбирают сами состояния: triage решает, идти ли за контекстом
        // влияния, арбитр — нужен ли второй проход. Здесь задаётся только точка входа.
        await _engine.Run(AgentStates.Triage, context);

        var exitCode = context.AllowPush ? 0 : 1;

        // Журнал пишется в самом конце: только здесь известен код выхода, а без него
        // запись о прогоне не позволяет сопоставить метрики с реальным исходом.
        if (_metricsWriter.Write(context, exitCode) is { } path)
        {
            _logger.Info("Журнал прогона: {Path}", path);
        }

        return exitCode;
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
