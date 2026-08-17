namespace ReviewAgent.Core.Review;

/// <summary>
/// Узел графа выполнения. В отличие от middleware состояние не вызывает «следующего»:
/// оно возвращает имя состояния, в которое переходит машина. Поэтому переходы могут
/// ветвиться и возвращаться назад, а не идти одной цепочкой.
/// </summary>
public interface IReviewState
{
    /// <summary>Имя узла — по нему на состояние ссылаются переходы.</summary>
    string Name { get; }

    Task<StateTransition> Run(ReviewContext context);
}

/// <summary>
/// Переход графа: следующее состояние и причина выбора. Причина не служебная —
/// именно она печатается в отчёте и показывает, по какой ветке пошёл сценарий.
/// </summary>
/// <param name="Next">Имя следующего состояния; <c>null</c> — сценарий завершён.</param>
/// <param name="Reason">Почему выбран этот переход.</param>
public readonly record struct StateTransition(string? Next, string Reason)
{
    public static StateTransition To(string state, string reason) => new(state, reason);

    public static StateTransition Finish(string reason) => new(null, reason);
}

/// <summary>Имена состояний — узлы графа выполнения.</summary>
public static class ReviewStates
{
    public const string Triage = "triage";
    public const string Impact = "impact";
    public const string Review = "review";
    public const string Validate = "validate";
    public const string Arbiter = "arbiter";
    public const string Report = "report";
}

/// <summary>Метки этапов в находках — по ним отчёт и арбитр отличают, кто что нашёл.</summary>
public static class Stages
{
    /// <summary>
    /// Находки предохранителей, а не моделей: инъекции в диффе, отказ схемы, исчерпанный
    /// бюджет. Арбитр их не пересматривает — иначе вердикт о безопасности выносила бы
    /// та самая модель, на которую и было направлено воздействие.
    /// </summary>
    public const string Security = "Безопасность";

    /// <summary>Сбой самого состояния — не находка о коде, а отказ агента.</summary>
    public const string Engine = "Сбой";

    public const string Impact = "Влияние";
    public const string Review = "Анализ";
    public const string Validate = "Валидация";
    public const string Arbiter = "Арбитраж";
}
