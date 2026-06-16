using System.ComponentModel;
using System.Text;
using LLMAgent.Modules.Git;
using Microsoft.Extensions.AI;

namespace LLMAgent.Modules.Tools;

/// <summary>
/// Собирает инструменты function-calling, привязанные к конкретному репозиторию.
/// Схемы инструментов выводятся из сигнатур C#-методов (через AIFunctionFactory),
/// поэтому отдельный JSON-Schema и ручной разбор аргументов не нужны.
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

    public IReadOnlyList<AITool> Build(string repoPath)
    {
        var repoFull = Path.GetFullPath(repoPath);

        var readFile = AIFunctionFactory.Create(
            ([Description("Путь к файлу относительно корня репозитория")] string path)
                => ReadFile(repoFull, path),
            name: "read_file",
            description: "Прочитать содержимое файла из анализируемого репозитория по относительному пути.");

        var gitLog = AIFunctionFactory.Create(
            ([Description("Сколько коммитов вернуть (по умолчанию 20)")] int? maxCount, CancellationToken ct)
                => GitLog(repoFull, maxCount ?? 20, ct),
            name: "git_log",
            description: "Получить историю последних коммитов локального git-репозитория.");

        var searchFiles = AIFunctionFactory.Create(
            ([Description("Часть имени файла")] string pattern,
             [Description("Необязательно: директория поиска (по умолчанию — репозиторий)")] string? directory)
                => SearchFiles(repoFull, pattern, directory),
            name: "search_files",
            description: "Найти файлы по части имени. По умолчанию ищет в репозитории; для другой директории запрашивается разрешение.");

        return [readFile, gitLog, searchFiles];
    }

    private string ReadFile(string repoFull, string path)
    {
        Console.Error.WriteLine($"🔧 function-call: read_file({path})");

        if (string.IsNullOrWhiteSpace(path))
        {
            return "Не указан путь к файлу.";
        }

        var full = Path.GetFullPath(Path.Combine(repoFull, path));
        if (!IsInside(repoFull, full) && !_permission.Ask($"прочитать файл вне репозитория: {full}"))
        {
            return "Доступ к файлу вне репозитория запрещён пользователем.";
        }

        if (!File.Exists(full))
        {
            return $"Файл не найден: {path}";
        }

        var content = File.ReadAllText(full);
        return content.Length > MaxFileChars
            ? content[..MaxFileChars] + "\n…(файл обрезан)"
            : content;
    }

    private async Task<string> GitLog(string repoFull, int maxCount, CancellationToken ct)
    {
        Console.Error.WriteLine($"🔧 function-call: git_log({maxCount})");
        return await _git.GetLog(repoFull, maxCount, ct);
    }

    private string SearchFiles(string repoFull, string pattern, string? directory)
    {
        Console.Error.WriteLine($"🔧 function-call: search_files({pattern}, {directory})");

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Не указан шаблон поиска.";
        }

        var searchRoot = string.IsNullOrWhiteSpace(directory)
            ? repoFull
            : Path.GetFullPath(Path.Combine(repoFull, directory));

        if (!IsInside(repoFull, searchRoot) && !_permission.Ask($"искать файлы вне репозитория: {searchRoot}"))
        {
            return "Поиск вне репозитория запрещён пользователем.";
        }

        if (!Directory.Exists(searchRoot))
        {
            return $"Директория не найдена: {searchRoot}";
        }

        return Search(repoFull, searchRoot, pattern);
    }

    private static string Search(string repoFull, string searchRoot, string pattern)
    {
        var matches = new StringBuilder();
        var count = 0;

        var pending = new Stack<string>();
        pending.Push(searchRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            try
            {
                // Спускаемся только в неигнорируемые подкаталоги.
                foreach (var subDirectory in Directory.EnumerateDirectories(directory))
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(subDirectory)))
                    {
                        pending.Push(subDirectory);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.AppendLine(Path.GetRelativePath(repoFull, file));
                    if (++count >= MaxSearchResults)
                    {
                        matches.AppendLine("…(результаты обрезаны)");
                        return matches.ToString();
                    }
                }
            }
            catch (Exception)
            {
                // Нет доступа к каталогу или он исчез — пропускаем.
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
}
