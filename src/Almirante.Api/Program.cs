using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Almirante.Api.Cli;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Almirante.Api.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.PostConfigure<JwtOptions>(options =>
{
    foreach (var kid in options.Keys.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList())
    {
        options.Keys.Remove(kid);
    }
});
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<JwtOptions>, JwtOptionsValidator>();
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection(ReverseProxyOptions.SectionName));

builder.Services.AddOptions<ConnectionStringsOptions>().Bind(builder.Configuration.GetSection(ConnectionStringsOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ConnectionStringsOptions>, SqlServerConnectionSecurityValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ConnectionStringsOptions>, SqlIdentityPolicyValidator>();

builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<Microsoft.Extensions.Options.IOptions<ReverseProxyOptions>>((options, reverseProxyOptions) =>
    {
        var trustedNetworkCidr = reverseProxyOptions.Value.TrustedNetworkCidr;
        if (string.IsNullOrWhiteSpace(trustedNetworkCidr))
        {
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        var network = System.Net.IPNetwork.Parse(trustedNetworkCidr);

        var minimoPrefixo = network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 64 : 16;
        if (network.PrefixLength < minimoPrefixo)
        {
            throw new InvalidOperationException(
                $"{ReverseProxyOptions.SectionName}:TrustedNetworkCidr ('{trustedNetworkCidr}') é amplo demais: " +
                $"com prefixo /{network.PrefixLength} a API passaria a confiar em X-Forwarded-For/X-Forwarded-Proto " +
                $"de praticamente qualquer origem, permitindo forjar IP e esquema. Use a sub-rede do reverse proxy " +
                $"(/{minimoPrefixo} ou mais específica; ex.: a subnet do Compose, ou 127.0.0.1/32 atrás do IIS).");
        }

        options.KnownIPNetworks.Add(network);
    });

var dbCredentialOptions = builder.Configuration.GetSection(DbCredentialOptions.SectionName).Get<DbCredentialOptions>() ?? new DbCredentialOptions();
builder.Services.AddOptions<DbCredentialOptions>().Bind(builder.Configuration.GetSection(DbCredentialOptions.SectionName))
    .Validate(o => o.RotationHours is >= 1 and <= 720, "DbCredentials:RotationHours deve estar entre 1 e 720.").ValidateOnStart();
if (dbCredentialOptions.Enabled)
{
    var appCredentialProvider = new AppDbCredentialProvider();
    builder.Services.AddSingleton(appCredentialProvider);
    builder.Services.AddSingleton<DbCredentialManager>();
    builder.Services.AddHostedService<DbCredentialRotationService>();
    builder.Services.AddHealthChecks().AddCheck<DbConnectivityHealthCheck>("almirante-db");
    builder.AddSqlServerDbContext<AlmiranteDbContext>("almirante",
        configureSettings: settings => settings.DisableHealthChecks = true,
        configureDbContextOptions: options => options.AddInterceptors(new AppDbCredentialInterceptor(appCredentialProvider)));
}
else
{
    builder.AddSqlServerDbContext<AlmiranteDbContext>("almirante");
}

builder.Services.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<LoginLockout>();
builder.Services.AddScoped<UsuariosService>();
builder.Services.AddScoped<LancamentosService>();
builder.Services.AddScoped<LancamentoGeralService>();
builder.Services.AddScoped<LancamentoExclusaoService>();
builder.Services.AddSingleton(new EventosLockOptions());
builder.Services.AddScoped<EventosService>();
builder.Services.AddScoped<CargosService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddLoginRateLimiting(builder.Configuration);

builder.Services.AddOptions<ClubeOptions>().Bind(builder.Configuration.GetSection(ClubeOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ClubeOptions>, ClubeOptionsValidator>();
builder.Services.AddSingleton<DataCivil>();

builder.Services.AddOptions<ViaCepOptions>().Bind(builder.Configuration.GetSection(ViaCepOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ViaCepOptions>, ViaCepOptionsValidator>();
// O pipeline padrão de resiliência do AddServiceDefaults (30 s, três novas tentativas) usa as mesmas opções para todo HttpClient.
// O ViaCEP troca esse pipeline pelo próprio: prazo total ViaCep:TimeoutMs e uma única nova tentativa rápida. RemoveAllResilienceHandlers
// é a forma documentada de fazer isso para um cliente só, mas ainda é marcada como experimental (EXTEXP0001).
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient<IConsultaCep, ViaCepClient>(ViaCepClient.NomeHttpClient, (sp, http) =>
    {
        var viaCep = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ViaCepOptions>>().Value;
        http.BaseAddress = new Uri(viaCep.BaseUrl);
        http.Timeout = viaCep.Timeout + TimeSpan.FromSeconds(1);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Almirante.Api");
    })
    // O log padrão do HttpClientFactory grava a URL, que tem o CEP do endereço do usuário; o ViaCepClient registra as falhas sem ele.
    .RemoveAllLoggers()
    .RemoveAllResilienceHandlers()
#pragma warning restore EXTEXP0001
    .AddStandardResilienceHandler()
    .Configure((resiliencia, sp) =>
    {
        var prazo = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ViaCepOptions>>().Value.Timeout;
        resiliencia.TotalRequestTimeout.Timeout = prazo;
        resiliencia.AttemptTimeout.Timeout = prazo / 2;
        resiliencia.Retry.MaxRetryAttempts = 1;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(100);
        resiliencia.Retry.BackoffType = Polly.DelayBackoffType.Constant;
        resiliencia.Retry.UseJitter = false;
        resiliencia.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, prazo.TotalSeconds));
    });

builder.Services.AddOptions<MediaOptions>().Bind(builder.Configuration.GetSection(MediaOptions.SectionName))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.Storage.Path) && builder.Environment.IsDevelopment())
        {
            options.Storage.Path = MediaOptions.CaminhoPadraoDesenvolvimento;
        }
    });
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<MediaOptions>, MediaOptionsValidator>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddSingleton<ProcessadorFotos>();
builder.Services.AddSingleton<LimitadorEnvioFotos>();
builder.Services.AddScoped<UsuarioFotoService>();
builder.Services.AddHostedService<FotosCleanupService>();

builder.Services.AddHsts(options =>
{
    var hsts = builder.Configuration.GetSection("Hsts");
    options.MaxAge = TimeSpan.FromDays(hsts.GetValue("MaxAgeDays", 365));
    options.IncludeSubDomains = hsts.GetValue("IncludeSubDomains", false);
    options.Preload = hsts.GetValue("Preload", false);
});
builder.Services.AddHostedService<AuthSessionCleanupService>();

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddControllersWithViews().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.Converters.Add(new Almirante.Api.Entities.TipoFluxoLancamentoJsonConverter(
        builder.Configuration.GetValue<bool>("Compatibility:LegacyTipoFluxoDespesa")));
})
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ModelBindingErrors.Responder);

builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<ApiProblemExceptionHandler>();
builder.Services.AddProblemDetails();
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection().SetApplicationName($"Almirante:{builder.Environment.EnvironmentName}")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "__Host-almirante-csrf"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.Path = "/"; });

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearerOptions.MapInboundClaims = false;
        bearerOptions.SaveToken = false;
        bearerOptions.TokenHandlers.Clear();
        bearerOptions.TokenHandlers.Add(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 8192 });
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, kid, _) => string.IsNullOrWhiteSpace(kid) ? [] : [JwtKeySet.GetKey(jwt, kid)],
            ValidateLifetime = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidTypes = ["at+jwt"],
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        bearerOptions.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                var required = new[] { "sub", "role", "iat", "nbf", "exp", "jti", "sid" };
                if (required.Any(type => principal.FindAll(type).Count() != 1) ||
                    !Guid.TryParse(principal.FindFirstValue("sub"), out var uid) ||
                    !Guid.TryParse(principal.FindFirstValue("sid"), out var sid) ||
                    !long.TryParse(principal.FindFirstValue("iat"), out var iat) ||
                    !long.TryParse(principal.FindFirstValue("nbf"), out var nbf) ||
                    !long.TryParse(principal.FindFirstValue("exp"), out var exp)) { context.Fail("Perfil de token inválido."); return; }
                var nowOffset = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                if (iat > nowOffset.ToUnixTimeSeconds() + 30 || nbf > exp) { context.Fail("Datas do token são incoerentes."); return; }
                var identity = (ClaimsIdentity)principal.Identity!;
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
                var db = context.HttpContext.RequestServices.GetRequiredService<AlmiranteDbContext>();
                var now = nowOffset.UtcDateTime;
                var valid = await db.AuthSessions.AsNoTracking().AnyAsync(x => x.Id == sid && x.UsuarioId == uid && x.RevokedAtUtc == null &&
                    x.AbsoluteExpiresAtUtc > now && x.Usuario != null && x.Usuario.SecurityVersion == x.SecurityVersion && x.Usuario.Cargo != null &&
                    x.Usuario.Cargo.Ativo && x.Usuario.Cargo.Role == principal.FindFirstValue("role"), context.HttpContext.RequestAborted);
                if (!valid) context.Fail("Sessão inválida.");
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.GestaoFinanceira, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Diretoria));
    options.AddPolicy(Policies.GestaoCadastros, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Diretoria));
});

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AcessoNegadoAuthorizationMiddlewareResultHandler>();

