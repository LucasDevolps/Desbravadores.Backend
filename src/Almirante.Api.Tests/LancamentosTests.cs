using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public sealed class LancamentosTests : IClassFixture<AlmiranteApiFactory>
{
    private readonly AlmiranteApiFactory factory;
    public LancamentosTests(AlmiranteApiFactory factory) => this.factory = factory;
    private static object Body(Guid? id, bool todos, decimal valor=50, DateOnly? vencimento=null) => new
    { membroId=id, finalidade="Mensalidade", descricao="Outubro", categoria="Clube", tipoFluxo="Entrada", valor,
      vencimento=(vencimento??DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)).ToString("yyyy-MM-dd"), aplicarATodosOsMembros=todos };

    [Fact]
    public async Task Registrar_ExigeAutenticacaoERoleFinanceira()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(Guid.NewGuid(), false))).StatusCode);
        var (unauthorizedRole, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await unauthorizedRole.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(Guid.NewGuid(), false))).StatusCode);
    }

    [Fact]
    public async Task RegistrarIndividual_UsaMembroDoBanco_EStatusPendente()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Nome confiável", "DS");
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var response = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id,false));
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var dto=await response.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal("Nome confiável",dto!.MembroNome); Assert.Equal(StatusLancamento.Pendente,dto.Status); Assert.Equal(membro.Id,dto.MembroId);
    }

    [Fact]
    public async Task RegistrarIndividual_RejeitaIdAusente_EInexistente()
    {
        var client=await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(null,false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(Guid.NewGuid(),false))).StatusCode);
    }

    [Fact]
    public async Task RegistrarIndividual_MembroInativo_Retorna404_SemNotificacao()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Inativo individual", "DS");
        await TestHelpers.WithDbAsync(factory, async db => { (await db.Usuarios.SingleAsync(u => u.Id == membro.Id)).Ativo = false; return await db.SaveChangesAsync(); });
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id, false))).StatusCode);
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AnyAsync(l => l.MembroId == membro.Id)));
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AnyAsync(n => n.DestinatarioId == membro.Id)));
    }

    [Fact]
    public async Task RegistrarTodos_IgnoraMembrosInativos()
    {
        var ativo = await TestHelpers.AddUsuarioAsync(factory, "Geral ativo", "DS");
        var inativo = await TestHelpers.AddUsuarioAsync(factory, "Geral inativo", "DS");
        await TestHelpers.WithDbAsync(factory, async db => { (await db.Usuarios.SingleAsync(u => u.Id == inativo.Id)).Ativo = false; return await db.SaveChangesAsync(); });
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar") { Content = JsonContent.Create(Body(null, true)) };
        req.Headers.Add("Idempotency-Key", $"inativos-{Guid.NewGuid():N}");

        var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var geral = await response.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        var ativos = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync(u => u.Ativo));
        Assert.Equal(ativos, geral!.LancamentosCriados);
        Assert.True(await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AnyAsync(l => l.MembroId == ativo.Id && l.OperacaoId == geral.OperacaoId)));
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AnyAsync(l => l.MembroId == inativo.Id)));
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AnyAsync(n => n.DestinatarioId == inativo.Id)));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public async Task Registrar_RejeitaValorNaoPositivo(decimal valor)
    {
        var client=await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(Guid.NewGuid(),false,valor))).StatusCode);
    }

    [Fact]
    public async Task Registrar_RejeitaDataPassada_EAceitaHoje()
    {
        var membro=await TestHelpers.AddUsuarioAsync(factory,"Data","DS");
        var client=await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(membro.Id,false,50,DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(membro.Id,false,50,DateOnly.FromDateTime(DateTime.UtcNow)))).StatusCode);
    }

    [Fact]
    public async Task RegistrarTodos_EIdempotente_EConflitaPayloadDiferente()
    {
        await TestHelpers.AddUsuarioAsync(factory,"Lote","DS");
        var client=await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var idempotencyKey = $"lote-{Guid.NewGuid():N}";
        async Task<HttpResponseMessage> Send(decimal valor)
        { var req=new HttpRequestMessage(HttpMethod.Post,"/api/Lancamentos/Registrar") { Content=JsonContent.Create(Body(null,true,valor)) }; req.Headers.Add("Idempotency-Key",idempotencyKey); return await client.SendAsync(req); }
        var first=await Send(50); var retry=await Send(50); var conflict=await Send(51);
        Assert.Equal(HttpStatusCode.OK,first.StatusCode); Assert.Equal(HttpStatusCode.OK,retry.StatusCode); Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<LancamentoGeralResponse>())!.OperacaoId,(await retry.Content.ReadFromJsonAsync<LancamentoGeralResponse>())!.OperacaoId);
    }

    [Fact]
    public async Task Update_RegistraUsuarioResponsavelAPartirDaIdentidadeAutenticada()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Atualizar", "DS");
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");
        var created = await (await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id, false))).Content.ReadFromJsonAsync<LancamentoDto>();

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{created!.Id}", new { status = "Atrasado" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entity = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.SingleAsync(l => l.Id == created.Id));
        Assert.Equal(usuarioId, entity.AtualizadoPorUsuarioId);
        Assert.NotNull(entity.DataAtualizacao);
    }

    [Fact]
    public async Task Update_IgnoraUsuarioResponsavelEnviadoNoBody_UsaSempreAIdentidadeAutenticada()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Forjar", "DS");
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");
        var created = await (await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id, false))).Content.ReadFromJsonAsync<LancamentoDto>();

        var forjado = Guid.NewGuid();
        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{created!.Id}", new { status = "Atrasado", usuarioResponsavelId = forjado });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entity = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.SingleAsync(l => l.Id == created.Id));
        Assert.Equal(usuarioId, entity.AtualizadoPorUsuarioId);
        Assert.NotEqual(forjado, entity.AtualizadoPorUsuarioId);
    }

    [Fact]
    public async Task Update_RetornaNotFound_ParaLancamentoInexistente()
    {
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{Guid.NewGuid()}", new { status = "Atrasado" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_RegistroHistoricoComResponsavelNulo_ContinuaValidoEPassaAReceberOResponsavel()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Historico", "DS");
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");
        var created = await (await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id, false))).Content.ReadFromJsonAsync<LancamentoDto>();

        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var entity = await db.Lancamentos.SingleAsync(l => l.Id == created!.Id);
            entity.AtualizadoPorUsuarioId = null;
            await db.SaveChangesAsync();
            return true;
        });

        var list = await (await client.GetAsync("/api/Lancamentos?search=Historico")).Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.Single(list!.Items);

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{created!.Id}", new { status = "Atrasado" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entityAtualizado = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.SingleAsync(l => l.Id == created.Id));
        Assert.Equal(usuarioId, entityAtualizado.AtualizadoPorUsuarioId);
    }

    [Fact]
    public async Task Delete_EhLogico_EListagemIgnoraInativo()
    {
        var membro=await TestHelpers.AddUsuarioAsync(factory,"Excluir","DS");
        var client=await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var created=await (await client.PostAsJsonAsync("/api/Lancamentos/Registrar",Body(membro.Id,false))).Content.ReadFromJsonAsync<LancamentoDto>();
        var req=new HttpRequestMessage(HttpMethod.Delete,$"/api/Lancamentos/{created!.Id}") { Content=JsonContent.Create(new { motivo="correção" }) };
        Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(req)).StatusCode);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        Assert.False((await db.Lancamentos.IgnoreQueryFilters().SingleAsync(x=>x.Id==created.Id)).Ativo);
        var list=await (await client.GetAsync("/api/Lancamentos?search=Excluir")).Content.ReadFromJsonAsync<LancamentosResponse>(); Assert.Empty(list!.Items);
    }
}
