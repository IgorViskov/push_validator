using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using ReviewAgent.Core.Models;

namespace ReviewAgent.Core.Llm;

/// <summary>
/// Диалог с одной моделью: накопленные сообщения, подключённые инструменты, учёт расхода.
///
/// Цикл Reason→Act→Observe крутит <c>FunctionInvokingChatClient</c> внутри одного
/// <c>GetResponseAsync</c> — сами инструменты вызывать не нужно, достаточно их передать.
/// </summary>
public sealed class Chat
{
    private readonly IChatClient _chatClient;
    private readonly ModelOptions _model;
    private readonly List<ChatMessage> _messages = [];
    private readonly List<AITool> _tools = [];

    public Chat(ModelOptions model, int maxReActIterations)
    {
        _model = model;

        _chatClient = CreateClient(model)
            .GetChatClient(model.Name)
            .AsIChatClient()
            .AsBuilder()
            // Потолок витков цикла Reason→Act→Observe. Без него исчерпанная квота
            // инструмента не останавливает модель: она получает текст отказа и зовёт
            // инструмент снова, а весь цикл укладывается внутрь одного обращения —
            // то есть мимо предохранителя прогона, который считает обращения.
            .UseFunctionInvocation(configure: client =>
                client.MaximumIterationsPerRequest = Math.Max(2, maxReActIterations))
            .Build();
    }

    public ModelOptions Model => _model;

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
    /// Свободный текстовый ответ, читаемый потоково. Каждый фрагмент потока (рассуждения,
    /// текст, вызовы инструментов) отдаётся в <paramref name="onUpdate"/>, чтобы вызывающий
    /// код мог показывать, чем сейчас занята модель, — в консоли это были строки статуса,
    /// в админке это события живого лога.
    /// </summary>
    public async Task<string> GetAnswer(
        Action<ChatResponseUpdate>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions
        {
            Tools = _tools.Count > 0 && _model.SupportsTools ? _tools : null
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
    /// Строго типизированное извлечение результата. Инструменты намеренно не передаются:
    /// function-calling и принудительная JSON-схема на многих моделях конфликтуют, поэтому
    /// структуру извлекаем отдельным запросом из уже накопленного контекста.
    ///
    /// Модель, не умеющая <c>response_format: json_schema</c>, обслуживается вторым путём:
    /// обычный текстовый запрос и разбор JSON из ответа. Локальные сборки отвечают на схему
    /// то 400-й ошибкой, то молчаливым игнорированием формата, и без этого пути половина
    /// доступных моделей была бы непригодна.
    /// </summary>
    public async Task<T?> GetAnswer<T>(CancellationToken cancellationToken = default) where T : class
    {
        if (_model.SupportsStructuredOutput)
        {
            var response = await _chatClient.GetResponseAsync<T>(
                _messages, cancellationToken: cancellationToken);

            Track(response.Usage, response.Text);
            _messages.AddMessages(response);

            if (response.TryGetResult(out var result)) return result;

            // Схема есть, а ответ по ней не разобрался — дальше разбираем как текст:
            // модель почти всегда отдаёт правильный JSON, просто обёрнутый в markdown.
            return Parse<T>(response.Text);
        }

        var text = await GetAnswer(cancellationToken: cancellationToken);
        return Parse<T>(text);
    }

    /// <summary>
    /// Разбор JSON из свободного текста. Модели любят обрамлять ответ пояснением и
    /// markdown-фенсом, поэтому берём подстроку от первой фигурной скобки до парной ей.
    /// </summary>
    private static T? Parse<T>(string text) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var json = ExtractJsonObject(text);
        if (json is null) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Модель ставит "severity": "Critical" там, где в типе enum-подобная строка,
        // и пропускает поля, которых не знает: разбор не должен падать ни на том, ни на другом.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }

        return null;
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

    private static OpenAIClient CreateClient(ModelOptions model)
    {
        var provider = model.Provider;

        // SDK требует непустой ключ, даже когда сервер его не проверяет.
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(provider.ApiKey) ? "no-key-required" : provider.ApiKey);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(Math.Max(30, provider.TimeoutSeconds))
        };

        return new OpenAIClient(credential, options);
    }
}
