using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Application;
using MyRegistry.Web.Services;

namespace MyRegistry.Web.Api;

public static class McpEndpoints
{
    public static void MapMcpApi(this WebApplication app)
    {
        var mcp = app.MapGroup("/mcp").WithTags("MCP");

        // Transport « Streamable HTTP » sans session ni flux serveur : chaque POST reçoit sa réponse JSON.
        mcp.MapPost("/", async (HttpContext ctx, McpServer server, MyRegistry.Infrastructure.SettingsService settings) =>
        {
            if (!McpRules.IsOriginAllowed(ctx.Request.Headers.Origin.FirstOrDefault(), ctx.Request.Host.Value))
                return Results.Problem(statusCode: 403, title: "Origine refusée");

            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Rpc(400, McpRules.ParseError, "JSON invalide."); }
            if (body is null) return Rpc(400, McpRules.InvalidRequest, "Corps vide.");

            var configured = await settings.GetAsync(MyRegistry.Application.SettingKeys.PublicUrl, "");
            var baseUrl = configured.Length > 0 ? configured : $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            if (body is JsonArray batch)
            {
                if (batch.Count is 0 or > McpRules.MaxBatch) return Rpc(400, McpRules.InvalidRequest, $"Lot de 1 à {McpRules.MaxBatch} requêtes.");
                var replies = new JsonArray();
                foreach (var item in batch) if (await server.HandleAsync(item, baseUrl, ctx.RequestAborted) is { } r) replies.Add(r);
                return replies.Count == 0 ? Results.StatusCode(202) : Results.Content(replies.ToJsonString(), "application/json");
            }
            var reply = await server.HandleAsync(body, baseUrl, ctx.RequestAborted);
            return reply is null ? Results.StatusCode(202) : Results.Content(reply.ToJsonString(), "application/json");
        }).WithName("McpPost");

        // Pas de flux serveur ni de session : on l'annonce explicitement (405) comme le prévoit la spécification.
        mcp.MapGet("/", () => Results.StatusCode(405)).WithName("McpGet");
        mcp.MapDelete("/", () => Results.StatusCode(405)).WithName("McpDelete");
    }

    private static IResult Rpc(int status, int code, string message) =>
        Results.Content(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), "application/json", statusCode: status);
}
