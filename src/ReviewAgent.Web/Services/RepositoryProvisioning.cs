using ReviewAgent.CodeGraph;
using ReviewAgent.Core.Git;
using ReviewAgent.Data;

namespace ReviewAgent.Web.Services;

/// <param name="Ok">Удалось ли выполнить операцию целиком.</param>
/// <param name="Message">Что произошло — готово к показу человеку.</param>
public sealed record ProvisioningResult(
    bool Ok, string Message, WatchedRepository? Repository = null)
{
    public static ProvisioningResult Failure(string message) => new(false, message);
}

/// <summary>
/// Подключение репозитория к обслуживанию и снятие с него.
///
/// Живёт отдельным сервисом, а не в обработчике кнопки, потому что вызывающих двое:
/// админка и HTTP-API. Логика здесь не косметическая — подключение это три связанных
/// действия (запись в реестр, установка хука, индексация графа), и разъехавшиеся
/// реализации означали бы репозиторий, подключённый наполовину.
/// </summary>
public sealed class RepositoryProvisioning
{
    private readonly RepositoryRegistry _registry;
    private readonly GitHookInstaller _hooks;
    private readonly IndexingQueue _indexing;
    private readonly IGraphStore _graph;
    private readonly ILogger<RepositoryProvisioning> _logger;

    public RepositoryProvisioning(
        RepositoryRegistry registry,
        GitHookInstaller hooks,
        IndexingQueue indexing,
        IGraphStore graph,
        ILogger<RepositoryProvisioning> logger)
    {
        _registry = registry;
        _hooks = hooks;
        _indexing = indexing;
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Берёт репозиторий на обслуживание: реестр, хук, полная индексация в фоне.
    ///
    /// Хук ставится сразу, а не отдельным действием: подключение без хука не даёт ничего —
    /// пуш всё равно пройдёт мимо агента, и человек узнает об этом не сразу.
    /// </summary>
    public async Task<ProvisioningResult> ConnectAsync(
        string path, string? name, string? solutionPath, bool overwriteForeignHook, CancellationToken ct)
    {
        var added = await _registry.AddAsync(path, name, solutionPath, ct);
        if (!added.Ok || added.Repository is null)
            return ProvisioningResult.Failure(added.Error ?? "Не удалось подключить репозиторий.");

        var repository = added.Repository;

        var hook = await _hooks.InstallAsync(
            repository.Path, repository.Key, repository.Token, overwriteForeignHook, ct);

        repository.HookInstalled = hook.Ok;
        repository.HookInstalledAt = hook.Ok ? DateTimeOffset.Now : null;
        await _registry.SaveAsync(ct);

        _indexing.Enqueue(repository);

        var message = hook.Ok
            ? $"Репозиторий {repository.Name} подключён, хук установлен, индексация запущена."
            : $"Репозиторий {repository.Name} подключён, индексация запущена. Хук: {hook.Message}";

        _logger.LogInformation("{Message}", message);
        return new ProvisioningResult(hook.Ok, message, repository);
    }

    /// <summary>
    /// Снимает репозиторий с обслуживания: хук, граф, запись реестра.
    ///
    /// Хук снимается обязательно: оставленный продолжал бы обращаться к агенту, получал бы
    /// отказ «репозиторий не обслуживается» и тормозил каждый пуш. Граф стирается по той же
    /// причине, по которой заводился, — держать слепок кода, который больше не обслуживают,
    /// незачем.
    /// </summary>
    public async Task<ProvisioningResult> DisconnectAsync(WatchedRepository repository, CancellationToken ct)
    {
        var hook = await _hooks.RemoveAsync(repository.Path, ct);

        var graphCleared = true;
        try
        {
            await _graph.ResetAsync(repository.Key, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            graphCleared = false;
            _logger.LogWarning("Граф репозитория {Key} не очищен: {Error}", repository.Key, e.Message);
        }

        await _registry.RemoveAsync(repository, ct);

        var notes = new List<string> { $"{repository.Name} исключён из обслуживания." };
        if (!hook.Ok) notes.Add($"Хук: {hook.Message}");
        if (!graphCleared) notes.Add("Граф не очищен — база недоступна.");

        return new ProvisioningResult(hook.Ok && graphCleared, string.Join(" ", notes));
    }
}
