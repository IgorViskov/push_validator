using System.Diagnostics.CodeAnalysis;
using LLMAgent.Extensions;
using LLMAgent.Models.Enums;
using LLMAgent.Modules.ErrorsModule;
using Microsoft.Extensions.Logging;

namespace LLMAgent.Modules.Logging;

public class Logger
{
    private readonly ILogger<Logger> _logger;

    public Logger(ILogger<Logger> logger)
    {
        _logger = logger;
    }

    
    public void NoChats(CognitiveRoutingType type)
    {
        _logger.LogError((string)"Не найден ни один чат для шага: {Description}", type.GetDescription());
    }
    
    public void Error(Exception e, string message)
    {
        _logger.LogError(e, message);
    }

    public void Info(string message, params object?[] args)
    {
        _logger.LogInformation(message, args);
    }

    public void Warn(string message, params object?[] args)
    {
        _logger.LogWarning(message, args);
    }
}