using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
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

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations().Validate(o => o.Keys.ContainsKey(o.ActiveKeyId), "ActiveKeyId deve existir em Keys.")
    .Validate(o => { try { foreach (var kid in o.Keys.Keys) _ = JwtKeySet.GetKey(o, kid); return true; } catch { return false; } }, "Todas as chaves devem ser Base64 e ter ao menos 32 bytes.")
    .ValidateOnStart();
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection(ReverseProxyOptions.SectionName));

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
builder.Services.AddScoped<UsuariosService>();
builder.Services.AddScoped<LancamentosService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CargosService>();
builder.Services.AddScoped<AuthSessionCleanup>();
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

builder.Services.AddControllers().AddJsonOptions(options =>
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
builder.Services.AddSingleton<CookieCsrfProtection>();

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
        // TokenHandlers é o pipeline usado pelo JwtBearer desde o .NET 8 (SecurityTokenValidators é ignorado).
        bearerOptions.TokenHandlers.Clear();
        bearerOptions.TokenHandlers.Add(new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = JwtProfile.MaximumTokenSizeInBytes });
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
            ValidTypes = [JwtProfile.TokenType],
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtProfile.RoleClaim,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        bearerOptions.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (context.SecurityToken is not JsonWebToken jwt || !JwtProfile.HasUnambiguousPayload(jwt) ||
                    JwtProfile.RequiredClaims.Any(type => principal.FindAll(type).Count() != 1) ||
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

builder.Services.AddAuthorization();

// Único IAuthorizationMiddlewareResultHandler da aplicação. Delega para o comportamento padrão em
// todos os endpoints, exceto os restritos por role ([AutorizarRoles] ou [Authorize(Roles=...)]),
// onde qualquer falha de autorização vira 401 "ACESSO NEGADO!" (ver o handler para detalhes) — não
// precisa registrar nada aqui por controller/policy, funciona para qualquer um que use o atributo.
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AcessoNegadoAuthorizationMiddlewareResultHandler>();

const string LocalCorsPolicy = "LocalFrontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://localhost:4200", "https://localhost:4201"];

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

    options.OperationFilter<CsrfHeaderOperationFilter>();

    options.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", null, null),
            new List<string>()
        },
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseSwagger();
app.UseSwaggerUI();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Precisa vir antes de qualquer middleware que dependa do IP/esquema reais (redirect HTTPS,
// autenticação, autorização), para que HttpContext.Connection.RemoteIpAddress e Request.Scheme
// já reflitam o cliente original quando esses middlewares executarem.
app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors(LocalCorsPolicy);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/Auth", StringComparison.OrdinalIgnoreCase))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});

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

public partial class Program;
