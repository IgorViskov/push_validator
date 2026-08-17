using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ReviewAgent.Data;

/// <param name="Ok">Удалось ли выполнить операцию.</param>
/// <param name="Error">Причина отказа, готовая к показу в админке.</param>
public sealed record RegistryResult(bool Ok, string? Error, WatchedRepository? Repository = null)
{
    public static RegistryResult Success(WatchedRepository repository) => new(true, null, repository);
    public static RegistryResult Failure(string error) => new(false, error);
}

/// <summary>
/// Реестр обслуживаемых репозиториев: единственное место, где решается, годится ли путь
/// в обслуживание и какой у репозитория ключ.
/// </summary>
public sealed partial class RepositoryRegistry
{
    private readonly AgentDbContext _db;
    private readonly ILogger<RepositoryRegistry> _logger;

    public RepositoryRegistry(AgentDbContext db, ILogger<RepositoryRegistry> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<List<WatchedRepository>> ListAsync(CancellationToken ct) =>
        _db.Repositories.OrderBy(r => r.Name).ToListAsync(ct);

    public Task<WatchedRepository?> FindAsync(int id, CancellationToken ct) =>
        _db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<WatchedRepository?> FindByKeyAsync(string key, CancellationToken ct) =>
        _db.Repositories.FirstOrDefaultAsync(r => r.Key == key, ct);

    /// <summary>
    /// Берёт репозиторий на обслуживание. Путь проверяется здесь, а не в UI: тот же вызов
    /// приходит из HTTP-API, и проверка, живущая в форме, до него не доедет.
    /// </summary>
    public async Task<RegistryResult> AddAsync(string path, string? name, string? solutionPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RegistryResult.Failure("Путь не указан.");

        var full = NormalizePath(path);

        if (!Directory.Exists(full))
            return RegistryResult.Failure(
                $"Каталог {full} агенту не виден. Он должен быть смонтирован в контейнер — " +
                "проверьте REPOS_ROOT в docker-compose.");

        if (!Directory.Exists(Path.Combine(full, ".git")) && !File.Exists(Path.Combine(full, ".git")))
            return RegistryResult.Failure($"В {full} нет каталога .git — это не рабочее дерево git.");

        if (await _db.Repositories.AnyAsync(r => r.Path == full, ct))
            return RegistryResult.Failure($"Репозиторий {full} уже обслуживается.");

        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(full))
            : name.Trim();

        var repository = new WatchedRepository
        {
            Key = await UniqueKeyAsync(displayName, ct),
            Name = displayName,
            Path = full,
            SolutionPath = string.IsNullOrWhiteSpace(solutionPath) ? null : NormalizePath(solutionPath),
            Token = NewToken(),
            Enabled = true
        };

        _db.Repositories.Add(repository);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Репозиторий {Name} ({Path}) взят на обслуживание, ключ {Key}",
            repository.Name, repository.Path, repository.Key);

        return RegistryResult.Success(repository);
    }

    public async Task RemoveAsync(WatchedRepository repository, CancellationToken ct)
    {
        _db.Repositories.Remove(repository);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Репозиторий {Name} исключён из обслуживания", repository.Name);
    }

    public async Task SetEnabledAsync(WatchedRepository repository, bool enabled, CancellationToken ct)
    {
        repository.Enabled = enabled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);

    /// <summary>
    /// Ключ репозитория: латиница, цифры и дефис. Он попадает в идентификаторы узлов графа
    /// и в текст хука, поэтому пробелы и кириллица здесь только мешают.
    /// </summary>
    private async Task<string> UniqueKeyAsync(string name, CancellationToken ct)
    {
        var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "repo";
        if (slug.Length > 40) slug = slug[..40].Trim('-');

        var candidate = slug;
        var suffix = 2;

        while (await _db.Repositories.AnyAsync(r => r.Key == candidate, ct))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
