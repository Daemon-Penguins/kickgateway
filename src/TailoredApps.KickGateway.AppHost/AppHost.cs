// Aspire AppHost — boots the whole topology (SQL Server + RabbitMQ + Api + Worker)
// as one F5 in Visual Studio. Connection strings/endpoints are injected into the
// child projects via `.WithReference(...)`, so the Api's `ConnectionStrings:KickGateway`
// and `RabbitMq` config sections come from the AppHost-managed containers — no
// docker compose needed in dev.

var builder = DistributedApplication.CreateBuilder(args);

// SQL Server — name "KickGateway" matches the ConnectionString key the Api reads.
// Production runs against the shared hq-config SQL Server (db-internal network).
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

builder.Build().Run();
