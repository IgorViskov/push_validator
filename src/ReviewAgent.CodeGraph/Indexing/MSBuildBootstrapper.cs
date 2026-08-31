using Microsoft.Build.Locator;

namespace ReviewAgent.CodeGraph.Indexing;

/// <summary>
/// Регистрация MSBuild. Обязана отработать до того, как загрузится первый тип MSBuild —
/// иначе Roslyn возьмёт свои копии сборок, не найдёт установленный SDK и откроет решение
/// без ссылок: семантическая модель молча перестанет разрешать символы, а половина рёбер
/// CALLS просто не появится.
///
/// Отсюда вызов первой строкой в <c>Program.cs</c>, а не при первой индексации.
/// </summary>
public static class MSBuildBootstrapper
{
    private static readonly Lock Gate = new();

    /// <summary>Найденный SDK — печатается в лог при старте, чтобы отличать «не тот SDK» от «нет SDK».</summary>
    public static string? RegisteredSdk { get; private set; }

    public static void Ensure()
    {
        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered) return;

            var instance = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance is null)
            {
                // Регистрация не удалась: индексация будет отказывать, а проверка коммита —
                // деградировать до анализа по одному диффу. Падать здесь нельзя, иначе
                // отсутствие SDK в образе уронит весь хост, включая админку.
                RegisteredSdk = null;
                return;
            }

            MSBuildLocator.RegisterInstance(instance);
            RegisteredSdk = $"{instance.Name} {instance.Version} ({instance.MSBuildPath})";
        }
    }

    public static bool IsReady => MSBuildLocator.IsRegistered;
}
