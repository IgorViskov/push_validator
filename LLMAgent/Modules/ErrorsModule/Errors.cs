using System.Diagnostics.CodeAnalysis;

namespace LLMAgent.Modules.ErrorsModule;

public static class Errors
{
    [DoesNotReturn]
    public static TResult Rise<TResult>(string message)
    {
        throw new Exception(message);
    }

    [DoesNotReturn]
    public static void Throw<TException>(params object?[]? args)
        where TException : Exception
    {
        throw (TException)Activator.CreateInstance(
            typeof(TException),
            args)!;
    }

    [DoesNotReturn]
    public static void Halt()
    {
        Environment.Exit(0);
    }
}