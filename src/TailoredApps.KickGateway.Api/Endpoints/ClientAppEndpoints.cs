using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class ClientAppEndpoints
{
    public record CreateClientAppRequest(string Name, string ClientId, string ClientSecret, string RedirectUri, string? Scopes, string? WebhookUrl);
    public record UpdateClientAppRequest(string? Name, string? ClientId, string? ClientSecret, string? RedirectUri, string? Scopes, string? WebhookUrl, bool? IsEnabled);

    public static IEndpointRouteBuilder MapClientAppEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/clients", async (KickGatewayDbContext db, CancellationToken ct) =>
        {
            var list = await db.ClientApps.OrderBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(list);
        });

        routes.MapPost("/api/clients", async (CreateClientAppRequest req, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = new KickClientApp
            {
                Name = req.Name,
                ClientId = req.ClientId,
                ClientSecret = req.ClientSecret,
                RedirectUri = req.RedirectUri,
                Scopes = string.IsNullOrWhiteSpace(req.Scopes) ? "events:subscribe channel:read channel:write user:read chat:write streamkey:read moderation:ban" : req.Scopes,
                WebhookUrl = req.WebhookUrl ?? "",
            };
            db.ClientApps.Add(app);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/clients/{app.Id}", app);
        });

        routes.MapPut("/api/clients/{id:guid}", async (Guid id, UpdateClientAppRequest req, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = await db.ClientApps.FindAsync(new object[] { id }, ct);
            if (app is null) return Results.NotFound();
            if (req.Name is not null) app.Name = req.Name;
            if (req.ClientId is not null) app.ClientId = req.ClientId;
            if (req.ClientSecret is not null) app.ClientSecret = req.ClientSecret;
            if (req.RedirectUri is not null) app.RedirectUri = req.RedirectUri;
            if (req.Scopes is not null) app.Scopes = req.Scopes;
            if (req.WebhookUrl is not null) app.WebhookUrl = req.WebhookUrl;
            if (req.IsEnabled.HasValue) app.IsEnabled = req.IsEnabled.Value;
            app.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(app);
        });

        routes.MapDelete("/api/clients/{id:guid}", async (Guid id, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = await db.ClientApps.FindAsync(new object[] { id }, ct);
            if (app is null) return Results.NotFound();
            db.ClientApps.Remove(app);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return routes;
    }
}
