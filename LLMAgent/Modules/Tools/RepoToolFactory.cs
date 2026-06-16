using System.Text;
using System.Text.Json;
using LLMAgent.Modules.Chats;
using LLMAgent.Modules.Git;
using OpenAI.Chat;

namespace LLMAgent.Modules.Tools;

/// <summary>
/// Собирает инструменты function-calling, привязанные к конкретному репозиторию.
/// Чтение/поиск вне каталога репозитория требуют разрешения пользователя.
/// </summary>
public sealed class RepoToolFactory
{
    private const int MaxFileChars = 60_000;
    private const int MaxSearchResults = 100;

    private readonly GitService _git;
    private readonly IUserPermission _permission;

    public RepoToolFactory(GitService git, IUserPermission permission)
    {
        _git = git;
        _permission = permission;
    }

    public IReadOnlyList<AgentTool> Build(string repoPath, CancellationToken pipelineToken)
    {
        var repoFull = Path.GetFullPath(repoPath);
        return
        [
            ReadFileTool(repoFull),
            GitLogTool(repoFull, pipelineToken),
            SearchFilesTool(repoFull)
        ];
    }

    private AgentTool ReadFileTool(string repoFull)
    {
        var definition = ChatTool.CreateFunctionTool(
            "read_file",
            "Прочитать содержимое файла из анализируемого репозитория по относительному пути.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Путь к файлу относительно корня репозитория"}},"required":["path"]}
                """));

        return new AgentTool(definition, (args, _) =>
        {
            var path = ReadStringArg(args, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult("Не указан аргумент 'path'.");
            }

            var full = Path.GetFullPath(Path.Combine(repoFull, path));
            if (!IsInside(repoFull, full) &&
                !_permission.Ask($"прочитать файл вне репозитория: {full}"))
            {
                return Task.FromResult("Доступ к файлу вне репозитория запрещён пользователем.");
            }

            if (!File.Exists(full))
            {
                return Task.FromResult($"Файл не найден: {path}");
            }

            var content = File.ReadAllText(full);
            if (content.Length > MaxFileChars)
            {
                content = content[..MaxFileChars] + "\n…(файл обрезан)";
            }

            return Task.FromResult(content);
        });
    }

    private AgentTool GitLogTool(string repoFull, CancellationToken pipelineToken)
    {
        var definition = ChatTool.CreateFunctionTool(
            "git_log",
            "Получить историю последних коммитов локального git-репозитория.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"maxCount":{"type":"integer","description":"Сколько коммитов вернуть (по умолчанию 20)"}}}
                """));

        return new AgentTool(definition, async (args, ct) =>
        {
            var max = ReadIntArg(args, "maxCount") ?? 20;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(pipelineToken, ct);
            return await _git.GetLog(repoFull, max, linked.Token);
        });
    }

    private AgentTool SearchFilesTool(string repoFull)
    {
        var definition = ChatTool.CreateFunctionTool(
            "search_files",
            "Найти файлы по части имени. По умолчанию ищет в репозитории; для другой директории запрашивается разрешение.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"pattern":{"type":"string","description":"Часть имени файла"},"directory":{"type":"string","description":"Необязательно: директория поиска"}},"required":["pattern"]}
                """));

        return new AgentTool(definition, (args, _) =>
        {
            var pattern = ReadStringArg(args, "pattern");
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Task.FromResult("Не указан аргумент 'pattern'.");
            }

            var directory = ReadStringArg(args, "directory");
            var searchRoot = string.IsNullOrWhiteSpace(directory)
                ? repoFull
                : Path.GetFullPath(Path.Combine(repoFull, directory));

            if (!IsInside(repoFull, searchRoot) &&
                !_permission.Ask($"искать файлы вне репозитория: {searchRoot}"))
            {
                return Task.FromResult("Поиск вне репозитория запрещён пользователем.");
            }

            if (!Directory.Exists(searchRoot))
            {
                return Task.FromResult($"Директория не найдена: {searchRoot}");
            }

            return Task.FromResult(Search(repoFull, searchRoot, pattern));
        });
    }

    private static string Search(string repoFull, string searchRoot, string pattern)
    {
        var matches = new StringBuilder();
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            if (!Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.AppendLine(Path.GetRelativePath(repoFull, file));
            if (++count >= MaxSearchResults)
            {
                matches.AppendLine("…(результаты обрезаны)");
                break;
            }
        }

        return count == 0 ? "Ничего не найдено." : matches.ToString();
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadStringArg(string args, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadIntArg(string args, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt32();
            }
        }
        catch (JsonException)
        {
            // игнорируем
        }

        return null;
    }
}
