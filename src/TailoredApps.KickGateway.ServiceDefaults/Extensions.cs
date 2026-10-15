using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TailoredApps.KickGateway.Telemetry;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    // MassTransit's built-in instrumentation: one meter (messaging.masstransit.*) and one activity source, both named after the library.
    private const string MassTransitInstrumentationName = "MassTransit";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Consume/receive duration, faults (consume.errors), retries and outbox delivery, per endpoint + message type.
                    .AddMeter(MassTransitInstrumentationName)
                    // Ingest latency at the edges (webhooks, Pusher) and end-to-end delivery lag - see KickGatewayMetrics.
                    .AddMeter(KickGatewayMetrics.MeterName)
                    // The Twitch side: EventSub ingest, session health, audio capture - see TwitchGatewayMetrics.
                    .AddMeter(TwitchGatewayMetrics.MeterName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(MassTransitInstrumentationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// OTLP export driven by the standard environment variables, so pointing a deployment at a backend never
    /// needs a rebuild:
    /// <list type="bullet">
    /// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> - one endpoint for metrics, traces and logs (the Aspire dashboard
    /// in dev; an OpenTelemetry Collector or Grafana Alloy in prod).</item>
    /// <item>Otherwise each signal is exported only where its own endpoint is set
    /// (<c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c>, <c>..._TRACES_ENDPOINT</c>, <c>..._LOGS_ENDPOINT</c>), e.g. metrics
    /// straight into Prometheus' OTLP receiver without traces and logs being pushed at it and failing.</item>
    /// </list>
    /// Protocol, headers, timeouts and the metric export interval come from the matching <c>OTEL_*</c> variables
    /// (see docs/MONITORING.md).
    /// </summary>
    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var cfg = builder.Configuration;

        if (!string.IsNullOrWhiteSpace(cfg["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
            return builder;
        }

        // The per-signal AddOtlpExporter() overloads only read the shared OTEL_EXPORTER_OTLP_* variables (the
        // SDK cannot tell which signal it is configuring there), so the signal-specific ones are applied by hand.
        if (TryGetSignalExporter(cfg, "METRICS", out var metricsExporter))
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddOtlpExporter(metricsExporter));

        if (TryGetSignalExporter(cfg, "TRACES", out var tracesExporter))
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddOtlpExporter(tracesExporter));

        if (TryGetSignalExporter(cfg, "LOGS", out var logsExporter))
            builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter(logsExporter));

        return builder;
    }

    /// <summary>
    /// Builds an OTLP exporter configuration from <c>OTEL_EXPORTER_OTLP_{signal}_ENDPOINT</c> (used verbatim - a
    /// full path such as Prometheus' <c>/api/v1/otlp/v1/metrics</c>) plus protocol, headers and timeout from the
    /// signal-specific variable, falling back to the shared one, as the OTel specification describes.
    /// </summary>
    private static bool TryGetSignalExporter(IConfiguration cfg, string signal, out Action<OtlpExporterOptions> configure)
    {
        var endpoint = cfg[$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            configure = _ => { };
            return false;
        }

        var protocol = FirstSet(cfg[$"OTEL_EXPORTER_OTLP_{signal}_PROTOCOL"], cfg["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        var headers = FirstSet(cfg[$"OTEL_EXPORTER_OTLP_{signal}_HEADERS"], cfg["OTEL_EXPORTER_OTLP_HEADERS"]);
        var timeout = FirstSet(cfg[$"OTEL_EXPORTER_OTLP_{signal}_TIMEOUT"], cfg["OTEL_EXPORTER_OTLP_TIMEOUT"]);

        configure = o =>
        {
            o.Endpoint = uri; // setting it in code also stops the SDK from appending /v1/<signal>
            if (protocol is not null)
                o.Protocol = protocol.Trim().Equals("grpc", StringComparison.OrdinalIgnoreCase)
                    ? OtlpExportProtocol.Grpc
                    : OtlpExportProtocol.HttpProtobuf;
            if (headers is not null) o.Headers = headers.Trim();
            if (timeout is not null && int.TryParse(timeout.Trim(), out var ms) && ms > 0) o.TimeoutMilliseconds = ms;
        };
        return true;

        static string? FirstSet(string? specific, string? shared) =>
            !string.IsNullOrWhiteSpace(specific) ? specific : !string.IsNullOrWhiteSpace(shared) ? shared : null;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
