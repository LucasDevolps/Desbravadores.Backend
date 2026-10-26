using System.Net;
using Almirante.Api.Cli;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public sealed class AdminPasswordResetCliTests : IClassFixture<AlmiranteApiFactory>
{
    private readonly AlmiranteApiFactory factory;
    public AdminPasswordResetCliTests(AlmiranteApiFactory factory) => this.factory = factory;

    private (AlmiranteDbContext Db, IPasswordHasher<Usuario> Hasher, TimeProvider Clock, IServiceScope Scope) ResolveDependencies()
    {
        var scope = factory.Services.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope);
    }

    [Fact]
    public async Task ResetPasswordAsync_EncerraRecuperacaoAberta_SemGravarAuditoriaDeResponsavel()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "ComPedidoAberto", "DS");
        var recuperacaoId = Guid.NewGuid();
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            db.RecuperacoesSenha.Add(new RecuperacaoSenha
            {
                Id = recuperacaoId, UsuarioId = usuario.Id, Origem = OrigemRecuperacaoSenha.SolicitacaoPublica, Status = StatusRecuperacaoSenha.Pendente,
                SolicitadaEmUtc = DateTime.UtcNow, UltimaSolicitacaoEmUtc = DateTime.UtcNow, Solicitacoes = 1, IpSolicitacao = "127.0.0.1",
                ExpiraEmUtc = DateTime.UtcNow.AddDays(7),
            });
            return await db.SaveChangesAsync();
        });

        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, _) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "Uma frase nova e bem forte 2026");
            Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado);
        }

        var recuperacao = await TestHelpers.WithDbAsync(factory, db => db.RecuperacoesSenha.AsNoTracking().SingleAsync(r => r.Id == recuperacaoId));
        Assert.Equal((StatusRecuperacaoSenha.Invalidada, "reset-senha-admin"), (recuperacao.Status, recuperacao.MotivoEncerramento));
        Assert.NotNull(recuperacao.EncerradaEmUtc);
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.AlteracoesSenha.AnyAsync(a => a.UsuarioAfetadoId == usuario.Id)));
    }

    [Fact]
    public async Task ResetPasswordAsync_AtualizaHash_ERevogaSessaoAtiva_ImpedindoRefreshAntigo()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "ParaResetarSenha", "DS");
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, usuario.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);

        const string novaSenha = "Uma$enhaNovaBemForte2026";
        var (db, hasher, clock, scope) = ResolveDependencies();
        (AdminPasswordResetCli.ResetResult Resultado, IReadOnlyList<string> Erros) resultado;
        using (scope)
        {
            resultado = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, novaSenha);
        }

        Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado.Resultado);
        Assert.Empty(resultado.Erros);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/Auth/refresh", null)).StatusCode);

        var novoClient = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novoClient);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(novoClient, usuario.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(novoClient, usuario.Email, novaSenha)).StatusCode);
    }

    [Fact]
    public async Task ResetPasswordAsync_UsuarioInexistente_RetornaResultadoEspecifico_SemAlterarNada()
    {
        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, erros) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, "nao-existe@local.dev", "Qualquer$enhaForte123");
            Assert.Equal(AdminPasswordResetCli.ResetResult.UsuarioNaoEncontrado, resultado);
            Assert.Empty(erros);
        }
    }

    [Fact]
    public async Task ResetPasswordAsync_SenhaForaDaPolitica_NaoAlteraOHashExistente()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "SenhaFraca", "DS");
        var hashAntes = usuario.SenhaHash;

        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, erros) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "senha");
            Assert.Equal(AdminPasswordResetCli.ResetResult.SenhaInvalida, resultado);
            Assert.NotEmpty(erros);
        }

        var hashDepois = await TestHelpers.WithDbAsync(factory, db =>
            db.Usuarios.AsNoTracking().Where(u => u.Id == usuario.Id).Select(u => u.SenhaHash).SingleAsync());
        Assert.Equal(hashAntes, hashDepois);
    }
}
