using System.Text;
using Microsoft.Extensions.Options;
using ReviewAgent.CodeGraph;
using ReviewAgent.CodeGraph.Indexing;
using ReviewAgent.Core.Llm;
using ReviewAgent.Core.Models;
using ReviewAgent.Core.Review;
using ReviewAgent.Data;
using ReviewAgent.Web.Services;

namespace ReviewAgent.Web.Api;

/// <summary>
/// HTTP-контракт для хука <c>pre-push</c>. Две ручки и обе намеренно примитивны: их
/// вызывает скрипт на sh, у которого из инструментов только curl.
/// </summary>
public static class ReviewEndpoints
{
    public const string RepoHeader = "X-Review-Repo";
    public const string TokenHeader = "X-Review-Token";
    public const string BranchHeader = "X-Review-Branch";
    public const string DecisionHeader = "X-Review-Decision";

    public static void MapReviewApi(this WebApplication app)
    {
        // ── Живость: по ней хук решает, нужно ли поднимать контейнер ──────────────────
        app.MapGet("/api/health", (
            CognitiveRouter router,
            IOptions<LlmOptions> llm) =>
        {
            var missing = router.MissingRoles;

            return Results.Ok(new
            {
                status = "ok",
                version = typeof(ReviewEndpoints).Assembly.GetName().Version?.ToString(),
                sdk = MSBuildBootstrapper.RegisteredSdk,
                models = llm.Value.AllModels().Count,
                rolesWithoutModel = missing.Select(role => role.ToString()).ToArray()
            });
        });

        // ── Готовность: то же плюс состояние графа. Отдельно от health, потому что
        //    проверка графа стоит сетевого запроса, а хук дёргает health в цикле. ──────
        app.MapGet("/api/ready", async (IGraphStore graph, CancellationToken ct) =>
        {
            var graphAvailable = await graph.IsAvailableAsync(ct);

            return Results.Ok(new
            {
                status = "ok",
                graph = graphAvailable ? "available" : "unavailable",
                sdk = MSBuildBootstrapper.IsReady ? "ready" : "missing"
            });
        });

        // ── Проверка изменений ────────────────────────────────────────────────────────
        // Дифф приходит сырым телом: собрать корректный JSON с произвольным диффом внутри
        // средствами sh нельзя без ручного экранирования, а оно ломается на первой кавычке.
        app.MapPost("/api/review", async (
            HttpRequest http,
            RepositoryRegistry registry,
            ReviewOrchestrator orchestrator,
            RunActivityBus bus,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var key = http.Headers[RepoHeader].ToString();
            var token = http.Headers[TokenHeader].ToString();
            var branch = http.Headers[BranchHeader].ToString();

            if (string.IsNullOrWhiteSpace(key))
                return Results.Text($"Не указан заголовок {RepoHeader}.\nREVIEW_DECISION=allowed\n",
                    "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);

            var repository = await registry.FindByKeyAsync(key, ct);
            if (repository is null)
            {
                logger.LogWarning("Проверка запрошена для неизвестного репозитория {Key}", key);
                return Results.Text(
                    $"Репозиторий '{key}' не обслуживается агентом. Подключите его в админке.\n",
                    "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status404NotFound);
            }

            // Токен сверяем в постоянном времени: строковое сравнение на раннем выходе
            // подсказывает длину совпавшего префикса, а токен здесь — единственная защита.
            if (!FixedTimeEquals(repository.Token, token))
            {
                logger.LogWarning("Проверка {Key} отклонена: неверный токен", key);
                return Results.Text(
                    "Токен репозитория не совпадает. Переустановите хук из админки.\n",
                    "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status401Unauthorized);
            }

            if (!repository.Enabled)
            {
                // Исключённый репозиторий пропускает пуш, а не блокирует его: исключение
                // из обслуживания — это «не проверяй», а не «не пускай».
                return Results.Text(
                    $"Репозиторий {repository.Name} исключён из обслуживания — проверка не выполнялась.\n" +
                    "REVIEW_DECISION=allowed\n",
                    "text/plain; charset=utf-8", Encoding.UTF8);
            }

            using var reader = new StreamReader(http.Body, Encoding.UTF8);
            var diff = await reader.ReadToEndAsync(ct);

            var progress = new CollectingProgress(bus.ProgressFor(repository.Key));

            var review = await orchestrator.RunAsync(
                repository, diff, NullIfEmpty(branch), source: "hook", progress, ct);

            var report = ReviewReportWriter.Build(review.Result, repository.Name, NullIfEmpty(branch));

            http.HttpContext.Response.Headers[DecisionHeader] =
                review.Result.Decision == ReviewDecision.Allowed ? "allowed" : "blocked";

            return Results.Text(report, "text/plain; charset=utf-8", Encoding.UTF8);
        });
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual ?? string.Empty);

        return a.Length == b.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
