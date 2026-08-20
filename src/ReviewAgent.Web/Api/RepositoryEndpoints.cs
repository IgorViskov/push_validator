using ReviewAgent.CodeGraph;
using ReviewAgent.Data;
using ReviewAgent.Web.Services;

namespace ReviewAgent.Web.Api;

/// <param name="Path">Путь к рабочему дереву, каким его видит агент.</param>
public sealed record ConnectRepositoryRequest(
    string Path, string? Name = null, string? SolutionPath = null, bool OverwriteForeignHook = false);

/// <summary>
/// Управление реестром репозиториев по HTTP — то же, что делает админка кнопками.
///
/// Нужно для автоматизации: сценарий показа и развёртывание на новой машине не должны
/// требовать человека у браузера. Прав эти ручки не добавляют — админка и без них
/// позволяет ровно то же самое и тоже без авторизации: это инструмент разработчика,
/// живущий на его же машине и опубликованный на localhost. Если агента когда-нибудь
/// выставят наружу, авторизацию нужно будет добавить и UI, и API одновременно.
/// </summary>
public static class RepositoryEndpoints
{
    public static void MapRepositoryApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/repositories");

        // Шаблоны корневых ручек пустые, а не "/": с MapGroup второе даёт маршрут
        // /api/repositories/ со слешем на конце, и обращение без слеша отвечает 404.

        // Шаблоны у корневых ручек пустые, а не "/": с MapGroup второе даёт маршрут
        // /api/repositories/ со слешем на конце, и обращение без слеша отвечает 404.

        group.MapGet("", async (
            RepositoryRegistry registry, IndexingQueue indexing, CancellationToken ct) =>
        {
            var repositories = await registry.ListAsync(ct);

            return Results.Ok(repositories.Select(r => new
            {
                r.Key,
                r.Name,
                r.Path,
                r.Enabled,
                r.HookInstalled,
                r.GraphNodes,
                r.GraphEdges,
                r.LastIndexedAt,
                r.LastIndexError,
                indexing = indexing.StatusOf(r.Key) is { } s
                    ? new { s.State, s.Message, s.At }
                    : null
            }));
        });

        group.MapPost("", async (
            ConnectRepositoryRequest request,
            RepositoryProvisioning provisioning,
            CancellationToken ct) =>
        {
            var result = await provisioning.ConnectAsync(
                request.Path, request.Name, request.SolutionPath, request.OverwriteForeignHook, ct);

            if (result.Repository is null)
                return Results.BadRequest(new { ok = false, message = result.Message });

            return Results.Ok(new
            {
                ok = result.Ok,
                message = result.Message,
                key = result.Repository.Key,
                name = result.Repository.Name,
                path = result.Repository.Path
            });
        });

        // Состояние индексации: по нему сценарий показа понимает, что граф готов,
        // и не начинает пуш раньше времени.
        group.MapGet("/{key}/status", async (
            string key,
            RepositoryRegistry registry,
            IndexingQueue indexing,
            IGraphStore graph,
            CancellationToken ct) =>
        {
            var repository = await registry.FindByKeyAsync(key, ct);
            if (repository is null) return Results.NotFound(new { error = $"репозиторий '{key}' не обслуживается" });

            var status = indexing.StatusOf(key);

            long nodes = repository.GraphNodes, edges = repository.GraphEdges;
            try
            {
                var stats = await graph.GetStatsAsync(key, ct);
                nodes = stats.Nodes;
                edges = stats.Edges;
            }
            catch (Exception) { /* граф недоступен — отдаём последнее известное из реестра */ }

            return Results.Ok(new
            {
                repository.Key,
                repository.Name,
                repository.Enabled,
                repository.HookInstalled,
                indexing = status?.State ?? (repository.LastIndexedAt is null ? "never" : "done"),
                message = status?.Message,
                nodes,
                edges,
                repository.LastIndexedAt,
                repository.LastIndexError
            });
        });

        group.MapDelete("/{key}", async (
            string key,
            RepositoryRegistry registry,
            RepositoryProvisioning provisioning,
            CancellationToken ct) =>
        {
            var repository = await registry.FindByKeyAsync(key, ct);
            if (repository is null) return Results.NotFound(new { error = $"репозиторий '{key}' не обслуживается" });

            var result = await provisioning.DisconnectAsync(repository, ct);
            return Results.Ok(new { ok = result.Ok, message = result.Message });
        });
    }
}
