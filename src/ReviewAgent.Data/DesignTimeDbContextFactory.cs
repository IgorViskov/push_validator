using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReviewAgent.Data;

/// <summary>
/// Контекст для <c>dotnet ef migrations</c>. Существует, чтобы инструментам не приходилось
/// поднимать веб-хост: тот при старте регистрирует MSBuild, применяет миграции и открывает
/// порт — всё это лишнее, когда нужно лишь сгенерировать SQL по модели.
///
/// Путь к базе здесь заведомо временный: миграции пишутся по модели, а не по содержимому.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite("Data Source=design-time.db");

        return new AgentDbContext(builder.Options);
    }
}
