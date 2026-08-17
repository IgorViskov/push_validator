using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReviewAgent.Core.Git;

/// <param name="Ok">Удалось ли выполнить операцию.</param>
/// <param name="Message">Что произошло — показывается в админке.</param>
public sealed record HookResult(bool Ok, string Message)
{
    public static HookResult Success(string message) => new(true, message);
    public static HookResult Failure(string message) => new(false, message);
}

public enum HookState
{
    /// <summary>Хука нет.</summary>
    Missing,

    /// <summary>Стоит наш хук.</summary>
    Installed,

    /// <summary>Стоит чужой хук — перезаписывать без явного согласия нельзя.</summary>
    Foreign
}

/// <summary>
/// Установка и снятие хука <c>pre-push</c> в обслуживаемом репозитории.
///
/// Работает по смонтированному рабочему дереву, а не по сети: агент видит репозиторий,
/// потому что тот же каталог смонтирован в контейнер. Путь к каталогу хуков спрашивается
/// у самого git (<c>rev-parse --git-path hooks</c>): в worktree и submodule <c>.git</c> —
/// файл-указатель, а не каталог, и <c>&lt;repo&gt;/.git/hooks</c> там просто не существует.
/// </summary>
public sealed class GitHookInstaller
{
    private const string HookName = "pre-push";

    private readonly GitService _git;
    private readonly HookOptions _options;
    private readonly ILogger<GitHookInstaller> _logger;

    public GitHookInstaller(
        GitService git, IOptions<HookOptions> options, ILogger<GitHookInstaller> logger)
    {
        _git = git;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HookState> GetStateAsync(string repoPath, CancellationToken ct)
    {
        var path = await ResolveHookPathAsync(repoPath, ct);
        if (path is null || !File.Exists(path)) return HookState.Missing;

        try
        {
            return IsOurs(await File.ReadAllTextAsync(path, ct)) ? HookState.Installed : HookState.Foreign;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HookState.Foreign;
        }
    }

    public async Task<HookResult> InstallAsync(
        string repoPath, string repoKey, string token, bool overwriteForeign, CancellationToken ct)
    {
        var hooksDirectory = await ResolveHooksDirectoryAsync(repoPath, ct);
        if (hooksDirectory is null)
            return HookResult.Failure($"Не удалось определить каталог хуков для {repoPath}.");

        var path = Path.Combine(hooksDirectory, HookName);

        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);

            if (!IsOurs(existing) && !overwriteForeign)
                return HookResult.Failure(
                    "В репозитории уже есть свой pre-push. Он будет сохранён рядом как " +
                    "pre-push.backup — подтвердите перезапись.");

            if (!IsOurs(existing))
            {
                // Чужой хук не удаляем: он может быть частью процесса команды, и восстановить
                // его из истории — это лезть в чужой репозиторий. Копия рядом дешевле.
                await File.WriteAllTextAsync(path + ".backup", existing, ct);
                _logger.LogWarning("Существующий pre-push сохранён как pre-push.backup в {Path}", hooksDirectory);
            }
        }

        try
        {
            Directory.CreateDirectory(hooksDirectory);

            // LF, а не CRLF: sh на Windows не исполнит скрипт с \r в шебанге —
            // ошибка выглядит как «/bin/sh^M: bad interpreter» и ищется долго.
            var script = HookScript.Build(_options, repoKey, token).Replace("\r\n", "\n");
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(false), ct);

            MakeExecutable(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HookResult.Failure($"Хук не записан: {e.Message}");
        }

        _logger.LogInformation("Хук pre-push установлен: {Path}", path);
        return HookResult.Success($"Хук установлен: {path}");
    }

    public async Task<HookResult> RemoveAsync(string repoPath, CancellationToken ct)
    {
        var path = await ResolveHookPathAsync(repoPath, ct);
        if (path is null || !File.Exists(path))
            return HookResult.Success("Хука не было — снимать нечего.");

        try
        {
            if (!IsOurs(await File.ReadAllTextAsync(path, ct)))
                return HookResult.Failure("В репозитории стоит чужой pre-push — он не тронут.");

            File.Delete(path);

            // Свой хук снят — возвращаем на место тот, что стоял до нас.
            var backup = path + ".backup";
            if (File.Exists(backup))
            {
                File.Move(backup, path, overwrite: true);
                MakeExecutable(path);
                return HookResult.Success("Хук снят, прежний pre-push восстановлен из резервной копии.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HookResult.Failure($"Хук не снят: {e.Message}");
        }

        _logger.LogInformation("Хук pre-push снят: {Path}", path);
        return HookResult.Success("Хук снят.");
    }

    /// <summary>Текст хука для показа в админке — чтобы было видно, что именно ставится.</summary>
    public string Preview(string repoKey, string token) => HookScript.Build(_options, repoKey, token);

    private async Task<string?> ResolveHookPathAsync(string repoPath, CancellationToken ct)
    {
        var directory = await ResolveHooksDirectoryAsync(repoPath, ct);
        return directory is null ? null : Path.Combine(directory, HookName);
    }

    private async Task<string?> ResolveHooksDirectoryAsync(string repoPath, CancellationToken ct)
    {
        if (!Directory.Exists(repoPath)) return null;

        var resolved = await _git.GetHooksDirectory(repoPath, ct);
        if (resolved is { Length: > 0 })
        {
            return Path.IsPathRooted(resolved)
                ? resolved
                : Path.GetFullPath(Path.Combine(repoPath, resolved));
        }

        // git недоступен — обычный случай раскладки остаётся верным для 99% репозиториев.
        var fallback = Path.Combine(repoPath, ".git", "hooks");
        return Directory.Exists(Path.Combine(repoPath, ".git")) ? fallback : null;
    }

    private static bool IsOurs(string content) =>
        content.Contains(HookScript.Marker, StringComparison.Ordinal);

    /// <summary>
    /// Бит исполнения. На Windows понятия нет, и вызов там ничего не делает; в контейнере
    /// на Linux без него git молча пропускает хук — он просто не считается исполняемым.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Права выставить не удалось — хук всё равно записан; сообщать об этом
            // будет сам git, отказавшись его запускать.
        }
    }
}
