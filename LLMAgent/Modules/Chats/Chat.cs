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

    /// <summary>
    /// Свободный текстовый ответ. Сам цикл вызова инструментов (Act→Observe→повтор)
    /// выполняет FunctionInvokingChatClient, подключённый в конструкторе через
    /// UseFunctionInvocation(); здесь — единственный запрос, запускающий этот цикл.
    /// </summary>
    public async Task<string> GetAnswer(CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions
        {
            Tools = _tools.Count > 0 ? _tools : null
        };

        var response = await _chatClient.GetResponseAsync(_messages, options, cancellationToken);
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
        _messages.AddMessages(response);
        return response.TryGetResult(out var result) ? result : default;
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
