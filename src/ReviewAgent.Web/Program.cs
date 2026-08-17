using ReviewAgent.CodeGraph.Indexing;
using ReviewAgent.Core;
using ReviewAgent.Core.Llm;
using ReviewAgent.Data;
using ReviewAgent.Web.Api;
using ReviewAgent.Web.Components;
using ReviewAgent.Web.Services;

// Регистрация MSBuild — первой строкой. Если Roslyn успеет загрузить свои копии сборок
// MSBuild, установленный SDK он уже не найдёт: решение откроется без ссылок, семантическая
// модель молча перестанет разрешать символы, и половина рёбер графа не появится.
MSBuildBootstrapper.Ensure();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddReviewEngine(builder.Configuration);
builder.Services.AddAgentStorage(builder.Configuration);

builder.Services.AddSingleton<RunActivityBus>();
builder.Services.AddSingleton<ReviewOrchestrator>();
builder.Services.AddSingleton<IndexingQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingQueue>());

var app = builder.Build();

await app.Services.MigrateAgentStorageAsync(CancellationToken.None);
ReportStartupState(app);

// MapStaticAssets, а не UseStaticFiles: начиная с .NET 9 файлы Blazor (в том числе
// _framework/blazor.web.js) раздаются через манифест статических ресурсов. Со старым
// UseStaticFiles скрипт отдаётся 404-м, страница рендерится, но интерактивной не становится —
// кнопки просто ничего не делают, и в консоли браузера видна единственная строка о 404.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapReviewApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Что агент увидел на старте. Без этой сводки «граф пуст» и «модель не отвечает»
/// выясняются на первом же пуше, посреди демонстрации, и причина неочевидна.
/// </summary>
static void ReportStartupState(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    logger.LogInformation("MSBuild: {Sdk}",
        MSBuildBootstrapper.RegisteredSdk ?? "не найден — разбор кода недоступен, граф не будет наполняться");

    var router = app.Services.GetRequiredService<CognitiveRouter>();
    foreach (var (role, models) in router.Configured)
    {
        logger.LogInformation("Роль {Role}: {Models}", role, string.Join(", ", models.Select(m => m.Name)));
    }

    foreach (var role in router.MissingRoles)
    {
        logger.LogWarning("Роль {Role} без модели — соответствующий этап проверки работать не будет", role);
    }
}
