using LLMAgent.Extensions;
using LLMAgent.Models.Enums;

namespace LLMAgent.Modules.ErrorsModule.Exceptions;

public class NoChatException: Exception
{
    public NoChatException(CognitiveRoutingType type) :
        base($"Не найден ни один чат для шага: {type.GetDescription()}")
    {
    }
}