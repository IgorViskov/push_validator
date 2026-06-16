using OpenAI.Chat;

namespace LLMAgent.Modules.Chats;

/// <summary>
/// Инструмент function-calling: описание для модели + исполнитель,
/// принимающий JSON-аргументы и возвращающий текстовый результат.
/// </summary>
public sealed record AgentTool(
    ChatTool Definition,
    Func<string, CancellationToken, Task<string>> Execute);
