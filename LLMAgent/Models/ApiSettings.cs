using LLMAgent.Models.Enums;

namespace LLMAgent.Models;

public sealed class ApiSettings
{
    public required string Name { get; set; }
    public required string ApiKey { get; set; }
    public required string Endpoint { get; set; }
    public required ModelSetting[] Models { get; set; }
}

public sealed class ModelSetting
{
    public required string Name { get; set; }
    public CognitiveRoutingType Role { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// Положение модели на шкале «цена ↔ ум» (≈1..10).
    /// Чем выше — тем модель умнее и дороже за токен.
    /// Когнитивный роутер выбирает для Execution самую дешёвую модель,
    /// чьё значение не ниже оценённой сложности изменений.
    /// </summary>
    public double CostEfficiency { get; set; } = 1;

    public ApiSettings ApiSettings { get; set; } = null!;
}