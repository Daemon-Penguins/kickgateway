using MassTransit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Analytics;
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
using TailoredApps.KickGateway.Api.Transcripts;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.Integrations.Twitch;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;
using TailoredApps.TwitchGateway.Contracts.Media;

var builder = WebApplication.CreateBuilder(args);

// === Aspire service defaults (OTel + health + service discovery) ===
builder.AddServiceDefaults();

// === Kick integration (shared HttpClient + signature verifier) ===
builder.Services.AddKickIntegration(builder.Configuration);

// === Twitch integration (bot-account OAuth for the listener, Helix for the admin page) ===
builder.Services.AddTwitchIntegration(builder.Configuration);

// Browser-UA client used by the /admin/realtime "Probe video" diagnostic to test-fetch a
// channel's live HLS playlist — the same path the realtime listener's capture loop uses.
builder.Services.AddHttpClient("VideoProbe", c => c.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"));

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
        // Always require HTTPS in prod. In dev, follow the request so the cookie also
        // sticks over plain-http localhost (used by the dev-only local sign-in).
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.LoginPath = "/api/auth/admin/login";
        o.AccessDeniedPath = "/admin/forbidden";
    })
    // /api/analytics/* also accepts a shared API key (for the MCP server). Only the analytics
    // policy looks at it — every other endpoint still authenticates with the cookie alone.
    .AddAnalyticsApiKey();
builder.Services.AddAuthorization(o => o.AddKickGatewayPolicies().AddAnalyticsPolicy());

// === Domain services ===
builder.Services.AddScoped<PkceStateStore>();
builder.Services.AddScoped<BroadcasterTokenService>();
builder.Services.AddScoped<SubscriptionEnrollmentService>();
builder.Services.AddScoped<KickWebhookDispatcher>();
builder.Services.AddHostedService<TokenRefreshBackgroundService>();

// === Chat analytics (read model projected from the webhook + realtime inboxes) ===
builder.Services.Configure<ChatAnalyticsOptions>(builder.Configuration.GetSection(ChatAnalyticsOptions.SectionName));
builder.Services.AddScoped<ChatAnalyticsService>();
builder.Services.AddHostedService<ChatProjectionService>();

// === Live-feed tap services ===
// Singleton in-memory ring buffer per client + a broadcaster→client resolver,
// both consumed by LiveFeedConsumer below.
builder.Services.AddSingleton<LiveEventBuffer>();
builder.Services.AddSingleton<BroadcasterClientResolver>();
// Public landing page (/): the last 10 chat messages / transcript slices across every platform + per-minute
// counters (in memory, per replica) and the cached row counts of ChatMessages / LiveTranscripts.
builder.Services.AddSingleton<HomeFeed>();
builder.Services.AddSingleton<StoredTotals>();

