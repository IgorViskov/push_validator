using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ReviewAgent.Data;

/// <summary>
/// Реестр обслуживаемых репозиториев и журнал прогонов.
///
/// Почему отдельная реляционная база, а не узлы в уже поднятом Neo4j — разбор в
/// docs/DECISIONS.md. Коротко: эти данные табличные и запрашиваются как таблица
/// («последние 20 прогонов репозитория X»), а главное — админка обязана работать,
/// когда граф недоступен. Держать список репозиториев в графе значит терять
/// управление агентом ровно в тот момент, когда с графом что-то не так.
/// </summary>
public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<WatchedRepository> Repositories => Set<WatchedRepository>();
    public DbSet<ReviewRunRecord> Runs => Set<ReviewRunRecord>();
    public DbSet<ReviewFindingRecord> Findings => Set<ReviewFindingRecord>();

    /// <summary>
    /// SQLite не умеет сортировать по <see cref="DateTimeOffset"/>: собственного типа даты
    /// у него нет, и запрос «последние прогоны по времени» падает с NotSupportedException
    /// прямо при рендеринге страницы. Штатный конвертер укладывает значение в целое так,
    /// что порядок по UTC сохраняется, — сортировка и индекс работают, смещение не теряется.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<WatchedRepository>(entity =>
        {
            entity.HasIndex(r => r.Key).IsUnique();

            // Один и тот же путь дважды — это две записи, спорящие за один граф
            // и один файл хука. Дешевле запретить, чем разбираться в последствиях.
            entity.HasIndex(r => r.Path).IsUnique();

            entity.HasMany(r => r.Runs)
                .WithOne(run => run.Repository!)
                .HasForeignKey(run => run.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReviewRunRecord>(entity =>
        {
            // Главный запрос журнала — «последние прогоны этого репозитория».
            entity.HasIndex(run => new { run.RepositoryId, run.StartedAt });

            entity.Property(run => run.CostUsd).HasColumnType("decimal(18,6)");

            entity.HasMany(run => run.Findings)
                .WithOne(finding => finding.Run!)
                .HasForeignKey(finding => finding.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
