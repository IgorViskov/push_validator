using System.ComponentModel;

namespace LLMAgent.Models.Enums;

public enum CognitiveRoutingType
{
    [Description("Планирование")]
    Orchestration = 1,
    [Description("Выполнение")]
    Execution = 2,
    [Description("Валидация")]
    Validation = 3
}