using System.ComponentModel.DataAnnotations;

namespace ReviewAgent.Data;

/// <summary>
/// Репозиторий, взятый агентом на обслуживание. Запись живёт дольше контейнера —
/// в этом и состоит требование персистентности: после <c>docker compose down</c> и
/// повторного подъёма список подключённых репозиториев должен остаться прежним.
/// </summary>
public sealed class WatchedRepository
{
    public int Id { get; set; }

    /// <summary>
    /// Стабильный ключ: им размечены узлы графа и его же несёт установленный хук.
    /// Не путь: путь может измениться при переносе точки монтирования, а граф
    /// переиндексировать из-за этого не нужно.
    /// </summary>
    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Путь к рабочему дереву, каким его видит агент (внутри контейнера).</summary>
    [MaxLength(1000)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Файл решения. Пусто — агент ищет <c>.sln</c> сам.</summary>
    [MaxLength(1000)]
    public string? SolutionPath { get; set; }

    /// <summary>
    /// Обслуживается ли репозиторий. Исключение из обслуживания не удаляет историю
    /// прогонов: по ней потом объясняют, почему репозиторий отключили.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Секрет, который предъявляет хук. Хук лежит в чужом рабочем дереве, а эндпоинт
    /// проверки открыт по сети внутри машины — без токена любой процесс мог бы
    /// заказывать проверки от имени репозитория.
    /// </summary>
    [MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    public bool HookInstalled { get; set; }
    public DateTimeOffset? HookInstalledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastIndexedAt { get; set; }
    public long GraphNodes { get; set; }
    public long GraphEdges { get; set; }

    /// <summary>Последняя ошибка индексации — иначе «граф пуст» в UI необъясним.</summary>
    [MaxLength(1000)]
    public string? LastIndexError { get; set; }

    public List<ReviewRunRecord> Runs { get; set; } = [];
}

/// <summary>
/// Запись о одном прогоне проверки. Раньше метрики писались JSON-файлами в каталог
/// <c>logs/</c> рядом с бинарём: процесс жил одну проверку, и другого места не было.
/// В контейнере такой каталог живёт до перезапуска, а сравнивать прогоны между собой
/// приходится глазами по именам файлов — поэтому журнал переехал в таблицу.
/// </summary>
public sealed class ReviewRunRecord
{
    public int Id { get; set; }

    public int RepositoryId { get; set; }
    public WatchedRepository? Repository { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public long DurationMs { get; set; }

    /// <summary>Allowed | Blocked.</summary>
    [MaxLength(16)]
    public string Decision { get; set; } = string.Empty;

    /// <summary>Success | Degraded | Aborted — главная метрика качества прогона.</summary>
    [MaxLength(16)]
    public string Outcome { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Branch { get; set; }

    /// <summary>Кто заказал проверку: hook | ui.</summary>
    [MaxLength(16)]
    public string Source { get; set; } = "hook";

    public int Complexity { get; set; }

    [MaxLength(200)]
    public string? ExecutionModel { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }

    public int ModelCalls { get; set; }
    public int GraphQueries { get; set; }
    public int ImpactChunks { get; set; }
    public int DiffLines { get; set; }

    [MaxLength(500)]
    public string? AbortReason { get; set; }

    [MaxLength(2000)]
    public string? GraphStatus { get; set; }

    /// <summary>
    /// Счётчики находок рядом с прогоном. Денормализация здесь безопасна и оправданна:
    /// запись журнала после создания не меняется, поэтому счётчик не может разойтись
    /// с содержимым, а список прогонов иначе пришлось бы грузить вместе со всеми находками
    /// всех сотни прогонов, чтобы напечатать одно число в колонке.
    /// </summary>
    public int FindingCount { get; set; }

    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>
    /// Маршрут, предохранители, обращения к моделям и вызовы инструментов — JSON.
    /// Разносить их по таблицам смысла нет: читают их целиком, на карточке прогона,
    /// и ни один запрос не фильтрует по их полям.
    /// </summary>
    public string RouteJson { get; set; } = "[]";
    public string GuardrailsJson { get; set; } = "[]";
    public string ModelCallsJson { get; set; } = "[]";
    public string ToolCallsJson { get; set; } = "{}";
    public string DegradationsJson { get; set; } = "[]";

    public List<ReviewFindingRecord> Findings { get; set; } = [];
}

public sealed class ReviewFindingRecord
{
    public int Id { get; set; }

    public int RunId { get; set; }
    public ReviewRunRecord? Run { get; set; }

    /// <summary>Info | Warning | Critical.</summary>
    [MaxLength(16)]
    public string Severity { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Stage { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? File { get; set; }

    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;
}
