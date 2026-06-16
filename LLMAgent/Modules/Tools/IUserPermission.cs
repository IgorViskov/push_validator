namespace LLMAgent.Modules.Tools;

/// <summary>
/// Запрос разрешения у пользователя на действие за пределами репозитория.
/// </summary>
public interface IUserPermission
{
    bool Ask(string what);
}

public sealed class ConsoleUserPermission : IUserPermission
{
    private readonly object _lock = new();

    public bool Ask(string what)
    {
        lock (_lock)
        {
            Console.WriteLine();
            Console.Write($"⚠️  Агент просит разрешение: {what} (y/N): ");
            var answer = Console.ReadLine()?.Trim();
            return answer is not null &&
                   (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                    answer.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    answer.Equals("да", StringComparison.OrdinalIgnoreCase));
        }
    }
}
