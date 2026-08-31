namespace ReviewAgent.Core.Models;

/// <summary>Роль, в которой когнитивный роутер использует модель.</summary>
public enum CognitiveRole
{
    /// <summary>Триаж и арбитраж: оценка, решение о решениях. Нужна дисциплина, не глубина.</summary>
    Orchestration = 1,

    /// <summary>Главный анализ диффа в цикле ReAct. Нужна работа с инструментами и понимание кода.</summary>
    Execution = 2,

    /// <summary>Текстовые проверки диффа вторым, независимым мнением. Нужна скорость.</summary>
    Validation = 3
}

/// <summary>
/// Провайдер моделей: любой OpenAI-совместимый endpoint — LM Studio, Ollama, vLLM,
/// OpenRouter, корпоративный шлюз. Секция <c>Llm:Providers</c>.
/// </summary>
public sealed class LlmProviderOptions
{
    public string Name { get; set; } = "default";

    /// <summary>
    /// Ключ API. Локальные серверы обычно его не проверяют, но LM Studio с включённой
    /// аутентификацией — проверяет. Задавать через переменную окружения, не в JSON.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Адрес вместе с версией пути: SDK дописывает к нему только <c>/chat/completions</c>.
    /// LM Studio — <c>http://host.docker.internal:1234/v1</c> при запуске агента в контейнере.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:1234/v1";

    /// <summary>Таймаут одного обращения. Локальная 30B-модель на слабой карте думает долго.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    public List<ModelOptions> Models { get; set; } = [];
}

/// <summary>Одна модель провайдера с назначенной ролью и ценой.</summary>
public sealed class ModelOptions
{
    public string Name { get; set; } = string.Empty;

    public CognitiveRole Role { get; set; } = CognitiveRole.Execution;

    /// <summary>Больше — предпочтительнее внутри своей роли.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Положение модели на шкале «цена ↔ ум» (≈1..10). Чем выше — тем модель умнее и дороже.
    /// Роутер выбирает для Execution самую дешёвую модель, чьё значение не ниже
    /// оценённой сложности изменений.
    /// </summary>
    public double CostEfficiency { get; set; } = 1;

    /// <summary>Цена за миллион входных токенов, USD. 0 — локальная или бесплатная модель.</summary>
    public decimal PricePerMillionInput { get; set; }

    /// <summary>Цена за миллион выходных токенов, USD.</summary>
    public decimal PricePerMillionOutput { get; set; }

    /// <summary>
    /// Поддерживает ли модель принудительную JSON-схему ответа (structured output).
    /// Часть локальных сборок отвечает на <c>response_format: json_schema</c> ошибкой 400 —
    /// для них извлечение находок идёт обычным запросом с разбором JSON из текста.
    /// </summary>
    public bool SupportsStructuredOutput { get; set; } = true;

    /// <summary>
    /// Поддерживает ли модель вызов инструментов. Без него этап анализа работает только
    /// по диффу и контексту влияния, не дочитывая файлы, — и это надо знать заранее,
    /// а не выяснять по 400-й ошибке посреди прогона.
    /// </summary>
    public bool SupportsTools { get; set; } = true;

    /// <summary>Провайдер, которому принадлежит модель. Заполняет роутер при разборе конфига.</summary>
    public LlmProviderOptions Provider { get; set; } = new();

    /// <summary>Стоимость обращения по факту израсходованных токенов.</summary>
    public decimal CostOf(long inputTokens, long outputTokens) =>
        inputTokens * PricePerMillionInput / 1_000_000m +
        outputTokens * PricePerMillionOutput / 1_000_000m;

    public override string ToString() => Name;
}

/// <summary>Секция <c>Llm</c>: список провайдеров и их моделей.</summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    public List<LlmProviderOptions> Providers { get; set; } = [];

    /// <summary>Все модели всех провайдеров с обратной ссылкой на своего провайдера.</summary>
    public IReadOnlyList<ModelOptions> AllModels()
    {
        var models = new List<ModelOptions>();

        foreach (var provider in Providers)
        {
            foreach (var model in provider.Models)
            {
                model.Provider = provider;
                models.Add(model);
            }
        }

        return models;
    }
}
