var builder = DistributedApplication.CreateBuilder(args);

// Sem valor padrão versionado: defina fora do Git antes de executar, por exemplo
//   dotnet user-secrets set "Parameters:sql-password" "<senha forte do sa>" --project backend/Almirante.AppHost
//   dotnet user-secrets set "Parameters:sql-admin-password" "<16+ caracteres, ver .env.example>" --project backend/Almirante.AppHost
// ou as variáveis de ambiente Parameters__sql-password / Parameters__sql-admin-password. Se o volume
// almirante-sqlserver-data já existir, use a mesma senha do sa com que ele foi criado (ou recrie o volume).
//
// Separação de identidades (igual ao compose.yaml): o segredo do "sa" pertence só ao recurso do SQL Server e
// ao container de bootstrap. A API NUNCA o recebe — por isso não há sql.AddDatabase(...).WithReference(...),
// que injetaria uma connection string com "sa" nela. Ela recebe a identidade administrativa dedicada e a de
// runtime (usuários contidos criados pelo bootstrap, sem privilégio de servidor), e recusa iniciar com "sa".
var saPassword = builder.AddParameter("sql-password", secret: true);
var adminUser = builder.AddParameter("sql-admin-user", "almirante_admin_bd");
var adminPassword = builder.AddParameter("sql-admin-password", secret: true);
var appUser = builder.AddParameter("sql-app-user", "almirante_user_bd");
const string database = "almirante";

var sql = builder.AddSqlServer("sql", password: saPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("almirante-sqlserver-data");

// Mesmo bootstrap do serviço "sql-bootstrap" do Compose (scripts/sql-bootstrap.sh + docs/sql/criar-usuario-admin-app.sql):
// cria o banco e as duas identidades da API, idempotente, roda uma vez e sai. A senha do sa e a administrativa vão
// por variável de ambiente (lidas pelo sqlcmd), nunca por linha de comando.
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
