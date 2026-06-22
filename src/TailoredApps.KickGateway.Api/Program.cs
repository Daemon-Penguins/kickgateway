using MassTransit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Channels;
using TailoredApps.KickGateway.Api.Components;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Endpoints;
using TailoredApps.KickGateway.Api.LiveFeed;
using TailoredApps.KickGateway.Api.Services;
using TailoredApps.KickGateway.Api.Webhooks;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Channels;
using TailoredApps.KickGateway.Contracts.Events;

var builder = WebApplication.CreateBuilder(args);

// === Aspire service defaults (OTel + health + service discovery) ===
builder.AddServiceDefaults();

// === Kick integration (shared HttpClient + signature verifier) ===
builder.Services.AddKickIntegration(builder.Configuration);

// === OBS clips player (catalog cache + CDN proxy client) ===
builder.Services.AddObsClips();

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

// === Live-feed tap services ===
// Singleton in-memory ring buffer per client + a broadcaster→client resolver,
// both consumed by LiveFeedConsumer below.
builder.Services.AddSingleton<LiveEventBuffer>();
builder.Services.AddSingleton<BroadcasterClientResolver>();

// === MassTransit + RabbitMQ + EF outbox + live-feed consumer ===
// The Api process is the publisher (outbox→RabbitMQ) AND a fan-out subscriber
// that taps every event into the in-memory live-feed buffer for the admin UI.
// Worker continues to consume the same exchanges into its own kebab-case queue
// — competing-consumers only happens for consumers sharing a queue name, so the
// api's per-process queue is a true fanout and doesn't steal worker messages.
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<KickGatewayDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<LiveFeedConsumer>();
    x.AddConsumer<ChannelStatsConsumer>();
    x.AddConsumer<ChannelVideosConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // Switch every Kick event from MassTransit's default fanout to a topic
        // exchange with the broadcaster slug as the routing key. Subscribers
        // can then bind to "#" (everything) or a specific slug.
        KickEventTopology.ConfigurePublishTopology(cfg);

        // Live-feed queue: dedicated name + auto-delete + non-durable. Each API
        // replica gets its own queue (queue name suffixed with the host so two
        // replicas don't compete). Messages buffered for this queue while the
        // API is down are simply discarded — the live feed is best-effort UX,
        // not a durable record (durable record = ReceivedWebhook table).
        var hostSuffix = Environment.MachineName.ToLowerInvariant();
        cfg.ReceiveEndpoint($"kickgateway-api-livefeed-{hostSuffix}", e =>
        {
            e.AutoDelete = true;
            e.Durable = false;

            // Bind to EVERY channel — admin live feed needs the firehose.
            KickEventTopology.BindKickEvent<ChatMessageSent>(e);
            KickEventTopology.BindKickEvent<ChannelFollowed>(e);
            KickEventTopology.BindKickEvent<ChannelSubscriptionNew>(e);
            KickEventTopology.BindKickEvent<ChannelSubscriptionGifts>(e);
            KickEventTopology.BindKickEvent<ChannelSubscriptionRenewal>(e);
            KickEventTopology.BindKickEvent<LivestreamStatusUpdated>(e);
            KickEventTopology.BindKickEvent<LivestreamMetadataUpdated>(e);
            KickEventTopology.BindKickEvent<ModerationBanned>(e);
            KickEventTopology.BindKickEvent<KicksGifted>(e);
            KickEventTopology.BindKickEvent<ChannelRewardRedemptionUpdated>(e);
            KickEventTopology.BindKickEvent<KickEventUnknown>(e);

            e.ConfigureConsumer<LiveFeedConsumer>(ctx);
        });

        // On-demand channel statistics. A SHARED durable queue (competing consumers
        // across API replicas, so each request is handled once) bound to
        // ChannelStatsRequested for every slug. The consumer fetches via the sidecar
        // and publishes ChannelStats (and responds, if invoked as a request).
        cfg.ReceiveEndpoint("kickgateway-channel-stats", e =>
        {
            KickEventTopology.BindKickEvent<ChannelStatsRequested>(e);
            e.ConfigureConsumer<ChannelStatsConsumer>(ctx);
        });

        // On-demand channel videos (VOD listing). Same shared-durable-queue shape as
        // channel stats: competing consumers across replicas, one fetch per request.
        cfg.ReceiveEndpoint("kickgateway-channel-videos", e =>
        {
            KickEventTopology.BindKickEvent<ChannelVideosRequested>(e);
            e.ConfigureConsumer<ChannelVideosConsumer>(ctx);
        });

        // No ConfigureEndpoints — we've declared the only consumer explicitly,
        // and we don't want a second kebab-case queue (would conflict with the
        // worker's competing-consumer queue of the same name).
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

    // Resolve the bootstrap super-admin username from env/config. The
    // migration seeds a placeholder ("superadmin"); the first deploy overrides
    // it with whoever owns the gateway. On subsequent restarts this is a
    // no-op because the row's username will already differ from the
    // placeholder.
    var configured = app.Configuration["Seed:SuperAdminUsername"]?.Trim();
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var seedRow = await db.AdminUsers.FindAsync(KickGatewayDbContext.SeedSuperAdminId);
        if (seedRow is not null && seedRow.Username == KickGatewayDbContext.SeedSuperAdminPlaceholderUsername)
        {
            seedRow.Username = configured;
            seedRow.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
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

// Public OBS clips player + HLS proxy — anonymous so an OBS browser source can load it.
app.MapObsClipsEndpoints();

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
