using ReviewAgent.Core.Models;

namespace ReviewAgent.Core.Review.States;

/// <summary>
/// Шаг 6. Отчёт: гейт пуша.
///
/// В консольной версии это состояние печатало отчёт и спрашивало разработчика прямо
/// в терминале: «разрешить пуш несмотря на критические ошибки?». У агента в контейнере
/// терминала нет, и спрашивать оттуда некого. Роль «диктатора» никуда не делась — она
/// переехала на сторону хука: агент отдаёт вердикт, а последнее слово остаётся за человеком,
/// который видит отчёт в своём терминале и решает, продавливать пуш или нет.
/// </summary>
public sealed class ReportState : IReviewState
{
    public string Name => ReviewStates.Report;

    public Task<StateTransition> Run(ReviewContext context)
    {
        context.AllowPush = !context.HasCritical;

        if (context.AllowPush)
        {
            context.Progress.Report(new ReviewProgressEvent(ReviewProgressKind.Done,
                "критических находок нет — пуш разрешён"));

            return Task.FromResult(StateTransition.Finish("пуш разрешён"));
        }

        var critical = context.Findings.Count(f => f.Severity == Severity.Critical);
        context.Progress.Report(new ReviewProgressEvent(ReviewProgressKind.Done,
            $"критических находок {critical} — пуш остановлен"));

        return Task.FromResult(StateTransition.Finish($"пуш остановлен: критических находок {critical}"));
    }
}
