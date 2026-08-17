namespace ReviewAgent.CodeGraph;

/// <summary>
/// Фрагмент кода, подключаемый к промпту ревьюера: тело символа плюс объяснение,
/// почему он здесь оказался.
///
/// До объединения проектов на этом месте был <c>ContextChunk</c> — универсальная единица
/// контекста, которую умели отдавать три источника (markdown-справка, HTTP-API, граф) и
/// которая ездила по сети между агентами в JSON. Источник остался один, сеть исчезла,
/// поэтому от универсальности осталось только лишнее: половина полей никогда не заполнялась,
/// а <c>Score</c> нормировался дважды — в графе и в композите поверх него.
/// </summary>
/// <param name="SymbolId">Идентификатор символа в графе; стабилен между сборками.</param>
/// <param name="Title">Полное имя символа — заголовок фрагмента в промпте.</param>
/// <param name="Text">Тело символа (плюс XML-документация, если она есть).</param>
/// <param name="Score">Релевантность запросу после ранжирования обхода, 0..1.</param>
/// <param name="Rationale">
/// Путь в графе, по которому фрагмент подключён: «вызывает GetTotal», «реализация IBasket».
/// Не украшение — без него модель видит набор чужих методов и не понимает, при чём они здесь.
/// </param>
public sealed record CodeChunk(
    string SymbolId,
    string Title,
    string Text,
    double Score,
    string Rationale)
{
    /// <summary>Вид фрагмента: Type, Method, Property, Field или GraphEdges для блока связей.</summary>
    public string? Kind { get; init; }

    /// <summary>Путь файла относительно корня репозитория.</summary>
    public string? FilePath { get; init; }

    public int? StartLine { get; init; }
    public int? EndLine { get; init; }

    /// <summary>Язык для подсветки в промпте. Пусто у блока связей — это не код.</summary>
    public string? Language { get; init; } = "csharp";

    /// <summary>Ссылка на место в коде для цитирования: <c>path/File.cs:24-62</c>.</summary>
    public string? Location => FilePath is null
        ? null
        : StartLine is null ? FilePath : $"{FilePath}:{StartLine}-{EndLine ?? StartLine}";

    /// <summary>Ключ дедупликации: один символ не должен попасть в промпт дважды.</summary>
    public string Key => SymbolId;
}
