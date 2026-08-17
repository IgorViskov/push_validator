using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Prompts;

namespace ReviewAgent.Core.Llm;

/// <summary>Ни одной модели под требуемую роль в конфигурации нет.</summary>
public sealed class NoModelForRoleException(CognitiveRole role)
    : Exception($"В конфигурации нет ни одной модели для роли {role}. " +
                "Проверьте секцию Llm:Providers — у каждой роли должна быть хотя бы одна модель.")
{
    public CognitiveRole Role { get; } = role;
}

/// <summary>
/// Выбор модели под шаг сценария. Две разные политики:
/// по роли (Orchestration / Validation) берётся модель с наибольшим приоритетом,
/// а для анализа — самая дешёвая, чей «ум» не ниже оценённой сложности изменений.
/// </summary>
public sealed class CognitiveRouter
{
    private readonly Dictionary<CognitiveRole, ModelOptions[]> _byRole;
    private readonly int _maxReActIterations;
    private readonly ILogger<CognitiveRouter> _logger;

    public CognitiveRouter(
        IOptions<LlmOptions> options,
        IOptions<GuardrailOptions> guardrails,
        ILogger<CognitiveRouter> logger)
    {
        _logger = logger;
        _maxReActIterations = guardrails.Value.Tools.MaxReActIterations;

        var configured = options.Value.AllModels();

        // Модель без имени — обычно недозаполненная запись в конфиге или переменной
        // окружения. Роутер выбрал бы её наравне с остальными, а падение случилось бы
        // позже и в другом месте: клиент OpenAI не создаётся с пустым идентификатором.
        var usable = configured.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToArray();

        foreach (var broken in configured.Except(usable))
        {
            _logger.LogWarning("В конфигурации модель без имени (провайдер {Provider}, роль {Role}) — пропущена",
                broken.Provider.Name, broken.Role);
        }

        _byRole = usable
            .GroupBy(m => m.Role)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Priority).ToArray());
    }

    /// <summary>Какие роли реально обеспечены моделями — админка показывает это до первого прогона.</summary>
    public IReadOnlyDictionary<CognitiveRole, ModelOptions[]> Configured => _byRole;

    /// <summary>Роли, под которые модели не заданы: без них соответствующий этап не отработает.</summary>
    public IReadOnlyList<CognitiveRole> MissingRoles => Enum.GetValues<CognitiveRole>()
        .Where(role => !_byRole.TryGetValue(role, out var models) || models.Length == 0)
        .ToArray();

    /// <summary>Чат для роли со штатным системным промптом.</summary>
    public Chat GetChat(CognitiveRole role) => GetChat(role, Prompt.For(role));

    /// <summary>
    /// Чат для роли с нестандартным системным промптом. Нужен арбитру: по характеру задачи
    /// он ближе всего к Orchestration, но отдельной роли в конфиге под него не заводится —
    /// иначе каждую существующую конфигурацию пришлось бы дополнять новой моделью.
    /// </summary>
    public Chat GetChat(CognitiveRole role, string systemPrompt)
    {
        var model = SelectByPriority(role);
        return Create(model, systemPrompt);
    }

    /// <summary>
    /// Когнитивный роутинг Execution-модели: берём самую дешёвую модель, чей CostEfficiency
    /// не ниже оценённой сложности. Если такой нет — самую «умную» из доступных.
    /// </summary>
    public Chat GetExecutionChat(int complexityScore)
    {
        if (!_byRole.TryGetValue(CognitiveRole.Execution, out var candidates) || candidates.Length == 0)
            throw new NoModelForRoleException(CognitiveRole.Execution);

        var ordered = candidates.OrderBy(m => m.CostEfficiency).ToArray();
        var model = ordered.FirstOrDefault(m => m.CostEfficiency >= complexityScore) ?? ordered[^1];

        _logger.LogInformation(
            "Когнитивный роутинг: сложность {Score} → модель {Model} (CostEfficiency {Cost})",
            complexityScore, model.Name, model.CostEfficiency);

        return Create(model, Prompt.For(CognitiveRole.Execution));
    }

    private ModelOptions SelectByPriority(CognitiveRole role)
    {
        if (!_byRole.TryGetValue(role, out var models) || models.Length == 0)
            throw new NoModelForRoleException(role);

        return models[0];
    }

    private Chat Create(ModelOptions model, string prompt)
    {
        var chat = new Chat(model, _maxReActIterations);
        chat.AddPrompt(prompt);
        return chat;
    }
}
