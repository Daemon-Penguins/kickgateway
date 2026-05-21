using MassTransit;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Components;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Endpoints;
using TailoredApps.KickGateway.Api.Services;
using TailoredApps.KickGateway.Api.Webhooks;

var builder = WebApplication.CreateBuilder(args);

// === Aspire service defaults (OTel + health + service discovery) ===
builder.AddServiceDefaults();

// === Kick integration (shared HttpClient + signature verifier) ===
builder.Services.AddKickIntegration(builder.Configuration);

// === Database ===
var connStr = builder.Configuration.GetConnectionString("KickGateway")
              ?? throw new InvalidOperationException("Missing connection string 'KickGateway'");

builder.Services.AddDbContext<KickGatewayDbContext>(opts =>
    opts.UseSqlServer(connStr, npg => npg.MigrationsAssembly(typeof(KickGatewayDbContext).Assembly.FullName)));
builder.Services.AddDbContextFactory<KickGatewayDbContext>(opts =>
    opts.UseSqlServer(connStr), lifetime: ServiceLifetime.Scoped);

// === Domain services ===
builder.Services.AddScoped<PkceStateStore>();
builder.Services.AddScoped<BroadcasterTokenService>();
builder.Services.AddScoped<SubscriptionEnrollmentService>();
builder.Services.AddScoped<KickWebhookDispatcher>();
builder.Services.AddHostedService<TokenRefreshBackgroundService>();

// === MassTransit + RabbitMQ + EF outbox ===
// The Gateway.Api process is publish-only. Consumers live in Gateway.Worker.
// MassTransit auto-creates a topic exchange per published contract; subscribers
// bind their queues to those exchanges on their side. We use the EF
// transactional outbox so a publish only goes out after the DB transaction commits.
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<KickGatewayDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        cfg.ConfigureEndpoints(ctx);
    });
});

// === Blazor Server ===
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient();

builder.Services.AddOpenApi();

var app = builder.Build();

// Apply EF migrations on startup. In-cluster you'd usually run this out of band,
// but for a single-instance gateway it's the path of least surprise.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();

// Static assets (Blazor framework JS + any wwwroot content).
// MapStaticAssets is the .NET 10-optimized replacement for UseStaticFiles —
// must run BEFORE UseAntiforgery so _framework/blazor.web.js is reachable
// without anti-forgery checks blocking it.
app.MapStaticAssets();

app.UseAntiforgery();

app.MapClientAppEndpoints();
app.MapBroadcasterEndpoints();
app.MapOAuthEndpoints();
app.MapSubscriptionEndpoints();
app.MapWebhookEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.Run();