const string LocalCorsPolicy = "LocalFrontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200", "http://localhost:4201"];

builder.Services.AddCors(options =>
{
    options.AddPolicy(LocalCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod().AllowCredentials();
    });
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Almirante API",
        Version = "v1",
    });

    options.SchemaFilter<MembrosSchemaFilter>();
    options.OperationFilter<EventosOperationFilter>();
    options.OperationFilter<UsuariosFotoOperationFilter>();

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Informe: Bearer {seu token}",
    });

    options.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", null, null),
            new List<string>()
        },
    });
});

var app = builder.Build();

var isAdminCli = args.Length > 0 && string.Equals(args[0], AdminPasswordResetCli.CommandName, StringComparison.OrdinalIgnoreCase);
try
{
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
}
catch (Microsoft.Extensions.Options.OptionsValidationException ex) when (isAdminCli)
{
    Console.Error.WriteLine("Configuração recusada pela validação de segurança: " + string.Join(" ", ex.Failures));
    return 1;
}

if (isAdminCli)
{
    var cliConnection = dbCredentialOptions.Enabled ? app.Configuration.GetConnectionString(DbCredentialManager.AdminConnectionName) : null;
    if (cliConnection is not null)
    {
        await using var auditConnection = new Microsoft.Data.SqlClient.SqlConnection(cliConnection);
        await auditConnection.OpenAsync();
        await DbAdminPrivilegeCheck.RunAsync(auditConnection);
    }
    return await AdminPasswordResetCli.RunAsync(app.Services, args, cliConnection);
}
if (dbCredentialOptions.Enabled)
{
    await app.Services.GetRequiredService<DbCredentialManager>().InitializeAsync();
}

app.MapDefaultEndpoints();

app.UseForwardedHeaders();

app.UseMiddleware<SecurityHeadersMiddleware>();
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/Auth", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }, context);
    }
    return next(context);
});

var swaggerSetting = app.Configuration["Swagger:Enabled"];
var swaggerEnabled = string.IsNullOrWhiteSpace(swaggerSetting)
    ? app.Environment.IsDevelopment()
    : bool.TryParse(swaggerSetting.Trim(), out var parsedSwagger)
        ? parsedSwagger
        : throw new InvalidOperationException("Swagger:Enabled deve ser true ou false.");
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();

app.UseCors(LocalCorsPolicy);

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();
    var seedOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>();
    await DbSeeder.SeedAsync(db, passwordHasher, seedOptions);

    if (!dbCredentialOptions.Enabled && db.Database.IsRelational())
    {
        await DbPrivilegeCheck.RunAsync(db, app.Configuration, app.Logger);
    }
}

app.Run();
return 0;

public partial class Program;
