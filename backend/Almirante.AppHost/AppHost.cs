var builder = DistributedApplication.CreateBuilder(args);

// Sem valor padrão versionado: defina fora do Git antes de executar, por exemplo
//   dotnet user-secrets set "Parameters:sql-password" "<senha forte>" --project backend/Almirante.AppHost
// ou a variável de ambiente Parameters__sql-password. Se o volume almirante-sqlserver-data já
// existir, use a mesma senha com que ele foi criado (ou recrie o volume).
var sqlPassword = builder.AddParameter("sql-password", secret: true);

var sql = builder.AddSqlServer("sql", password: sqlPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("almirante-sqlserver-data");

var almiranteDb = sql.AddDatabase("almirante");

builder.AddProject<Projects.Almirante_Api>("almirante-api")
    .WithReference(almiranteDb)
    .WaitFor(almiranteDb)
    .WithExternalHttpEndpoints();

builder.Build().Run();
