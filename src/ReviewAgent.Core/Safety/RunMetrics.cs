using System.Diagnostics;

namespace ReviewAgent.Core.Safety;

/// <summary>Чем закончился прогон — главная метрика качества.</summary>
public enum RunOutcome
{
    /// <summary>Все запланированные этапы отработали, вердикт получен по существу.</summary>
    Success,

    /// <summary>Вердикт есть, но часть этапов работала на запасном пути (модель, граф, схема).</summary>
    Degraded,

    /// <summary>Прогон остановлен предохранителем: бюджет, время, предохранитель модели.</summary>
    Aborted
}

/// <param name="Stage">Состояние графа, из которого шло обращение.</param>
/// <param name="Model">Имя модели.</param>
/// <param name="Attempts">Сколько попыток потребовалось (1 — с первой).</param>
/// <param name="Outcome">ok | invalid | failed | skipped.</param>
public sealed record ModelCallMetric(
    string Stage,
    string Model,
    int Attempts,
    double ElapsedMs,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    string Outcome,
    string? Error = null);

/// <param name="Kind">
/// Какой предохранитель сработал: budget, retry, circuit-breaker, tool-quota,
/// denied-file, injection, output-schema, graph-limit, transition-limit, abort.
/// </param>
public sealed record GuardrailEvent(string Kind, string Detail, double AtMs);

public sealed record StateMetric(string State, double ElapsedMs);

/// <summary>
/// Метрики одного прогона: успех, время, стоимость. Собираются по ходу графа состояний
/// и в конце пишутся в журнал — иначе судить об агенте можно только по последней строке
/// консоли, а она ничего не говорит ни о цене, ни о том, сколько раз он спотыкался.
/// </summary>
public sealed class RunMetrics
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<ModelCallMetric> _modelCalls = [];
    private readonly List<GuardrailEvent> _guardrailEvents = [];
    private readonly List<StateMetric> _states = [];
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.Ordinal);
    private readonly List<string> _degradations = [];

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string ConversationId { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;

    public IReadOnlyList<ModelCallMetric> ModelCalls => _modelCalls;
    public IReadOnlyList<GuardrailEvent> GuardrailEvents => _guardrailEvents;
    public IReadOnlyList<StateMetric> States => _states;
    public IReadOnlyDictionary<string, int> ToolCalls => _toolCalls;
    public IReadOnlyList<string> Degradations => _degradations;

    /// <summary>Сколько раз за прогон агент обращался к графу кода.</summary>
    public int GraphQueries { get; private set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public long InputTokens => _modelCalls.Sum(c => c.InputTokens);
    public long OutputTokens => _modelCalls.Sum(c => c.OutputTokens);
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal CostUsd => _modelCalls.Sum(c => c.CostUsd);
    public int ModelCallCount => _modelCalls.Count(c => c.Outcome != "skipped");

    /// <summary>Причина аварийной остановки; заполняется предохранителем.</summary>
    public string? AbortReason { get; private set; }

    public bool Aborted => AbortReason is not null;

    public double NowMs => _stopwatch.Elapsed.TotalMilliseconds;

    public void AddModelCall(ModelCallMetric call) => _modelCalls.Add(call);

    public void AddState(string state, double elapsedMs) => _states.Add(new StateMetric(state, elapsedMs));

    public void AddToolCall(string tool) =>
        _toolCalls[tool] = _toolCalls.GetValueOrDefault(tool) + 1;

    public int ToolCallCount(string tool) => _toolCalls.GetValueOrDefault(tool);

    public void AddGraphQuery() => GraphQueries++;

    public void AddGuardrailEvent(string kind, string detail) =>
        _guardrailEvents.Add(new GuardrailEvent(kind, detail, NowMs));

    /// <summary>Строки недоверенного текста, адресованные модели, а не человеку.</summary>
    public int InjectionHits { get; private set; }

    public void AddInjection(int hits, string detail)
    {
        InjectionHits += hits;
        AddGuardrailEvent("injection", detail);
    }

    /// <summary>
    /// Отметка о работе на запасном пути. Прогон при этом доводится до конца,
    /// но успехом он уже не считается — иначе «модель молчала, зато не упали»
    /// выглядит в журнале так же, как полноценная проверка.
    /// </summary>
    public void Degrade(string reason)
    {
        if (!_degradations.Contains(reason, StringComparer.Ordinal))
            _degradations.Add(reason);
    }

    public void Abort(string reason)
    {
        AbortReason ??= reason;
        AddGuardrailEvent("abort", reason);
    }

    public RunOutcome Outcome =>
        Aborted ? RunOutcome.Aborted :
        _degradations.Count > 0 ? RunOutcome.Degraded :
        RunOutcome.Success;
}
