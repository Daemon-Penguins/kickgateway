// Aspire AppHost — boots the whole topology (SQL Server + RabbitMQ + Api + Worker)
// as one F5 in Visual Studio. Connection strings/endpoints are injected into the
// child projects via `.WithReference(...)`, so the Api's `ConnectionStrings:KickGateway`
// and `RabbitMq` config sections come from the AppHost-managed containers — no
// docker compose needed in dev.

var builder = DistributedApplication.CreateBuilder(args);

// SQL Server — name "KickGateway" matches the ConnectionString key the Api reads.
// Production runs against a shared SQL Server reached over a private network.
var sql = builder.AddSqlServer("sqlserver")
    .WithDataVolume("kickgateway-sqlserver-data");

var kickGatewayDb = sql.AddDatabase("KickGateway", databaseName: "kickgateway");

// RabbitMQ — the Aspire integration exposes a connection string under the
// resource name. Our Api/Worker read host/port from a "RabbitMq" config section,
// so we map the rabbit endpoint env-var into those individual keys below.
//
// Pin an explicit username parameter: AddRabbitMQ leaves Resource.UserNameParameter
// *null* when no username is supplied (the broker just falls back to "guest"), which
// would NRE the `RabbitMq__Username` env mappings below. The password stays
// Aspire-generated (PasswordParameter is always non-null).
var rabbitUser = builder.AddParameter("rabbitmq-username", "guest");
var rabbit = builder.AddRabbitMQ("rabbitmq", userName: rabbitUser)
    .WithDataVolume("kickgateway-rabbitmq-data")
    .WithManagementPlugin();

// Browser-TLS fetch proxy used to read Kick clips past Cloudflare. Built from
// docker/clips-fetcher; the Api reaches it over the Aspire-managed network. The
// shared secret is generated per run and injected into both sides so they always
// match (dev only — prod sets CLIPS_FETCHER_SECRET via deploy secrets).
var clipsFetcherSecret = Guid.NewGuid().ToString("N");
var clipsFetcher = builder.AddDockerfile("clips-fetcher", "../../docker/clips-fetcher")
    .WithEnvironment("FETCH_SECRET", clipsFetcherSecret)
    .WithHttpEndpoint(targetPort: 8080, name: "http");

builder.AddProject<Projects.TailoredApps_KickGateway_Api>("kickgateway-api")
    .WithReference(kickGatewayDb)
    .WaitFor(kickGatewayDb)
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WaitFor(clipsFetcher)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter)
    .WithEnvironment("Kick__ClipsFetcherUrl", clipsFetcher.GetEndpoint("http"))
    .WithEnvironment("Kick__ClipsFetcherSecret", clipsFetcherSecret);

builder.AddProject<Projects.TailoredApps_KickGateway_Worker>("kickgateway-worker")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

// Live-video capture: ON by default in dev so you can see LiveVideoSegment flowing without extra
// setup (in dev you typically have only a channel or two). Set REALTIME_VIDEO_ENABLED=false to turn
// it off — it's heavy (continuous HLS pull per live channel). Prod is governed separately
// (deploy.yml defaults REALTIME_VIDEO_ENABLED to false).
var realtimeVideoEnabled = !string.Equals(
    builder.Configuration["REALTIME_VIDEO_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);

// Real-time listener. Needs the DB (roster + realtime inbox), RabbitMQ (publish), and the
// clips-fetcher sidecar (to resolve each slug's channel_id/chatroom_id past Cloudflare).
builder.AddProject<Projects.TailoredApps_KickGateway_Realtime>("kickgateway-realtime")
    .WithReference(kickGatewayDb)
    .WaitFor(kickGatewayDb)
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WaitFor(clipsFetcher)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter)
    .WithEnvironment("Kick__ClipsFetcherUrl", clipsFetcher.GetEndpoint("http"))
    .WithEnvironment("Kick__ClipsFetcherSecret", clipsFetcherSecret)
    .WithEnvironment("Kick__Realtime__Video__Enabled", realtimeVideoEnabled ? "true" : "false");

// Three independent subscriber apps. Each uses a unique kebab-case queue
// prefix ("loyalty", "alerts", "analytics") so RabbitMQ creates three separate
// durable queues bound to the same per-message-type fanout exchanges the
// gateway publishes to. Result: every running subscriber gets every message;
// while a subscriber is offline, its queue retains messages on disk.
builder.AddProject<Projects.TailoredApps_KickGateway_Subscribers_Loyalty>("subscriber-loyalty")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

builder.AddProject<Projects.TailoredApps_KickGateway_Subscribers_Alerts>("subscriber-alerts")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

builder.AddProject<Projects.TailoredApps_KickGateway_Subscribers_Analytics>("subscriber-analytics")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

// Sample/test subscriber for the live-video firehose: consumes LiveVideoSegment and reassembles it
// into playable files on disk (proves the video path end-to-end). Binding the media exchange also
// makes `...Realtime.Media:LiveVideoSegment` appear in RabbitMQ at startup. Optional output dir via
// Recorder__OutputDir; defaults to a temp folder (logged at startup).
builder.AddProject<Projects.TailoredApps_KickGateway_Subscribers_VideoRecorder>("video-recorder")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

// Live-stream transcriber: consumes LiveVideoSegment, pulls the audio track out with ffmpeg and runs
// it through Whisper (GPU via Vulkan/CUDA when present, else CPU), publishing LiveTranscript + writing
// per-channel .txt/.jsonl files. Needs `ffmpeg` on PATH (or Transcriber__FfmpegPath) and downloads the
// GGML model (~550 MB for large-v3-turbo q5_0) on first run. Set TRANSCRIBER_ENABLED=false to skip it.
var transcriberEnabled = !string.Equals(
    builder.Configuration["TRANSCRIBER_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);
if (transcriberEnabled)
{
    builder.AddProject<Projects.TailoredApps_KickGateway_Subscribers_Transcriber>("transcriber")
        .WithReference(rabbit)
        .WaitFor(rabbit)
        .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
        .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
        .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
        .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);
}

builder.Build().Run();
