namespace LLMAgent.Modules.Tools;

/// <summary>
/// Имена директорий, которые поиск по файлам не обходит: служебные каталоги
/// систем контроля версий и IDE, а также артефакты сборки и зависимости
/// популярных экосистем (.NET, JS/TS, Python, Java/JVM, Go).
/// </summary>
public static class IgnoredDirectories
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Системы контроля версий и IDE
        ".git", ".svn", ".hg", ".idea", ".vs", ".vscode",

        // .NET
        "bin", "obj", "packages",

        // JavaScript / TypeScript
        "node_modules", "bower_components", "dist", "build", "out", "coverage",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", ".cache",

        // Python
        "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".eggs",

        // Java / JVM
        "target", ".gradle", ".mvn",

        // Go
        "vendor"
    };

    public static bool Contains(string directoryName) => Names.Contains(directoryName);
}
