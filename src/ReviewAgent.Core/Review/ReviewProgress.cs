namespace ReviewAgent.Core.Review;

public enum ReviewProgressKind
{
    /// <summary>Начался этап графа состояний.</summary>
    Stage,

    /// <summary>Переход между состояниями с причиной выбора ветки.</summary>
    Transition,

    /// <summary>Модель вызвала инструмент.</summary>
    Tool,

    /// <summary>Модель размышляет или формулирует ответ.</summary>
    Model,

    /// <summary>Обращение к графу кода.</summary>
    Graph,

    Info,
    Warning,

    /// <summary>Прогон закончен, вердикт вынесен.</summary>
    Done
}

/// <param name="Kind">Что произошло — по нему UI выбирает значок и цвет.</param>
/// <param name="Message">Готовая к показу строка на русском.</param>
public sealed record ReviewProgressEvent(ReviewProgressKind Kind, string Message)
{
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
}

/// <summary>
/// Куда уходит ход прогона. В консольной версии это был рендерер, печатавший статусы
/// в stderr; теперь получателей два — живой лог в админке и текстовый отчёт, который
/// хук печатает разработчику. Оба получают одни и те же события.
/// </summary>
public interface IReviewProgress
{
    void Report(ReviewProgressEvent progress);
}

/// <summary>Приёмник, который ничего не делает: нужен для прогонов без наблюдателя.</summary>
public sealed class NullReviewProgress : IReviewProgress
{
    public static readonly NullReviewProgress Instance = new();

    public void Report(ReviewProgressEvent progress)
    {
    }
}

public static class ReviewProgressExtensions
{
    public static void Info(this IReviewProgress progress, string message) =>
        progress.Report(new ReviewProgressEvent(ReviewProgressKind.Info, message));

    public static void Warn(this IReviewProgress progress, string message) =>
        progress.Report(new ReviewProgressEvent(ReviewProgressKind.Warning, message));

    public static void Stage(this IReviewProgress progress, string message) =>
        progress.Report(new ReviewProgressEvent(ReviewProgressKind.Stage, message));

    public static void Graph(this IReviewProgress progress, string message) =>
        progress.Report(new ReviewProgressEvent(ReviewProgressKind.Graph, message));
}
