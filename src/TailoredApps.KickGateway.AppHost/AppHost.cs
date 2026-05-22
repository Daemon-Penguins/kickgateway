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
var rabbit = builder.AddRabbitMQ("rabbitmq")
    .WithDataVolume("kickgateway-rabbitmq-data")
    .WithManagementPlugin();

builder.AddProject<Projects.TailoredApps_KickGateway_Api>("kickgateway-api")
    .WithReference(kickGatewayDb)
    .WaitFor(kickGatewayDb)
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

builder.AddProject<Projects.TailoredApps_KickGateway_Worker>("kickgateway-worker")
    .WithReference(rabbit)
    .WaitFor(rabbit)
    .WithEnvironment("RabbitMq__Host", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("RabbitMq__Port", rabbit.Resource.PrimaryEndpoint.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("RabbitMq__Username", rabbit.Resource.UserNameParameter!)
    .WithEnvironment("RabbitMq__Password", rabbit.Resource.PasswordParameter);

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

builder.Build().Run();
