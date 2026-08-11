using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace LLMAgent.Modules.Chats;

/// <summary>
/// Превращает потоковый ответ модели (ChatResponseUpdate) в короткие консольные статусы:
/// чем занята модель — размышляет, формулирует анализ, какой инструмент вызывает.
/// Статус печатается один раз при смене активности, а не на каждый токен;
/// после вызова инструмента активность сбрасывается, чтобы новый виток ReAct был виден.
/// </summary>
public sealed class ChatActivityRenderer
{
    private enum Activity
    {
        None,
        Reasoning,
        Writing
    }

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private Activity _current = Activity.None;

    public void OnUpdate(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    Show(Activity.Reasoning, "🧠 Модель размышляет…");
                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    Show(Activity.Writing, "✍️ Модель формулирует анализ…");
                    break;

                case FunctionCallContent call:
                    _current = Activity.None;
                    Console.Error.WriteLine($"   {Describe(call)}");
                    break;
            }
        }
    }

    /// <summary>Итоговая строка после успешного завершения потока.</summary>
    public void Complete()
    {
        Console.Error.WriteLine($"⏱ Анализ занял {_stopwatch.Elapsed.TotalSeconds:F0} с.");
    }

    private void Show(Activity activity, string status)
    {
        if (_current == activity)
        {
            return;
        }

        _current = activity;
        Console.Error.WriteLine(status);
    }

    private static string Describe(FunctionCallContent call) => call.Name switch
    {
        "read_file" => $"📄 читает файл: {Argument(call, "path")}",
        "git_log" => "🕘 изучает историю git-коммитов",
        "search_files" => $"🔍 ищет файлы: {Argument(call, "pattern")}",
        "graph_impact" => $"🕸 спрашивает граф кода: кто вызывает {Argument(call, "symbol")}",
        _ => $"⚙️ вызывает инструмент {call.Name}"
    };

    private static string Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) && value is not null
            ? value.ToString() ?? "…"
            : "…";
}
