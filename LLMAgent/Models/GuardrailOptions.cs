namespace LLMAgent.Models;

/// <summary>
/// Предохранители агентного цикла (секция <c>Guardrails</c> в appsettings.json).
/// Значения по умолчанию рассчитаны на один прогон проверки коммита, а не на сервис:
/// после исчерпания лимита прогон завершается отчётом, а не ждёт восстановления.
/// </summary>
public sealed class GuardrailOptions
{
    public BudgetOptions Budget { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public ToolLimitOptions Tools { get; set; } = new();
    public OutputOptions Output { get; set; } = new();

    /// <summary>Куда писать журнал прогона; пусто — рядом с бинарём, каталог <c>logs</c>.</summary>
    public string MetricsPath { get; set; } = "logs";
}

/// <summary>
/// Потолки одного прогона. Проверяются перед каждым обращением к модели и после
/// каждого состояния: превышение любого — аварийная остановка с переходом в отчёт.
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>Предел стоимости, USD. 0 — не ограничивать (бесплатные модели).</summary>
    public decimal MaxCostUsd { get; set; } = 0.50m;

    /// <summary>Предел суммы входных и выходных токенов. Работает и там, где цена нулевая.</summary>
    public long MaxTokens { get; set; } = 400_000;

    /// <summary>Предел времени прогона: pre-push hook не должен висеть.</summary>
    public int MaxDurationSeconds { get; set; } = 900;

    /// <summary>Предел числа обращений к моделям — страховка от петли в графе состояний.</summary>
    public int MaxModelCalls { get; set; } = 12;

    /// <summary>Предел числа задач, отправленных чужим агентам.</summary>
    public int MaxDelegations { get; set; } = 8;
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
    /// Маски файлов, которые инструментам читать нельзя, даже внутри репозитория.
    /// Секреты не должны попадать в промпт, а оттуда — в текст находки.
    /// </summary>
    public string[] DeniedFiles { get; set; } =
    [
        "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", ".env", ".env.*",
        "launchSettings.json", "secrets.json", "*.user"
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
