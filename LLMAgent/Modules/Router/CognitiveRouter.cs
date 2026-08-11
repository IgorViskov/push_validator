using LLMAgent.Models;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.Chats;
using LLMAgent.Modules.ErrorsModule;
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

        var configured = apiSettings
            .Select(x =>
            {
                foreach (var model in x.Models)
                {
                    model.ApiSettings = x;
                }

                return x;
            })
            .SelectMany(x => x.Models)
            .ToArray();

        // Модель без имени — обычно недозаполненная запись в конфиге или переменной
        // окружения. Роутер её выберет наравне с остальными, а падение случится позже
        // и в другом месте: клиент OpenAI не создаётся с пустым идентификатором модели.
        var usable = configured.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToArray();

        foreach (var broken in configured.Except(usable))
        {
            _logger.Warn("В конфигурации модель без имени (API {Api}, роль {Role}) — пропущена.",
                broken.ApiSettings?.Name ?? "—", broken.Role);
        }

        _settings = usable
            .GroupBy(x => x.Role)
            .ToDictionary(
                x => x.Key,
                x => x
                    .OrderByDescending(o => o.Priority)
                    .ToArray());
    }

    /// <summary>
    /// Возвращает чат для роли и саму модель: имя и цена нужны шлюзу обращений,
    /// который считает расход и ведёт предохранитель по каждой модели отдельно.
    /// </summary>
    public (Chat Chat, ModelSetting Model) GetChat(CognitiveRoutingType type) => GetChat(type, Prompt.For(type));

    /// <summary>
    /// Чат для роли с нестандартным системным промптом. Нужен арбитру: по характеру задачи
    /// он ближе всего к Orchestration, но отдельной роли в конфиге под него не заводится —
    /// иначе каждый существующий appsettings.json пришлось бы дополнять новой моделью.
    /// </summary>
    public (Chat Chat, ModelSetting Model) GetChat(CognitiveRoutingType type, string systemPrompt)
    {
        var model = SelectModel(type);
        return (CreateChat(model, systemPrompt), model);
    }

    /// <summary>
    /// Когнитивный роутинг Execution-модели: берём самую дешёвую модель,
    /// чьё CostEfficiency не ниже оценённой сложности. Если такой нет — самую «умную».
    /// </summary>
    public (Chat Chat, ModelSetting Model) GetExecutionChat(int complexityScore)
    {
        if (!_settings.TryGetValue(CognitiveRoutingType.Execution, out var candidates) || candidates.Length == 0)
        {
            Errors.Throw<NoChatException>(CognitiveRoutingType.Execution);
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
            Errors.Throw<NoChatException>(type);
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
