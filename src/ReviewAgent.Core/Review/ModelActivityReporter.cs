using Microsoft.Extensions.AI;

namespace ReviewAgent.Core.Review;

/// <summary>
/// Превращает потоковый ответ модели в короткие события хода прогона: чем занята модель —
/// размышляет, формулирует анализ, какой инструмент вызывает.
///
/// Событие отправляется один раз при смене активности, а не на каждый токен; после вызова
/// инструмента активность сбрасывается, чтобы новый виток ReAct был виден.
/// </summary>
public sealed class ModelActivityReporter
{
    private enum Activity
    {
        None,
        Reasoning,
        Writing
    }

    private readonly IReviewProgress _progress;
    private Activity _current = Activity.None;

    public ModelActivityReporter(IReviewProgress progress) => _progress = progress;

    public void OnUpdate(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    Show(Activity.Reasoning, "модель размышляет…");
                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    Show(Activity.Writing, "модель формулирует анализ…");
                    break;

                case FunctionCallContent call:
                    _current = Activity.None;
                    _progress.Report(new ReviewProgressEvent(ReviewProgressKind.Tool, Describe(call)));
                    break;
            }
        }
    }

    private void Show(Activity activity, string status)
    {
        if (_current == activity) return;

        _current = activity;
        _progress.Report(new ReviewProgressEvent(ReviewProgressKind.Model, status));
    }

    private static string Describe(FunctionCallContent call) => call.Name switch
    {
        "read_file" => $"читает файл: {Argument(call, "path")}",
        "git_log" => "изучает историю git-коммитов",
        "search_files" => $"ищет файлы: {Argument(call, "pattern")}",
        "graph_impact" => $"спрашивает граф кода: кто вызывает {Argument(call, "symbol")}",
        _ => $"вызывает инструмент {call.Name}"
    };

    private static string Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) && value is not null
            ? value.ToString() ?? "…"
            : "…";
}
