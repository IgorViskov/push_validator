using System.ClientModel;
using LLMAgent.Models;
using OpenAI;
using OpenAI.Chat;

namespace LLMAgent.Modules.Chats;

public sealed class Chat
{
    private const int MaxToolIterations = 6;

    private readonly ChatClient _chatClient;
    private readonly List<ChatMessage> _chatMessages = [];
    private readonly Dictionary<string, AgentTool> _tools = [];

    public Chat(ModelSetting settings)
    {
        var client = GetClient(settings);
        _chatClient = client.GetChatClient(settings.Name);
    }

    public Chat AddPrompt(string prompt)
    {
        _chatMessages.Add(new SystemChatMessage(prompt));
        return this;
    }

    public Chat AddMessage(string message)
    {
        _chatMessages.Add(new UserChatMessage(message));
        return this;
    }

    public Chat AddTool(AgentTool tool)
    {
        _tools[tool.Definition.FunctionName] = tool;
        return this;
    }

    /// <summary>
    /// Запускает обмен с моделью. Если модель просит вызвать инструменты (function-calling),
    /// выполняет их и возвращает результат в диалог — это и есть цикл Reason→Act→Observe.
    /// </summary>
    public async Task<string> GetAnswer(CancellationToken cancellationToken = default)
    {
        var options = new ChatCompletionOptions();
        foreach (var tool in _tools.Values)
        {
            options.Tools.Add(tool.Definition);
        }

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var completion = await _chatClient.CompleteChatAsync(_chatMessages, options, cancellationToken);
            var result = completion.Value;

            if (result.FinishReason == ChatFinishReason.ToolCalls && result.ToolCalls.Count > 0)
            {
                _chatMessages.Add(new AssistantChatMessage(result));
                foreach (var call in result.ToolCalls)
                {
                    var output = await ExecuteTool(call, cancellationToken);
                    _chatMessages.Add(new ToolChatMessage(call.Id, output));
                }

                continue;
            }

            var text = result.Content.Count > 0 ? result.Content[0].Text : string.Empty;
            _chatMessages.Add(new AssistantChatMessage(text));
            return text;
        }

        return "Превышен лимит итераций инструментов без финального ответа.";
    }

    private async Task<string> ExecuteTool(ChatToolCall call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.FunctionName, out var tool))
        {
            return $"Инструмент '{call.FunctionName}' не зарегистрирован.";
        }

        Console.Error.WriteLine($"🔧 function-call: {call.FunctionName}({call.FunctionArguments})");

        try
        {
            return await tool.Execute(call.FunctionArguments.ToString(), cancellationToken);
        }
        catch (Exception e)
        {
            return $"Ошибка инструмента '{call.FunctionName}': {e.Message}";
        }
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
