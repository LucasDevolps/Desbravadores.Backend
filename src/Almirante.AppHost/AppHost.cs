var builder = DistributedApplication.CreateBuilder(args);

var saPassword = builder.AddParameter("sql-password", secret: true);
var adminUser = builder.AddParameter("sql-admin-user", "almirante_admin_bd");
var adminPassword = builder.AddParameter("sql-admin-password", secret: true);
var appUser = builder.AddParameter("sql-app-user", "almirante_user_bd");
const string database = "almirante";

var sql = builder.AddSqlServer("sql", password: saPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("almirante-sqlserver-data");

var repositoryRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var bootstrap = builder.AddContainer("sql-bootstrap", "mcr.microsoft.com/mssql/server", "2022-latest")
    .WithEntrypoint("/bin/bash")
    .WithArgs("/bootstrap/sql-bootstrap.sh")
    .WithBindMount(Path.Combine(repositoryRoot, "scripts", "sql-bootstrap.sh"), "/bootstrap/sql-bootstrap.sh", isReadOnly: true)
    .WithBindMount(Path.Combine(repositoryRoot, "docs", "sql", "criar-usuario-admin-app.sql"), "/bootstrap/criar-usuario-admin-app.sql", isReadOnly: true)
    .WithEnvironment("SQLCMDPASSWORD", saPassword)
    .WithEnvironment("SQL_BOOTSTRAP_HOST", sql.Resource.Name)
    .WithEnvironment("DB_NAME", database)
    .WithEnvironment("ADMIN_USER", adminUser)
    .WithEnvironment("APP_USER", appUser)
    .WithEnvironment("APP_ADMIN_PASSWORD", adminPassword)
    .WaitFor(sql);

var endpoint = sql.Resource.PrimaryEndpoint;
var server = ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)},{endpoint.Property(EndpointProperty.Port)}");

builder.AddProject<Projects.Almirante_Api>("almirante-api")
    .WithEnvironment("ConnectionStrings__almirante", ReferenceExpression.Create($"Server={server};Database={database};Encrypt=True;TrustServerCertificate=True"))
    .WithEnvironment("ConnectionStrings__AlmiranteAdmin", ReferenceExpression.Create($"Server={server};Database={database};User Id={adminUser};Password={adminPassword};Encrypt=True;TrustServerCertificate=True"))
    .WithEnvironment("DbCredentials__AppUser", appUser)
    .WaitForCompletion(bootstrap)
    .WithExternalHttpEndpoints();

builder.Build().Run();
