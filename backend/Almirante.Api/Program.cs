using System.Text;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
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
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UsuariosService>();
builder.Services.AddScoped<LancamentosService>();
builder.Services.AddScoped<LancamentosGeraisService>();
builder.Services.AddScoped<CargosService>();

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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Resolved lazily via IOptions<JwtOptions> (post configuration-merge) so the exact same
// settings back both token issuance (JwtTokenService) and token validation below.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
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
    ?? ["http://localhost:4200", "http://localhost:4201"];

builder.Services.AddCors(options =>
{
    options.AddPolicy(LocalCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
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
