namespace ReviewAgent.Core.Models;

/// <summary>
/// Предохранители агентного цикла (секция <c>Guardrails</c>).
/// Значения по умолчанию рассчитаны на один прогон проверки пуша: после исчерпания лимита
/// прогон завершается отчётом, а не ждёт восстановления.
///
/// Раньше все предохранители были singleton — процесс жил ровно одну проверку и умирал.
/// Теперь агент живёт в контейнере и обслуживает много пушей, поэтому счётчики прогона
/// регистрируются как scoped, а один прогон = одна область DI. Настройки при этом
/// остаются общими: это конфигурация, а не состояние.
/// </summary>
public sealed class GuardrailOptions
{
    public const string Section = "Guardrails";

    public BudgetOptions Budget { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public ToolLimitOptions Tools { get; set; } = new();
    public OutputOptions Output { get; set; } = new();

    /// <summary>Предел переходов по графу состояний — страховка от ошибки в условии перехода.</summary>
    public int MaxTransitions { get; set; } = 12;
}

/// <summary>
/// Потолки одного прогона. Проверяются перед каждым обращением к модели и после
/// каждого состояния: превышение любого — аварийная остановка с переходом в отчёт.
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>Предел стоимости, USD. 0 — не ограничивать (локальные модели бесплатны).</summary>
    public decimal MaxCostUsd { get; set; } = 0.50m;

    /// <summary>Предел суммы входных и выходных токенов. Работает и там, где цена нулевая.</summary>
    public long MaxTokens { get; set; } = 400_000;

    /// <summary>
    /// Предел времени прогона. Пуш ждёт ответа синхронно, поэтому потолок нужен щедрый,
    /// но конечный: локальная 30B-модель отвечает минутами, а зависший хук — минутами вечно.
    /// </summary>
    public int MaxDurationSeconds { get; set; } = 900;

    /// <summary>Предел числа обращений к моделям — страховка от петли в графе состояний.</summary>
    public int MaxModelCalls { get; set; } = 12;

    /// <summary>Предел числа обращений к графу кода за прогон.</summary>
    public int MaxGraphQueries { get; set; } = 8;
}

public sealed class RetryOptions
{
    /// <summary>Сколько раз повторить обращение к модели сверх первой попытки.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Базовая задержка; растёт экспоненциально с номером попытки.</summary>
    public int BaseDelayMs { get; set; } = 700;
}

public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// Сколько неудач подряд по одной модели переводят её в «разомкнутое» состояние.
    /// Дальнейшие обращения к ней в этом прогоне не выполняются вовсе.
    /// </summary>
    public int FailureThreshold { get; set; } = 2;
}

/// <summary>Квоты на инструменты за прогон: и от петель ReAct, и от перебора репозитория.</summary>
public sealed class ToolLimitOptions
{
    public int ReadFile { get; set; } = 25;
    public int SearchFiles { get; set; } = 15;
    public int GitLog { get; set; } = 3;
    public int GraphImpact { get; set; } = 5;

    /// <summary>
    /// Сколько витков «модель просит инструмент → получает результат» допускается в одном
    /// обращении к модели.
    ///
    /// Квоты выше ограничивают работу инструментов, но не число обращений к ним: исчерпанная
    /// квота возвращает модели текст «лимит исчерпан, заканчивай анализ», и модель вольна
    /// позвать инструмент снова. На проверке это и случилось — модель ходила по кругу,
    /// получая отказ за отказом, минут двадцать. Предохранитель прогона её не остановил:
    /// весь цикл ReAct укладывается внутрь одного обращения к модели, а бюджет проверяется
    /// между обращениями.
    ///
    /// Поэтому потолок нужен именно здесь, на уровне цикла.
    /// </summary>
    public int MaxReActIterations { get; set; } = 6;

    /// <summary>
    /// Общий потолок вызовов инструментов за прогон. Отдельно от потолка витков, потому что
    /// один виток — это не один вызов: модель вправе запросить десятки инструментов сразу,
    /// и на проверке так и вышло — 6 витков превратились в 328 обращений.
    ///
    /// По исчерпании инструменты начинают бросать исключение, а не возвращать текст отказа.
    /// Это не грубость, а единственный способ остановить цикл: три подряд ошибки инструмента
    /// завершают его силами самого <c>FunctionInvokingChatClient</c>, тогда как вежливый
    /// отказ строкой модель читает как повод попробовать ещё раз.
    /// </summary>
    public int MaxTotalToolCalls { get; set; } = 60;

    /// <summary>
    /// Разрешать ли инструментам выходить за пределы репозитория. В консольной версии на этом
    /// месте стоял вопрос пользователю в терминал; у агента в контейнере терминала нет,
    /// спросить некого — значит, решение принимается конфигурацией, и по умолчанию это «нет».
    /// </summary>
    public bool AllowOutsideRepository { get; set; }

    /// <summary>
    /// Маски файлов, которые инструментам читать нельзя, даже внутри репозитория.
    /// Секреты не должны попадать в промпт, а оттуда — в текст находки и в журнал.
    /// </summary>
    public string[] DeniedFiles { get; set; } =
    [
        "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", ".env", ".env.*",
        "launchSettings.json", "secrets.json", "*.user", "appsettings.*.json"
    ];
}

/// <summary>Строгая проверка того, что вернула модель.</summary>
public sealed class OutputOptions
{
    /// <summary>Сколько находок принимается от одного этапа.</summary>
    public int MaxFindingsPerStage { get; set; } = 20;

    /// <summary>Предельная длина текста находки — защита от «простыни» вместо описания.</summary>
    public int MaxMessageChars { get; set; } = 600;

    /// <summary>Проверять, что указанный в находке файл действительно существует.</summary>
    public bool VerifyFilePaths { get; set; } = true;
}