// === MassTransit + RabbitMQ + EF outbox + live-feed consumer ===
// The Api process is the publisher (outbox→RabbitMQ) AND a fan-out subscriber
// that taps every event into the in-memory live-feed buffer for the admin UI.
// Worker continues to consume the same exchanges into its own kebab-case queue
// — competing-consumers only happens for consumers sharing a queue name, so the
// api's per-process queue is a true fanout and doesn't steal worker messages.
//
// Consumers that write to SQL retry in-process first (1, 3, 5, 7, 9 s): a transient error
// (timeout, deadlock, failover) must not park a message in the _error queue. What still fails
// is a real fault, lands in _error and trips the broker-side alert (docs/MONITORING.md).
static void UseDbRetry(IReceiveEndpointConfigurator e) =>
    e.UseMessageRetry(r => r.Incremental(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

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
    x.AddConsumer<RealtimeLiveFeedConsumer>();
    x.AddConsumer<ChannelStatsConsumer>();
    x.AddConsumer<ChannelVideosConsumer>();
    x.AddConsumer<LiveTranscriptConsumer>();
    x.AddConsumer<LiveTranscriptTranslationConsumer>();
    x.AddConsumer<TwitchLiveTranscriptConsumer>();
    x.AddConsumer<TwitchLiveFeedConsumer>();
    x.AddConsumer<TwitchStreamStatsConsumer>();

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

        // Live-media family (LiveVideoSegment in, LiveTranscript out). The Api only consumes
        // transcripts, but the publish topology must match the transcriber so the exchange
        // is a slug-routed topic and BindLiveTranscript below binds to the right thing.
        KickMediaTopology.ConfigurePublishTopology(cfg);

        // Realtime (Pusher) family: the Api only taps RealtimeChatMessage for the landing page, but the
        // exchange must be declared the way the listener publishes it (slug-routed topic) for the Bind below.
        KickRealtimeTopology.ConfigurePublishTopology(cfg);

        // Twitch families: declared here for the same reason (the Api only consumes them, but the
        // exchanges must be login-routed topics for the Bind helpers below).
        TwitchEventTopology.ConfigurePublishTopology(cfg);
        TwitchMediaTopology.ConfigurePublishTopology(cfg);

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

        // Realtime chat tap for the landing page: same shape as the live-feed queue above (per replica,
        // auto-delete, every channel). Chat from channels the realtime listener follows without a webhook
        // broadcaster only ever reaches the page through here.
        cfg.ReceiveEndpoint($"kickgateway-api-realtime-livefeed-{hostSuffix}", e =>
        {
            e.AutoDelete = true;
            e.Durable = false;
            KickRealtimeTopology.BindKickRealtimeEvent<RealtimeChatMessage>(e);
            e.ConfigureConsumer<RealtimeLiveFeedConsumer>(ctx);
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

        // Live transcripts from Subscribers.Transcriber: a SHARED durable queue (competing
        // consumers across replicas) bound to every channel. Each slice is stored once in
        // LiveTranscripts; redeliveries are absorbed by the DedupeKey.
        cfg.ReceiveEndpoint("kickgateway-live-transcripts", e =>
        {
            UseDbRetry(e);
            KickMediaTopology.BindLiveTranscript(e);
            e.ConfigureConsumer<LiveTranscriptConsumer>(ctx);
        });

        // Translations of those slices (Subscribers.Translator) - stored next to the transcript, joined on read.
        cfg.ReceiveEndpoint("kickgateway-live-transcript-translations", e =>
        {
            UseDbRetry(e);
            KickMediaTopology.BindLiveTranscriptTranslation(e);
            e.ConfigureConsumer<LiveTranscriptTranslationConsumer>(ctx);
        });

        // Twitch transcripts (the transcriber with Transcriber:Source=twitch) - same table, Platform = twitch.
        cfg.ReceiveEndpoint("kickgateway-twitch-live-transcripts", e =>
        {
            UseDbRetry(e);
            TwitchMediaTopology.BindTranscript(e);
            e.ConfigureConsumer<TwitchLiveTranscriptConsumer>(ctx);
        });

        // On-demand Twitch stream stats (Helix with the app token): the Twitch counterpart of the
        // channel-stats queue above - shared, durable, bound to every login, one answer per request.
        cfg.ReceiveEndpoint("kickgateway-twitch-stream-stats", e =>
        {
            TwitchEventTopology.BindTwitchStreamStatsRequests(e);
            e.ConfigureConsumer<TwitchStreamStatsConsumer>(ctx);
        });

        // Twitch live-feed tap (per replica, auto-delete): end-to-end delivery lag for every Twitch contract.
        cfg.ReceiveEndpoint($"kickgateway-api-twitch-livefeed-{hostSuffix}", e =>
        {
            e.AutoDelete = true;
            e.Durable = false;
            TwitchEventTopology.BindTwitchEvent<TwitchChatMessage>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChatNotification>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChatMessageDeleted>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChatCleared>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChatUserMessagesCleared>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChatSettingsUpdated>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchStreamOnline>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchStreamOffline>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchChannelUpdated>(e);
            TwitchEventTopology.BindTwitchEvent<TwitchEventUnknown>(e);
            e.ConfigureConsumer<TwitchLiveFeedConsumer>(ctx);
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

// Behind Traefik (TLS terminated at the proxy) the Api receives plain http, so without this every
// request-derived absolute URL - the OAuth redirect_uri sent to Kick/Twitch, cookie policies, Blazor's
// base URI - is built with the wrong scheme (verified 2026-10-15: prod sent redirect_uri=http://...).
// Trust X-Forwarded-Proto/-For from any peer: the container is reachable only on the docker network.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear(); // defaults = loopback only, which Traefik on the compose network is not
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

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

// Twitch bot-account OAuth callback — anonymous like the Kick broadcaster callback (the sealed state is the proof).
app.MapTwitchAuthCallback();

// Dev-only local sign-in — bypasses Kick OAuth for local work + manual testing.
// Mapped ONLY in Development; never exists in Production.
if (app.Environment.IsDevelopment())
    app.MapDevAuthEndpoints();

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
admin.MapTwitchAuthEndpoints();  // SuperAdmin check inside

// Chat analytics — own policy (admin cookie OR analytics API key), per-channel scoping inside.
app.MapAnalyticsEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .WithStaticAssets();

app.Run();
