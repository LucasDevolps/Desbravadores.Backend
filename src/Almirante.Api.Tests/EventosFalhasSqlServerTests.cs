using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public sealed class EventosFalhasFixture : IAsyncLifetime
{
    public FalhasInjetaveisNoBanco Falhas { get; } = new();
    public SqlServerApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(Falhas, new FalhasInjetaveisNoBanco.Comandos(Falhas)));
        Client = await TestHelpers.CreateAuthenticatedClientAsync(Factory);
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Eventos sob falhas de concorrência contra SQL Server: violação de unicidade simultânea, concorrência otimista, resposta idempotente
/// ilegível, exclusão cujo UPDATE não afeta a linha e parâmetros de lock recusados. Em todos, a resposta é explícita e nada fica gravado
/// pela metade (a transação inteira é desfeita).
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosFalhasSqlServerTests(EventosFalhasFixture fx) : IClassFixture<EventosFalhasFixture>
{
    private static Exception ViolacaoDeUnicidade() => new DbUpdateException("simulada", SqlExceptionDeTeste.Criar(2601));

    private Task<int> ContarAsync(string sql, params (string, object)[] parametros) =>
        EventosSqlSupport.EscalarAsync<int>(fx.Factory, sql, parametros);

    private static async Task<string> TituloAsync(HttpResponseMessage resposta, HttpStatusCode esperado)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.True(resposta.StatusCode == esperado, $"esperado {(int)esperado}, obtido {(int)resposta.StatusCode}: {corpo}");
        return (string?)JsonNode.Parse(corpo)?["title"] ?? "";
    }

    [Fact]
    public async Task Cadastro_ViolacaoDeUnicidadeSimultanea_SemOperacaoGravada_Responde409SemGravarNada()
    {
        var membros = await EventosSqlSupport.MembrosAsync(fx.Factory, 2);
        var referencia = await EventosSqlSupport.CriarAsync(fx.Client, membros[0]);
        var antes = await ContarAsync("SELECT COUNT(*) FROM dbo.eventos");

        fx.Falhas.FalharProximoSave(ViolacaoDeUnicidade);
        var comReferencia = await EventosTestKit.PostAsync(fx.Client, EventosTestKit.Corpo(membros[1], referencia.DataEvento, referencia.Local,
            referencia: referencia.Id), Guid.NewGuid().ToString());
        Assert.Equal("Conflito de concorrência.", await TituloAsync(comReferencia, HttpStatusCode.Conflict));

        fx.Falhas.FalharProximoSave(ViolacaoDeUnicidade);
        var semReferencia = await EventosTestKit.PostAsync(fx.Client, EventosTestKit.Corpo(membros[1]), Guid.NewGuid().ToString());
        Assert.Equal("Conflito de concorrência.", await TituloAsync(semReferencia, HttpStatusCode.Conflict));

        Assert.Equal(antes, await ContarAsync("SELECT COUNT(*) FROM dbo.eventos"));
        Assert.Equal(0, await ContarAsync("SELECT COUNT(*) FROM dbo.Lancamentos WHERE MembroId = @m", ("@m", membros[1])));
    }

    [Fact]
    public async Task Edicao_ViolacaoDeUnicidadeOuConcorrenciaOtimista_Responde409ENadaMuda()
    {
        var membros = await EventosSqlSupport.MembrosAsync(fx.Factory, 2);
        var evento = await EventosSqlSupport.CriarAsync(fx.Client, membros[0]);

        fx.Falhas.FalharProximoSave(ViolacaoDeUnicidade);
        var unicidade = await EventosTestKit.PutAsync(fx.Client, evento.Id, EventosSqlSupport.Editar(evento, membros, local: "Outro local"));
        Assert.Equal("Conflito de concorrência.", await TituloAsync(unicidade, HttpStatusCode.Conflict));

        fx.Falhas.FalharProximoSave(() => new DbUpdateConcurrencyException("simulada"));
        var versao = await EventosTestKit.PutAsync(fx.Client, evento.Id, EventosSqlSupport.Editar(evento, membros, local: "Outro local"));
        Assert.Equal("Versão do evento desatualizada.", await TituloAsync(versao, HttpStatusCode.Conflict));

        Assert.Equal(evento.Local, (await fx.Client.GetFromJsonAsync<JsonObject>($"/api/Eventos/{evento.Id}"))!["local"]!.GetValue<string>());
        Assert.Equal(0, await ContarAsync("SELECT COUNT(*) FROM dbo.Lancamentos WHERE MembroId = @m", ("@m", membros[1])));
    }

    [Fact]
    public async Task Reenvio_ComRespostaOriginalIlegivel_FalhaSemRecriarOEvento()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(fx.Factory, 1))[0];
        var chave = Guid.NewGuid().ToString();
        var corpo = EventosTestKit.Corpo(membro);
        Assert.Equal(HttpStatusCode.Created, (await EventosTestKit.PostAsync(fx.Client, corpo, chave)).StatusCode);
        // Dado corrompido fora das garantias do banco: a CHECK (ISJSON) é suspensa só para gravar o literal null e volta em seguida.
        Assert.Equal(1, await ContarAsync("""
            ALTER TABLE dbo.eventos_operacoes NOCHECK CONSTRAINT CK_eventos_operacoes_RespostaJson;
            UPDATE dbo.eventos_operacoes SET RespostaJson = 'null' WHERE IdempotencyKey = @k;
            DECLARE @linhas int = @@ROWCOUNT;
            ALTER TABLE dbo.eventos_operacoes WITH NOCHECK CHECK CONSTRAINT CK_eventos_operacoes_RespostaJson;
            SELECT @linhas;
            """, ("@k", chave)));

        var reenvio = await EventosTestKit.PostAsync(fx.Client, corpo, chave);

        Assert.Equal(HttpStatusCode.InternalServerError, reenvio.StatusCode);
        Assert.Equal(1, await ContarAsync("SELECT COUNT(*) FROM dbo.Lancamentos WHERE MembroId = @m", ("@m", membro)));
    }

    [Fact]
    public async Task Exclusao_QueNaoDesativaOEventoTravado_FalhaEDesfazTudo()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(fx.Factory, 1))[0];
        var evento = await EventosSqlSupport.CriarAsync(fx.Client, membro);

        fx.Falhas.AnularProximoComando(sql => sql.Contains("UPDATE dbo.eventos SET Ativo = 0", StringComparison.Ordinal));
        var exclusao = await EventosTestKit.DeleteAsync(fx.Client, evento.Id, "cancelado pelo teste", evento.Versao);

        Assert.Equal(HttpStatusCode.InternalServerError, exclusao.StatusCode);
        Assert.Equal(1, await ContarAsync("SELECT COUNT(*) FROM dbo.eventos WHERE Id = @e AND Ativo = 1", ("@e", evento.Id)));
        Assert.Equal(1, await ContarAsync("SELECT COUNT(*) FROM dbo.Lancamentos WHERE EventoId = @e AND Ativo = 1", ("@e", evento.Id)));
        Assert.Equal(0, await ContarAsync("SELECT COUNT(*) FROM dbo.historico_eventos WHERE EventoId = @e", ("@e", evento.Id)));
    }

    [Fact]
    public async Task LockDoPasseio_ComParametroRecusadoPeloSqlServer_FalhaSemAlterarNada()
    {
        using var factory = new SqlServerApiFactory(configureServices: s => s.AddSingleton(new EventosLockOptions(TimeoutMs: -2)));
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membros = await EventosSqlSupport.MembrosAsync(factory, 2);
        var evento = await EventosSqlSupport.CriarAsync(client, membros[0]);

        var edicao = await EventosTestKit.PutAsync(client, evento.Id, EventosSqlSupport.Editar(evento, membros, local: "Outro local"));

        Assert.Equal(HttpStatusCode.InternalServerError, edicao.StatusCode);
        Assert.Equal(evento.Local, (await client.GetFromJsonAsync<JsonObject>($"/api/Eventos/{evento.Id}"))!["local"]!.GetValue<string>());
    }
}
