using ReviewAgent.Core.Models;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace ReviewAgent.Core.Safety;

/// <summary>
/// Предохранитель прогона: единственное место, где решается, можно ли потратить ещё.
/// Проверяется перед каждым обращением к модели, перед каждым делегированием и после
/// каждого состояния графа — сработавший лимит переводит прогон в аварийную остановку,
/// а не просто пропускает одно действие.
/// </summary>
public sealed class RunGuard
{
    private readonly BudgetOptions _budget;
    private readonly RunMetrics _metrics;
    private readonly ILogger<RunGuard> _logger;

    public RunGuard(IOptions<GuardrailOptions> options, RunMetrics metrics, ILogger<RunGuard> logger)
    {
        _budget = options.Value.Budget;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Можно ли обратиться к модели. Причина отказа уже записана в метрики.</summary>
    public bool CanCallModel(out string? reason)
    {
        if (Exceeded(out reason)) return false;

        if (_budget.MaxModelCalls > 0 && _metrics.ModelCallCount >= _budget.MaxModelCalls)
        {
            reason = $"исчерпан лимит обращений к моделям ({_budget.MaxModelCalls})";
            Trip(reason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Можно ли ещё раз спросить граф кода. Потолок против петли «арбитр просит контекст →
    /// анализ снова спрашивает граф», которая иначе упирается только в терпение пользователя.
    /// </summary>
    public bool CanQueryGraph(out string? reason)
    {
        if (Exceeded(out reason)) return false;

        if (_budget.MaxGraphQueries > 0 && _metrics.GraphQueries >= _budget.MaxGraphQueries)
        {
            reason = $"исчерпан лимит обращений к графу кода ({_budget.MaxGraphQueries})";
            Trip(reason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверка между состояниями графа. Возвращает причину, по которой прогон
    /// нужно немедленно свести к отчёту.
    /// </summary>
    public bool ShouldAbort(out string? reason)
    {
        if (_metrics.Aborted)
        {
            reason = _metrics.AbortReason;
            return true;
        }

        return Exceeded(out reason);
    }

    /// <summary>Общие потолки прогона: деньги, токены, время.</summary>
    private bool Exceeded(out string? reason)
    {
        if (_budget.MaxDurationSeconds > 0 && _metrics.Elapsed.TotalSeconds > _budget.MaxDurationSeconds)
        {
            reason = $"превышено время прогона ({_metrics.Elapsed.TotalSeconds:F0} с > {_budget.MaxDurationSeconds} с)";
            Trip(reason);
            return true;
        }

        if (_budget.MaxCostUsd > 0 && _metrics.CostUsd >= _budget.MaxCostUsd)
        {
            reason = $"исчерпан бюджет стоимости (${_metrics.CostUsd:F4} ≥ ${_budget.MaxCostUsd:F2})";
            Trip(reason);
            return true;
        }

        if (_budget.MaxTokens > 0 && _metrics.TotalTokens >= _budget.MaxTokens)
        {
            reason = $"исчерпан бюджет токенов ({_metrics.TotalTokens} ≥ {_budget.MaxTokens})";
            Trip(reason);
            return true;
        }

        reason = null;
        return false;
    }

    private void Trip(string reason)
    {
        if (_metrics.Aborted) return;

        _logger.LogWarning("Предохранитель: {Reason} — прогон сворачивается к отчёту.", reason);
        _metrics.Abort(reason);
    }
}
