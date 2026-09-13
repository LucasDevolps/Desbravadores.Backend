var builder = DistributedApplication.CreateBuilder(args);

// MVP: default dev-only password. Override via `dotnet user-secrets set Parameters:sql-password ...`
// or the Parameters__sql-password environment variable for anything beyond local development.
var sqlPassword = builder.AddParameter("sql-password", "Almirante_Dev_2026!", secret: true);

var sql = builder.AddSqlServer("sql", password: sqlPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("almirante-sqlserver-data");

var almiranteDb = sql.AddDatabase("almirante");

builder.AddProject<Projects.Almirante_Api>("almirante-api")
    .WithReference(almiranteDb)
    .WaitFor(almiranteDb)
    .WithExternalHttpEndpoints();

builder.Build().Run();
