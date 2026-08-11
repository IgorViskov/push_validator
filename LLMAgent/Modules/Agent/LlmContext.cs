using LLMAgent.Models;
using LLmSeracher.Core.Context;

namespace LLMAgent.Modules.Agent;

/// <summary>
/// Шина данных, которую состояния графа последовательно наполняют:
/// дифф → сложность и затронутые символы → контекст влияния из графа кода →
/// находки анализа и валидации → вердикт арбитра → решение о пуше.
/// </summary>
public sealed class LlmContext
{
    public required string RepoPath { get; init; }

    /// <summary>Git-дифф последних изменений, анализируемых агентом.</summary>
    public string Diff { get; set; } = string.Empty;

    /// <summary>Оценка когнитивной сложности изменений (1..10), выставляется триажем.</summary>
    public int ComplexityScore { get; set; }

    /// <summary>Модель, выбранная когнитивным роутером для этапа анализа.</summary>
    public ModelSetting? ExecutionModel { get; set; }

    /// <summary>Все находки со всех этапов.</summary>
    public List<Finding> Findings { get; } = [];

    /// <summary>Итоговое разрешение на пуш (false — приостановлен пользователем).</summary>
    public bool AllowPush { get; set; } = true;

    public bool HasCritical => Findings.Any(f => f.Severity == Severity.Critical);

    public CancellationToken CancellationToken { get; init; }

    // ── Взаимодействие с сетью агентов LLmSeracher ────────────────────────────────────

    /// <summary>
    /// Сквозной идентификатор диалога: связывает все делегирования одной проверки
    /// в цепочку, по которой их видно на стороне хоста агентов.
    /// </summary>
    public string ConversationId { get; } = Guid.NewGuid().ToString("n")[..12];

    /// <summary>Символы, затронутые диффом, — по ним запрашивается контекст влияния.</summary>
    public List<string> ChangedSymbols { get; } = [];

    /// <summary>
    /// Фрагменты кода, подключённые агентом-ретривером из графа: вызывающий код,
    /// реализации, регистрации. Пусто — граф не спрашивали либо он не ответил.
    /// </summary>
    public List<ContextChunk> ImpactChunks { get; } = [];

    /// <summary>Обращались ли к графу — отличает «не спрашивали» от «спросили, пусто».</summary>
    public bool ImpactRequested { get; set; }

    public bool HasImpact => ImpactChunks.Count > 0;

    /// <summary>Уточняющий запрос к графу; заполняет арбитр, когда ему не хватает фактов.</summary>
    public string? ImpactQuery { get; set; }

    /// <summary>Сколько раз арбитр уже отправлял сценарий на второй проход.</summary>
    public int ArbiterRetries { get; set; }

    /// <summary>Пройденный путь по графу состояний — печатается в отчёте.</summary>
    public List<TraceEntry> Trace { get; } = [];

    /// <summary>
    /// Добавляет фрагменты графа, отсеивая уже подключённые. Источников два — состояние
    /// <c>impact</c> и инструмент <c>graph_impact</c> внутри ReAct, — а счётчик в отчёте
    /// и блок контекста для арбитра должны быть общими.
    /// </summary>
    /// <returns>Сколько фрагментов оказалось новыми.</returns>
    public int AddImpactChunks(IEnumerable<ContextChunk> chunks)
    {
        var added = 0;

        foreach (var chunk in chunks)
        {
            if (ImpactChunks.Any(existing => existing.Key == chunk.Key)) continue;

            ImpactChunks.Add(chunk);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Заменяет находки одного этапа. Нужно из-за возвратов в графе: повторный анализ
    /// должен вытеснить свой прошлый результат, а не удвоить список.
    /// </summary>
    public void ReplaceFindings(string stage, IEnumerable<Finding> findings)
    {
        Findings.RemoveAll(f => f.Stage == stage);
        Findings.AddRange(findings);
    }
}
