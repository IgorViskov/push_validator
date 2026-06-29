namespace LLMAgent.Modules.ErrorsModule.Exceptions;

public class ExitException : Exception
{
    public int ExitCode { get; set; }
    
    public ExitException(int exitCode)
    {
        ExitCode = exitCode;
    }
}