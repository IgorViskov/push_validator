namespace ReviewAgent.CodeGraph;

/// <summary>
/// Настройки обращения к графу кода за контекстом влияния (секция <c>Impact</c>).
/// </summary>
public sealed class ImpactOptions
{
    public const string Section = "Impact";

    /// <summary>Спрашивать ли граф кода вообще. false — проверка идёт только по диффу.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// С какой сложности изменений идти за контекстом влияния. Ниже порога обращение
    /// не окупается: правка текста не ломает вызывающий код.
    /// </summary>
    public int ComplexityThreshold { get; set; } = 4;

    /// <summary>Сколько изменённых символов попадёт в запрос к графу.</summary>
    public int MaxSymbols { get; set; } = 5;

    /// <summary>Сколько фрагментов запрашивать у графа, включая блок связей.</summary>
    public int MaxChunks { get; set; } = 10;

    /// <summary>Бюджет блока контекста влияния в промпте, символов.</summary>
    public int MaxContextChars { get; set; } = 6000;

    /// <summary>Сколько раз арбитр может отправить сценарий на второй проход.</summary>
    public int MaxArbiterRetries { get; set; } = 1;

    /// <summary>
    /// Обновлять ли граф перед анализом. Выключается только для замеров: с выключенным
    /// граф отстаёт от кода на все изменения текущего пуша, и «кто вызывает» отвечает
    /// по прошлому состоянию.
    /// </summary>
    public bool RefreshGraphBeforeReview { get; set; } = true;
}
