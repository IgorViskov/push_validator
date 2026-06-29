using LLMAgent.Models;
using LLMAgent.Modules.Agent;
using LLMAgent.Modules.Agent.Middelwares;
using LLMAgent.Modules.ErrorsModule.Exceptions;
using LLMAgent.Modules.Git;
using LLMAgent.Modules.Logging;
using LLMAgent.Modules.Router;
using LLMAgent.Modules.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Путь к репозиторию берём из аргументов командной строки (по умолчанию — текущий каталог).
var repoPath = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var apis = configuration.GetSection("Apis").Get<ApiSettings[]>() ?? [];
if (apis.Length == 0)
{
    Console.Error.WriteLine("В конфигурации не задано ни одного API (секция 'Apis').");
    return 1;
}

var services = new ServiceCollection();

services.AddLogging(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

foreach (var api in apis)
{
    services.AddSingleton(api);
}

services.AddSingleton<Logger>();
services.AddSingleton<GitService>();
services.AddSingleton<IUserPermission, ConsoleUserPermission>();
services.AddSingleton<RepoToolFactory>();
services.AddSingleton<CognitiveRouter>();

services.AddSingleton<AgentEngine>();
services.AddSingleton<Agent>();
services.AddMiddlewares();

await using var provider = services.BuildServiceProvider();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var agent = provider.GetRequiredService<Agent>();

try
{
    return await agent.Run(repoPath, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Проверка отменена.");
    return 1;
}
catch (ExitException e)
{
    return e.ExitCode;
}
catch (Exception ex)
{
    var logger = provider.GetRequiredService<Logger>();
    logger.Error(ex, "Непредвиденная ошибка при проверке коммита.");
    return 1;
}
