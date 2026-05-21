using MassTransit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Auth;
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

// === Data-protection key ring persisted to DB ===
// Without this, every container restart generates a new key, breaking cookies
// (admin sign-in) and antiforgery tokens across redeploys.
builder.Services.AddDbContext<DataProtectionDbContext>(opts =>
    opts.UseSqlServer(connStr, sql => sql.MigrationsAssembly(typeof(DataProtectionDbContext).Assembly.FullName)));
builder.Services.AddDataProtection()
    .SetApplicationName("kickgateway")
    .PersistKeysToDbContext<DataProtectionDbContext>();

// === Cookie auth (admin SSO via Kick OAuth) ===
builder.Services.AddAuthentication(AdminClaims.AuthenticationScheme)
    .AddCookie(AdminClaims.AuthenticationScheme, o =>
    {
        o.Cookie.Name = "kickgw_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.LoginPath = "/api/auth/admin/login";
        o.AccessDeniedPath = "/admin/forbidden";
    });
builder.Services.AddAuthorization(o => o.AddKickGatewayPolicies());

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
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
    BlazorAuthStateProvider>();

builder.Services.AddOpenApi();

var app = builder.Build();

// Apply EF migrations on startup. In-cluster you'd usually run this out of band,
// but for a single-instance gateway it's the path of least surprise.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
    await db.Database.MigrateAsync();
    var dp = scope.ServiceProvider.GetRequiredService<DataProtectionDbContext>();
    await dp.Database.MigrateAsync();
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

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Webhook receiver stays anonymous (auth = Kick signature verification, not cookies).
app.MapWebhookEndpoints();

// Admin SSO endpoints — public by definition (login/callback/logout).
app.MapAdminAuthEndpoints();

// All admin-facing REST endpoints — must be logged in. Per-client checks
// happen inside the handlers. Group all under a single "/" gate so attribute-
// like .RequireAuthorization sticks (extension methods that take
// IEndpointRouteBuilder can't be chained with .RequireAuthorization themselves).
var admin = app.MapGroup("").RequireAuthorization(AdminPolicies.AnyAuthenticatedAdmin);
admin.MapClientAppEndpoints();
admin.MapBroadcasterEndpoints();
admin.MapOAuthEndpoints();
admin.MapSubscriptionEndpoints();
admin.MapAdminUserEndpoints();   // per-route SuperAdminOnly policy inside

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.Run();
