namespace LLMAgent.Models;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>
/// Единичная находка любого этапа анализа (оркестрация/исполнение/валидация).
/// Критические находки приостанавливают пуш до решения пользователя.
/// </summary>
public sealed record Finding(Severity Severity, string Stage, string Message, string? File = null);
