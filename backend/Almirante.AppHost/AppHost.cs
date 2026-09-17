var builder = DistributedApplication.CreateBuilder(args);

// MVP: default dev-only password. Override via `dotnet user-secrets set Parameters:sql-password ...`
// or the Parameters__sql-password environment variable for anything beyond local development.
var sqlPassword = builder.AddParameter("sql-password", "Almirante_Dev_2026!", secret: true);

// Chave de assinatura do JWT: sem valor padrão (a API recusa chaves fracas ou placeholders). Defina
// uma vez por máquina, fora do Git:
//   dotnet user-secrets set "Parameters:jwt-key-v1" "$(openssl rand -base64 32)" --project backend/Almirante.AppHost
var jwtKey = builder.AddParameter("jwt-key-v1", secret: true);

var sql = builder.AddSqlServer("sql", password: sqlPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("almirante-sqlserver-data");

var almiranteDb = sql.AddDatabase("almirante");

// Perfil "https": login, refresh e logout usam cookies __Host- e só são aceitos por HTTPS.
builder.AddProject<Projects.Almirante_Api>("almirante-api", launchProfileName: "https")
    .WithReference(almiranteDb)
    .WaitFor(almiranteDb)
    .WithEnvironment("Jwt__ActiveKeyId", "v1")
    .WithEnvironment("Jwt__Keys__v1", jwtKey)
    .WithExternalHttpEndpoints();

builder.Build().Run();
