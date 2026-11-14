using System.Security.Claims;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>
/// Autorização por permissões (issue #182) fora do pipeline HTTP: EF InMemory com os 17 cargos e a matriz inicial semeados como o DbSeeder
/// faz, o <see cref="PermissaoPolicyProvider"/> e o <see cref="PermissaoAuthorizationHandler"/> registrados como em Program.cs.
/// </summary>
public sealed class AutorizacaoTestKit : IDisposable
{
    private readonly ServiceProvider _services;

    public AutorizacaoTestKit()
    {
        var banco = Guid.NewGuid().ToString();
        _services = new ServiceCollection().AddLogging().AddAuthorization(Policies.Registrar)
            .AddDbContext<AlmiranteDbContext>(o => o.UseInMemoryDatabase(banco))
            .AddSingleton<IAuthorizationPolicyProvider, PermissaoPolicyProvider>()
            .AddScoped<PermissoesService>()
            .AddScoped<IAuthorizationHandler, PermissaoAuthorizationHandler>()
            .BuildServiceProvider();

        Executar(db =>
        {
            db.Database.EnsureCreated();
            var cargos = MatrizInicialAutorizacao.CargosConhecidos.Select(role => new Cargo
            {
                Id = Guid.NewGuid(), Nome = $"Cargo {role}", Descricao = "Teste", CriadoPor = "Teste", Role = role,
                SistemaProtegido = MatrizInicialAutorizacao.Protegidos.Contains(role),
            }).ToList();
            db.Cargos.AddRange(cargos);
            DbSeeder.SemearAutorizacao(db, cargos, DateTime.UtcNow);
            db.SaveChanges();
        });
    }

    public void Executar(Action<AlmiranteDbContext> acao)
    {
        using var scope = _services.CreateScope();
        acao(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>());
    }

    public T Executar<T>(Func<AlmiranteDbContext, T> acao)
    {
        using var scope = _services.CreateScope();
        return acao(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>());
    }

    public Guid CargoId(string role) => Executar(db => db.Cargos.Single(c => c.Role == role).Id);

    /// <summary>Usuário ativo com o cargo; devolve o principal como o JwtBearer o deixaria (sub e role).</summary>
    public ClaimsPrincipal Usuario(string role, Action<Usuario>? ajustar = null)
    {
        var id = Guid.NewGuid();
        Executar(db =>
        {
            var usuario = new Usuario
            {
                Id = id, Nome = $"Usuário {role}", Email = $"{id:N}@local.dev", EmailNormalizado = $"{id:N}@LOCAL.DEV", SenhaHash = "x",
                CargoId = db.Cargos.Single(c => c.Role == role).Id, Ativo = true,
            };
            ajustar?.Invoke(usuario);
            db.Usuarios.Add(usuario);
            db.SaveChanges();
        });
        return Principal(id, role);
    }

    public static ClaimsPrincipal Principal(Guid id, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("role", role)], "Bearer", "sub", "role"));

    /// <summary>Chaves do catálogo concedidas ao principal pela policy perm:&lt;chave&gt;, em escopo novo (como uma requisição nova).</summary>
    public async Task<string[]> ConcedidasAsync(ClaimsPrincipal usuario, IEnumerable<string>? chaves = null)
    {
        using var scope = _services.CreateScope();
        var autorizacao = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var lista = new List<string>();
        foreach (var chave in chaves ?? CatalogoPermissoes.Todas.Select(p => p.Chave))
        {
            if ((await autorizacao.AuthorizeAsync(usuario, PermissaoPolicyProvider.Prefixo + chave)).Succeeded) lista.Add(chave);
        }

        return [.. lista];
    }

    public async Task<T> ComPermissoesAsync<T>(Func<PermissoesService, Task<T>> acao)
    {
        using var scope = _services.CreateScope();
        return await acao(scope.ServiceProvider.GetRequiredService<PermissoesService>());
    }

    public void Dispose() => _services.Dispose();
}
