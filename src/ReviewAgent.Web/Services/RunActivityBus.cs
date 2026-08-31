using System.Collections.Concurrent;
using ReviewAgent.Core.Review;

namespace ReviewAgent.Web.Services;

/// <param name="RepositoryKey">Чей это прогон.</param>
/// <param name="Event">Что произошло.</param>
public sealed record RunActivity(string RepositoryKey, ReviewProgressEvent Event);

/// <summary>
/// Живой лог прогонов для админки.
///
/// В консольной версии ход проверки печатался в stderr тому, кто её запустил, и других
/// зрителей не было. У сервиса зритель может подключиться посреди прогона и должен увидеть
/// не пустой экран, — поэтому шина хранит последние события и отдаёт их при подписке.
/// Хранилище в памяти намеренно: это диагностика хода, а не журнал; журнал — в базе.
/// </summary>
public sealed class RunActivityBus
{
    private const int HistoryLimit = 300;

    private readonly ConcurrentQueue<RunActivity> _history = new();
    private readonly ConcurrentDictionary<Guid, Action<RunActivity>> _subscribers = new();

    public event Action? Changed;

    public void Publish(RunActivity activity)
    {
        _history.Enqueue(activity);
        while (_history.Count > HistoryLimit) _history.TryDequeue(out _);

        foreach (var subscriber in _subscribers.Values)
        {
            // Отвалившийся подписчик не должен ронять прогон: он всего лишь смотрел.
            try
            {
                subscriber(activity);
            }
            catch (Exception)
            {
                // Игнорируем: подписка — это наблюдение, а не участие.
            }
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<RunActivity> Snapshot(string? repositoryKey = null) =>
        repositoryKey is null
            ? _history.ToArray()
            : _history.Where(a => a.RepositoryKey == repositoryKey).ToArray();

    public IDisposable Subscribe(Action<RunActivity> handler)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = handler;
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    /// <summary>Приёмник хода прогона, публикующий события в шину.</summary>
    public IReviewProgress ProgressFor(string repositoryKey) => new BusProgress(this, repositoryKey);

    private sealed class BusProgress(RunActivityBus bus, string repositoryKey) : IReviewProgress
    {
        public void Report(ReviewProgressEvent progress) =>
            bus.Publish(new RunActivity(repositoryKey, progress));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Собирает события прогона в список — для ответа хуку и для страницы прогона.</summary>
public sealed class CollectingProgress : IReviewProgress
{
    private readonly List<ReviewProgressEvent> _events = [];
    private readonly IReviewProgress? _next;

    public CollectingProgress(IReviewProgress? next = null) => _next = next;

    public IReadOnlyList<ReviewProgressEvent> Events => _events;

    public void Report(ReviewProgressEvent progress)
    {
        lock (_events) _events.Add(progress);
        _next?.Report(progress);
    }
}
