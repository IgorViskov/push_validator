namespace ReviewAgent.Core.Git;

/// <summary>
/// Настройки устанавливаемого хука (секция <c>Hook</c>). Всё, что здесь есть, попадает
/// в текст скрипта: хук исполняется на машине разработчика и о конфигурации агента
/// ничего не знает.
/// </summary>
public sealed class HookOptions
{
    public const string Section = "Hook";

    /// <summary>
    /// Адрес агента, каким его видит машина разработчика. Не тот, что внутри контейнера:
    /// хук ходит снаружи, через опубликованный порт.
    /// </summary>
    public string PublicUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Чем поднимать агента, если он не отвечает. Хук исполняется на хосте, где docker есть,
    /// поэтому автозапуск возможен; пустая строка отключает попытку.
    /// </summary>
    public string AutostartCommand { get; set; } = "docker start review-agent";

    /// <summary>Сколько секунд ждать готовности агента после автозапуска.</summary>
    public int StartupWaitSeconds { get; set; } = 90;

    /// <summary>
    /// Что делать, если агент недоступен и поднять его не удалось: <c>allow</c> — пропустить
    /// пуш, <c>block</c> — остановить. По умолчанию пропускать: неработающий агент не должен
    /// парализовать работу команды, а «упавший ревьюер молча блокирует пуш» — худший
    /// из возможных отказов.
    /// </summary>
    public string OnUnavailable { get; set; } = "allow";

    /// <summary>
    /// Позволять ли разработчику продавить пуш переменной окружения
    /// <c>REVIEW_AGENT_FORCE=1</c>. Последнее слово за человеком — это тот самый механизм.
    /// </summary>
    public bool AllowForceOverride { get; set; } = true;

    /// <summary>Таймаут ожидания вердикта. Локальные модели думают долго.</summary>
    public int ReviewTimeoutSeconds { get; set; } = 1200;
}
