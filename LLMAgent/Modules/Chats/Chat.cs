using System.ClientModel;
using LLMAgent.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LLMAgent.Modules.Chats;

public sealed class Chat
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _messages = [];
    private readonly List<AITool> _tools = [];

    public Chat(ModelSetting settings)
    {
        var openAiClient = GetClient(settings);

        // FunctionInvokingChatClient сам выполняет вызовы инструментов и крутит
        // цикл Reason→Act→Observe внутри одного GetResponseAsync.
        _chatClient = openAiClient
            .GetChatClient(settings.Name)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    public Chat AddPrompt(string prompt)
    {
        _messages.Add(new ChatMessage(ChatRole.System, prompt));
        return this;
    }

    public Chat AddMessage(string message)
    {
        _messages.Add(new ChatMessage(ChatRole.User, message));
        return this;
    }

    public Chat AddTool(AITool tool)
    {
        _tools.Add(tool);
        return this;
    }

    /// <summary>Израсходовано входных токенов за всё время жизни чата.</summary>
    public long InputTokens { get; private set; }

    /// <summary>Израсходовано выходных токенов.</summary>
    public long OutputTokens { get; private set; }

    /// <summary>Пришёл ли расход от провайдера. false — значения оценены по длине текста.</summary>
    public bool UsageReported { get; private set; } = true;

    /// <summary>
    /// Свободный текстовый ответ, читаемый потоково. Сам цикл вызова инструментов
    /// (Act→Observe→повтор) выполняет FunctionInvokingChatClient, подключённый в конструкторе
    /// через UseFunctionInvocation(); здесь — единственный запрос, запускающий этот цикл.
    /// Каждый фрагмент потока (рассуждения, текст, вызовы инструментов) отдаётся в onUpdate,
    /// чтобы вызывающий код мог показывать, чем сейчас занята модель.
    /// </summary>
    public async Task<string> GetAnswer(Action<ChatResponseUpdate>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions
        {
            Tools = _tools.Count > 0 ? _tools : null
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(_messages, options, cancellationToken))
        {
            updates.Add(update);
            onUpdate?.Invoke(update);
        }

        var response = updates.ToChatResponse();
        Track(response.Usage, response.Text);
        _messages.AddMessages(response);
        return response.Text;
    }

    /// <summary>
    /// Строго типизированное извлечение результата через structured output
    /// (response_format по JSON-схеме, выведенной из типа T). Инструменты намеренно
    /// не передаются: function-calling и принудительная JSON-схема на многих моделях конфликтуют,
    /// поэтому структуру извлекаем отдельным запросом из уже накопленного контекста.
    /// </summary>
    public async Task<T?> GetAnswer<T>(CancellationToken cancellationToken = default)
    {
        var response = await _chatClient.GetResponseAsync<T>(_messages, cancellationToken: cancellationToken);
        Track(response.Usage, response.Text);
        _messages.AddMessages(response);
        return response.TryGetResult(out var result) ? result : default;
    }

    /// <summary>
    /// Учёт расхода. Провайдер возвращает usage не всегда — в потоковом режиме это обычное
    /// дело, — а бюджет прогона должен на что-то опираться. Поэтому при отсутствии данных
    /// расход оценивается по длине текста (≈4 символа на токен) и помечается как оценка.
    /// </summary>
    private void Track(UsageDetails? usage, string answer)
    {
        if (usage?.InputTokenCount is { } input && usage.OutputTokenCount is { } output)
        {
            InputTokens += input;
            OutputTokens += output;
            return;
        }

        UsageReported = false;
        InputTokens += _messages.Sum(m => m.Text?.Length ?? 0) / 4;
        OutputTokens += answer.Length / 4;
    }

    private static OpenAIClient GetClient(ModelSetting setting)
    {
        var credential = new ApiKeyCredential(setting.ApiSettings.ApiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(setting.ApiSettings.Endpoint),
            NetworkTimeout = TimeSpan.FromMinutes(60)
        };

        return new OpenAIClient(credential, options);
    }
}
