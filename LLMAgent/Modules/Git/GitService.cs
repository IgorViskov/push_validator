using System.Diagnostics;
using System.Text;

namespace LLMAgent.Modules.Git;

/// <summary>
/// Тонкая обёртка над установленным git (через Process). Без внешних зависимостей.
/// </summary>
public sealed class GitService
{
    /// <summary>
    /// Возвращает дифф «последних изменений»: сначала пытается взять ещё не запушенные
    /// коммиты, затем — последний коммит, затем — изменения рабочего дерева.
    /// </summary>
    public async Task<string> GetLatestChanges(string repoPath, CancellationToken cancellationToken)
    {
        var unpushed = await Run(repoPath, cancellationToken, "diff", "--no-color", "@{u}..HEAD");
        if (unpushed is { Success: true, Output.Length: > 0 })
        {
            return unpushed.Output;
        }

        var lastCommit = await Run(repoPath, cancellationToken, "diff", "--no-color", "HEAD~1", "HEAD");
        if (lastCommit is { Success: true, Output.Length: > 0 })
        {
            return lastCommit.Output;
        }

        var working = await Run(repoPath, cancellationToken, "diff", "--no-color", "HEAD");
        return working.Success ? working.Output : string.Empty;
    }

    public async Task<string> GetLog(string repoPath, int maxCount, CancellationToken cancellationToken)
    {
        var result = await Run(repoPath, cancellationToken,
            "log", $"-n{Math.Clamp(maxCount, 1, 200)}", "--no-color", "--pretty=format:%h %an %ad %s", "--date=short");
        return result.Success ? result.Output : result.Error;
    }

    public async Task<bool> IsGitRepository(string repoPath, CancellationToken cancellationToken)
    {
        var result = await Run(repoPath, cancellationToken, "rev-parse", "--is-inside-work-tree");
        return result.Success && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GitResult> Run(string repoPath, CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitResult(false, string.Empty, "Не удалось запустить процесс git.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await stdoutTask;
            var error = await stderrTask;
            return new GitResult(process.ExitCode == 0, output, error);
        }
        catch (Exception e)
        {
            return new GitResult(false, string.Empty, e.Message);
        }
    }

    private readonly record struct GitResult(bool Success, string Output, string Error);
}
