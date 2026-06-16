using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Chats;
using LLMAgent.Modules.ErrorsModule.Exceptions;
using LLMAgent.Modules.Logging;
using LLMAgent.Prompts;

namespace LLMAgent.Modules.Router;

public sealed class CognitiveRouter
{
    private readonly Dictionary<CognitiveRoutingType, ModelSetting[]> _settings;
    private readonly Logger _logger;

    public CognitiveRouter(IEnumerable<ApiSettings> apiSettings, Logger logger)
    {
        _logger = logger;
        _settings = apiSettings
            .Select(x =>
            {
                foreach (var model in x.Models)
                {
                    model.ApiSettings = x;
                }

                return x;
            })
            .SelectMany(x => x.Models)
            .GroupBy(x => x.Role)
            .ToDictionary(
                x => x.Key,
                x => x
                    .OrderByDescending(o => o.Priority)
                    .ToArray());
    }

    /// <summary>
    /// Возвращает чат для роли, перебирая модели по приоритету до первой успешно созданной.
    /// </summary>
    public Chat GetChat(CognitiveRoutingType type)
    {
        var model = SelectModel(type);
        return CreateChat(model, Prompt.For(type));
    }

    /// <summary>
    /// Когнитивный роутинг Execution-модели: берём самую дешёвую модель,
    /// чьё CostEfficiency не ниже оценённой сложности. Если такой нет — самую «умную».
    /// </summary>
    public (Chat Chat, ModelSetting Model) GetExecutionChat(int complexityScore)
    {
        if (!_settings.TryGetValue(CognitiveRoutingType.Execution, out var candidates) || candidates.Length == 0)
        {
            throw new NoChatException(CognitiveRoutingType.Execution);
        }

        var ordered = candidates.OrderBy(m => m.CostEfficiency).ToArray();
        var model = ordered.FirstOrDefault(m => m.CostEfficiency >= complexityScore) ?? ordered[^1];

        _logger.Info(
            "Когнитивный роутинг: сложность {Score} → модель {Model} (CostEfficiency {Cost})",
            complexityScore, model.Name, model.CostEfficiency);

        return (CreateChat(model, Prompt.Executing), model);
    }

    private ModelSetting SelectModel(CognitiveRoutingType type)
    {
        if (!_settings.TryGetValue(type, out var models) || models.Length == 0)
        {
            _logger.NoChats(type);
            throw new NoChatException(type);
        }

        return models[0];
    }

    private static Chat CreateChat(ModelSetting model, string prompt)
    {
        var chat = new Chat(model);
        chat.AddPrompt(prompt);
        return chat;
    }
}
