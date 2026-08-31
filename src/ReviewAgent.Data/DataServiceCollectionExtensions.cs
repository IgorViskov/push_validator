using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReviewAgent.Data;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>
    /// Путь к файлу SQLite. По умолчанию — каталог <c>data</c>, который в контейнере
    /// смонтирован томом: именно этим и обеспечивается персистентность реестра.
    /// </summary>
    public string DatabasePath { get; set; } = "data/review-agent.db";

    /// <summary>Сколько прогонов хранить на репозиторий. 0 — не чистить.</summary>
    public int KeepRunsPerRepository { get; set; } = 200;
}

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddAgentStorage(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));

        var options = new StorageOptions();
        config.GetSection(StorageOptions.Section).Bind(options);

        var path = Path.IsPathRooted(options.DatabasePath)
            ? options.DatabasePath
            : Path.Combine(AppContext.BaseDirectory, options.DatabasePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        services.AddDbContext<AgentDbContext>(builder => builder.UseSqlite($"Data Source={path}"));
        services.AddScoped<RepositoryRegistry>();
        services.AddScoped<ReviewJournal>();

        return services;
    }

    /// <summary>
    /// Применяет миграции при старте. Для одноконтейнерного агента это правильная точка:
    /// внешнего процесса развёртывания у него нет, а база — файл на томе рядом с ним.
    /// </summary>
    public static async Task MigrateAgentStorageAsync(this IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Storage");

        await db.Database.MigrateAsync(ct);

        var repositories = await db.Repositories.CountAsync(ct);
        logger.LogInformation("Реестр готов: обслуживаемых репозиториев {Count}", repositories);
    }
}
