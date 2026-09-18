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
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
// Rotação de chave (issue #50): compose.yaml passa a mapear Jwt__Keys__v1/v2 como opcionais
// (${JWT_KEY_V1:-}), então uma entrada "não configurada" chega aqui como string vazia em vez de
// simplesmente ausente do dicionário. Remove essas entradas antes de qualquer validação/uso — sem
// isso, JwtOptionsValidator (abaixo) recusaria o startup por causa de uma chave que o operador nunca
// pretendeu configurar. Não muda o suporte existente a múltiplas chaves por "kid": só limpa entradas
// vazias antes dele agir.
builder.Services.PostConfigure<JwtOptions>(options =>
{
    foreach (var kid in options.Keys.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList())
    {
        options.Keys.Remove(kid);
    }
});
// Falha clara no startup para chave ausente, placeholder, Base64 inválido ou < 32 bytes (HS256).
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<JwtOptions>, JwtOptionsValidator>();
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection(ReverseProxyOptions.SectionName));

// Falha clara no startup em Production com TrustServerCertificate=True ou Encrypt=False na connection
// string do SQL Server (ver SqlServerConnectionSecurityValidator); Development continua permitindo
// certificado autoassinado.
builder.Services.AddOptions<ConnectionStringsOptions>().Bind(builder.Configuration.GetSection(ConnectionStringsOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ConnectionStringsOptions>, SqlServerConnectionSecurityValidator>();

// Só confia nos headers X-Forwarded-For/X-Forwarded-Proto quando ReverseProxy:TrustedNetworkCidr
// estiver configurado (Docker/nginx). Sem essa configuração, ForwardedHeaders permanece "None"
// (padrão) e o middleware não faz nada — preserva o comportamento atual fora do Docker (ex.: IIS),
// onde não existe esse proxy e o IP/esquema já chegam corretos por outros meios.
// Quando configurado, KnownProxies é limpo e substituído por essa única rede conhecida: um
// X-Forwarded-For enviado diretamente pelo cliente só é aceito se a conexão imediata (o nginx)
// vier dessa rede, e ForwardLimit=1 garante que só o valor mais à direita (o que o nginx
// efetivamente observou) é usado — qualquer valor forjado à esquerda pelo cliente é ignorado.
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
        // Totalmente qualificado: Microsoft.AspNetCore.HttpOverrides (acima) também expõe um tipo
        // IPNetwork (obsoleto), o que tornaria "IPNetwork" ambíguo sem qualificação aqui.
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(trustedNetworkCidr));
    });

builder.AddSqlServerDbContext<AlmiranteDbContext>("almirante");

builder.Services.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<LoginLockout>();
builder.Services.AddScoped<UsuariosService>();
builder.Services.AddScoped<LancamentosService>();
builder.Services.AddScoped<CargosService>();
builder.Services.AddLoginRateLimiting(builder.Configuration);

// HSTS (emitido por SecurityHeadersMiddleware fora de Development e só em HTTPS). Padrão: 365 dias,
// sem includeSubDomains/preload — só habilite esses dois após confirmar que TODOS os subdomínios
// do domínio publicado servem HTTPS (preload é praticamente irreversível).
builder.Services.AddHsts(options =>
{
    var hsts = builder.Configuration.GetSection("Hsts");
    options.MaxAge = TimeSpan.FromDays(hsts.GetValue("MaxAgeDays", 365));
    options.IncludeSubDomains = hsts.GetValue("IncludeSubDomains", false);
    options.Preload = hsts.GetValue("Preload", false);
});
builder.Services.AddHostedService<AuthSessionCleanupService>();

// MediatR: os DTOs de request em Dtos/LancamentoDtos.cs implementam IRequest<T> diretamente (sem
// uma camada paralela de "Command"). ValidationBehavior roda todo IValidator<TRequest> registrado
// (AddValidatorsFromAssemblyContaining abaixo) antes do Handler, rejeitando entrada inválida com
// FluentValidation.ValidationException — tratada globalmente por ValidationExceptionHandler.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// AddControllersWithViews (em vez de AddControllers) registra ValidateAntiforgeryTokenAuthorizationFilter,
// exigido pelo [ValidateAntiForgeryToken] do AuthController; não há Views/Razor pages neste projeto.
builder.Services.AddControllersWithViews().AddJsonOptions(options =>
{
    // ASP.NET Core já usa camelCase por padrão; mantido explícito para clareza.
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
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

// Resolved lazily via IOptions<JwtOptions> (post configuration-merge) so the exact same
// settings back both token issuance (JwtTokenService) and token validation below.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearerOptions.MapInboundClaims = false;
        bearerOptions.SaveToken = false;
        // TokenHandlers (e não SecurityTokenValidators, obsoleto e ignorado por padrão desde o .NET 8)
        // é a coleção efetivamente usada na validação; o limite de tamanho só vale se estiver aqui.
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

// RBAC: a role vem da claim "role" (RoleClaimType acima) e, a cada request, OnTokenValidated confere
// que ela ainda é a role do cargo ativo do usuário no banco. Matriz documentada em Security/Roles.cs.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.GestaoFinanceira, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Diretoria));
    options.AddPolicy(Policies.GestaoCadastros, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Diretoria));
});

// Único IAuthorizationMiddlewareResultHandler da aplicação: sem autenticação válida -> 401 (challenge
// padrão); autenticado sem permissão -> 403 "ACESSO NEGADO!".
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

// Ferramenta local de rotação de senha do admin (issue #50): roda antes do pipeline HTTP normal e
// sai em seguida, sem subir o host web. Ver Cli/AdminPasswordResetCli.cs.
if (args.Length > 0 && string.Equals(args[0], AdminPasswordResetCli.CommandName, StringComparison.OrdinalIgnoreCase))
{
    return await AdminPasswordResetCli.RunAsync(app.Services, args);
}

// Dispara agora as validações registradas com ValidateOnStart (JwtOptions, ConnectionStringsOptions):
// por padrão elas só rodam dentro de app.Run() (quando o host efetivamente inicia), o que é DEPOIS do
// seed/migração do SQL Server logo abaixo. Sem esta chamada explícita, uma connection string insegura
// em Production (TrustServerCertificate=True/Encrypt=False) chegaria a conectar e migrar o banco antes
// da falha de startup do SqlServerConnectionSecurityValidator ser lançada.
app.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();

app.MapDefaultEndpoints();

// Ordem do pipeline (cada item depende dos anteriores):
// 1. ForwardedHeaders primeiro: IP/esquema reais (só de proxy confiável) antes de HSTS, redirect
//    HTTPS, rate limiting (partição por IP), auditoria de IP e autenticação.
app.UseForwardedHeaders();

// 2. Cabeçalhos de segurança antes de tudo que pode gerar resposta (Swagger, erros, 401/403/404/429).
app.UseMiddleware<SecurityHeadersMiddleware>();
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/Auth", StringComparison.OrdinalIgnoreCase))
    {
        // OnStarting: sobrevive ao Response.Clear() do UseExceptionHandler.
        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }, context);
    }
    return next(context);
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();

app.UseCors(LocalCorsPolicy);

// 3. Rate limiting antes da autenticação: requisições rejeitadas não consultam sessão no banco nem
//    chegam ao antiforgery/controller.
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
}

app.Run();
return 0;

public partial class Program;
