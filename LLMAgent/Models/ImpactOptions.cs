namespace LLMAgent.Models;

/// <summary>
/// Настройки обращения к сети агентов LLmSeracher за контекстом влияния
/// (секция <c>Impact</c> в appsettings.json).
/// </summary>
public sealed class ImpactOptions
{
    /// <summary>Спрашивать ли граф кода вообще. false — сценарий работает только по диффу.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// С какой сложности изменений идти за контекстом влияния. Ниже порога обращение
    /// к чужому агенту не окупается: правка текста не ломает вызывающий код.
    /// </summary>
    public int ComplexityThreshold { get; set; } = 4;

    /// <summary>Сколько изменённых символов попадёт в запрос к графу.</summary>
    public int MaxSymbols { get; set; } = 5;

    /// <summary>Бюджет блока контекста влияния в промпте, символов.</summary>
    public int MaxContextChars { get; set; } = 6000;

    /// <summary>Сколько раз арбитр может отправить сценарий на второй проход.</summary>
    public int MaxArbiterRetries { get; set; } = 1;
}
