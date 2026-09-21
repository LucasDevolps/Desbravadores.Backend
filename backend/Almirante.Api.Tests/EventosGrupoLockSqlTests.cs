using System.Net;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

// Coordenação de POST (com eventoReferenciaId), PUT, DELETE e pagamento por passeio (EventoGrupoId), contra SQL Server
// real, com intercalamentos FORÇADOS: uma requisição é parada em um comando SQL exato enquanto a outra corre; a prova de
// que a coordenação atuou é haver uma sessão esperando o applock do passeio (sys.dm_tran_locks), não um Task.Delay.
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosGrupoLockSqlTests : IAsyncLifetime
{
    private readonly FalhaDeCommitInterceptor _falha = new();
    private readonly PausaDeComandoInterceptor _pausa = new();
    private SqlServerApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(_falha, _pausa),
            services => services.AddSingleton(new EventosLockOptions(TimeoutMs: 1_500)));
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static bool InsereEvento(string sql) => sql.Contains("INSERT INTO [eventos]") || sql.Contains("MERGE [eventos]");
    private static bool AtualizaEventoPeloPut(string sql) => sql.Contains("UPDATE [eventos]");
    private static bool DesativaEvento(string sql) => sql.Contains("UPDATE dbo.eventos SET Ativo = 0");

    private Task<List<Guid>> Membros(int n) => EventosSqlSupport.MembrosAsync(_factory, n);
    private Task<T?> Sql<T>(string sql, params (string, object)[] p) => EventosSqlSupport.EscalarAsync<T>(_factory, sql, p);

    private async Task<List<(Guid Id, DateOnly Data, string Local)>> AtivosDoGrupoAsync(Guid grupo)
    {
        await using var conexao = new SqlConnection(_factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT Id, DataEvento, [Local] FROM eventos WHERE EventoGrupoId = @g AND Ativo = 1";
        comando.Parameters.AddWithValue("@g", grupo);
        var lista = new List<(Guid, DateOnly, string)>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync()) lista.Add((leitor.GetGuid(0), DateOnly.FromDateTime(leitor.GetDateTime(1)), leitor.GetString(2)));
        return lista;
    }

    private async Task AssertPasseioCoerenteAsync(Guid grupo)
    {
        var ativos = await AtivosDoGrupoAsync(grupo);
        Assert.True(ativos.Select(a => (a.Data, a.Local)).Distinct().Count() <= 1, "cadastros ativos do mesmo passeio com data/local divergentes");
        Assert.Equal(0, await Sql<int>("""
            SELECT COUNT(*) FROM (SELECT MembroId FROM evento_membros WHERE EventoGrupoId = @g AND Ativo = 1 GROUP BY MembroId HAVING COUNT(*) > 1) d
            """, ("@g", grupo)));
    }

    // Referência a um cadastro do passeio: o original, ou (quando o original já foi excluído) um cadastro secundário.
    private async Task<EventoDto> PreparaReferenciaAsync(bool secundariaComOriginalInativo)
    {
        var m = await Membros(3);
        var a = await EventosSqlSupport.CriarAsync(_client, m[0]);
        if (!secundariaComOriginalInativo) return a;

        var b = await EventosSqlSupport.CriarAsync(_client, m[1], a.Id);
        var exclusao = await EventosTestKit.DeleteAsync(_client, a.Id, "original saiu", a.Versao);
        Assert.Equal(HttpStatusCode.NoContent, exclusao.StatusCode);
        return b; // o passeio sobrevive: o grupo continua sendo o do original
    }

    // ------------------------------------------------------------------ POST antes do PUT

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostComReferencia_ValidadoEPausado_PutQueMudaLocalEsperaOLock_ENaoDivergeOPasseio(bool secundariaComOriginalInativo)
    {
        var referencia = await PreparaReferenciaAsync(secundariaComOriginalInativo);
        var novo = (await Membros(1))[0];

        // POST valida a referência (mesma data/local) e para no INSERT do novo cadastro
        var pausa = _pausa.Armar(InsereEvento);
        var post = EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, referencia.DataEvento, referencia.Local, referencia: referencia.Id));
        await pausa.Atingida;

        // PUT da referência altera data e local: a consulta "há outro cadastro ativo?" ainda não enxerga o novo
        var membrosDaReferencia = referencia.Membros;
        var put = EventosTestKit.PutAsync(_client, referencia.Id,
            EventosSqlSupport.Editar(referencia, membrosDaReferencia, local: "Parque Novo", data: referencia.DataEvento.AddDays(1)));
        await EventosSqlSupport.AguardarConclusaoOuBloqueioAsync(_factory, put);
        pausa.Liberar();
        var respostas = await Task.WhenAll(post, put);

        // com a coordenação, o PUT ficou esperando o passeio: o POST venceu e o PUT foi recusado (409) por haver 2 cadastros
        Assert.Equal(HttpStatusCode.Created, respostas[0].StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, respostas[1].StatusCode);
        await AssertPasseioCoerenteAsync(referencia.EventoGrupoId);
        Assert.Equal(referencia.DataEvento, (await AtivosDoGrupoAsync(referencia.EventoGrupoId)).Select(a => a.Data).Distinct().Single());
    }

    // ------------------------------------------------------------------ PUT antes do POST

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutPausadoAposValidar_PostComReferenciaEsperaORelock_ERevalidaDataELocal(bool secundariaComOriginalInativo)
    {
        var referencia = await PreparaReferenciaAsync(secundariaComOriginalInativo);
        var novo = (await Membros(1))[0];
        var novaData = referencia.DataEvento.AddDays(1);
        var chave = Guid.NewGuid().ToString();

        // PUT (único cadastro ativo do passeio) valida e para no UPDATE do cadastro
        var pausa = _pausa.Armar(AtualizaEventoPeloPut);
        var put = EventosTestKit.PutAsync(_client, referencia.Id,
            EventosSqlSupport.Editar(referencia, referencia.Membros, local: "Parque Novo", data: novaData));
        await pausa.Atingida;

        // POST usando a data/local ANTIGOS da referência: sem coordenação leria o estado antigo e gravaria o divergente
        var post = EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, referencia.DataEvento, referencia.Local, referencia: referencia.Id), chave);
        await EventosSqlSupport.AguardarConclusaoOuBloqueioAsync(_factory, post);
        pausa.Liberar();
        var respostas = await Task.WhenAll(put, post);

        Assert.Equal(HttpStatusCode.OK, respostas[0].StatusCode);
        // o POST releu a referência sob o lock: data/local agora divergem do que ele enviou → erro de negócio 400
        Assert.Equal(HttpStatusCode.BadRequest, respostas[1].StatusCode);
        var erros = JsonNode.Parse(await respostas[1].Content.ReadAsStringAsync())!["errors"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("DataEvento", erros);
        Assert.Contains("Local", erros);
        await AssertPasseioCoerenteAsync(referencia.EventoGrupoId);
        Assert.Single(await AtivosDoGrupoAsync(referencia.EventoGrupoId));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE MembroId = @m", ("@m", novo)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
    }

    // ------------------------------------------------------------------ POST x DELETE da referência

    [Fact]
    public async Task PostComReferenciaPausado_DeleteDaReferenciaEsperaOLock_PostVenceEOGrupoSobrevive()
    {
        var a = await EventosSqlSupport.CriarAsync(_client, (await Membros(1))[0]);
        var novo = (await Membros(1))[0];

        var pausa = _pausa.Armar(InsereEvento);
        var post = EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, a.DataEvento, a.Local, referencia: a.Id));
        await pausa.Atingida;
        var delete = EventosTestKit.DeleteAsync(_client, a.Id, "corrida com o POST", a.Versao);
        await EventosSqlSupport.AguardarConclusaoOuBloqueioAsync(_factory, delete);
        pausa.Liberar();
        var respostas = await Task.WhenAll(post, delete);

        // ordem válida: POST, depois DELETE. O novo cadastro existe, ativo, no mesmo passeio; o original foi excluído.
        Assert.Equal(HttpStatusCode.Created, respostas[0].StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, respostas[1].StatusCode);
        var criado = await EventosTestKit.LerAsync(respostas[0]);
        Assert.Equal(a.EventoGrupoId, criado.EventoGrupoId);
        Assert.Equal([criado.Id], (await AtivosDoGrupoAsync(a.EventoGrupoId)).Select(x => x.Id));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM historico_eventos WHERE EventoId = @e", ("@e", a.Id)));
    }

    [Fact]
    public async Task DeletePausadoAposValidar_PostComReferenciaEsperaORelock_Retorna404SemRegistrosParciais()
    {
        var a = await EventosSqlSupport.CriarAsync(_client, (await Membros(1))[0]);
        var novo = (await Membros(1))[0];
        var chave = Guid.NewGuid().ToString();

        var pausa = _pausa.Armar(DesativaEvento);
        var delete = EventosTestKit.DeleteAsync(_client, a.Id, "excluído primeiro", a.Versao);
        await pausa.Atingida;
        var post = EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, a.DataEvento, a.Local, referencia: a.Id), chave);
        await EventosSqlSupport.AguardarConclusaoOuBloqueioAsync(_factory, post);
        pausa.Liberar();
        var respostas = await Task.WhenAll(delete, post);

        Assert.Equal(HttpStatusCode.NoContent, respostas[0].StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, respostas[1].StatusCode);   // referência inativa não serve de referência
        Assert.Empty(await AtivosDoGrupoAsync(a.EventoGrupoId));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM eventos WHERE EventoGrupoId = @g", ("@g", a.EventoGrupoId)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE MembroId = @m", ("@m", novo)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM evento_membros WHERE MembroId = @m", ("@m", novo)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
    }

    // ------------------------------------------------------------------ contenção limitada e liberação do lock

    [Fact]
    public async Task LockDoPasseioOcupado_RequisicaoDesistePorTimeout409_SemRegistros_ELockLiberadoDepois()
    {
        var a = await EventosSqlSupport.CriarAsync(_client, (await Membros(1))[0]);
        var novo = (await Membros(1))[0];
        var recurso = EventosService.RecursoDoGrupo(a.EventoGrupoId);

        await using (var dono = await SqlIdentityEnvironment.OpenHarnessAsync(new SqlConnectionStringBuilder(_factory.ConnectionString).InitialCatalog))
        {
            await using var tx = (SqlTransaction)await dono.BeginTransactionAsync();
            await using (var cmd = dono.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource=@res, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=0; SELECT @r;";
                cmd.Parameters.AddWithValue("@res", recurso);
                Assert.True((int)(await cmd.ExecuteScalarAsync())! >= 0);
            }

            var inicio = DateTime.UtcNow;
            var post = await EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, a.DataEvento, a.Local, referencia: a.Id));
            var put = await EventosTestKit.PutAsync(_client, a.Id, EventosSqlSupport.Editar(a, a.Membros, transporte: 11m));
            var delete = await EventosTestKit.DeleteAsync(_client, a.Id, "com lock ocupado", a.Versao);

            Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.True(DateTime.UtcNow - inicio < TimeSpan.FromSeconds(20), "a espera pelo lock deve ser limitada");
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE MembroId = @m", ("@m", novo)));
            Assert.Equal(1, await Sql<int>("SELECT CAST(Ativo AS int) FROM eventos WHERE Id = @e", ("@e", a.Id)));
            await tx.RollbackAsync();
        }

        // sem o lock externo, as mesmas operações passam: nada ficou preso na API (locks liberados por commit/rollback)
        var ok = await EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, a.DataEvento, a.Local, referencia: a.Id));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(0, await EventosSqlSupport.EsperasDeApplockAsync(_factory));
        Assert.Equal(0, await EventosSqlSupport.ContarApplocksAsync(_factory, apenasEsperando: false));   // nenhum lock de passeio ficou preso
    }

    // ------------------------------------------------------------------ ausência de deadlock nas combinações

    [Fact]
    public async Task PagamentoPutPostEDeleteSimultaneosNoMesmoPasseio_NaoGeramDeadlockNemEstadoIncoerente()
    {
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var m = await Membros(3);
            var a = await EventosSqlSupport.CriarAsync(_client, new[] { m[0], m[1] });
            var novo = m[2];

            var pagar = EventosSqlSupport.PagarAsync(_client, a.Lancamentos[0].Id);
            var editar = EventosTestKit.PutAsync(_client, a.Id, EventosSqlSupport.Editar(a, a.Membros, transporte: 33m));
            var criar = EventosTestKit.PostAsync(_client, EventosTestKit.Corpo(novo, a.DataEvento, a.Local, referencia: a.Id));
            var excluir = EventosTestKit.DeleteAsync(_client, a.Id, "todas juntas", a.Versao);
            var codigos = (await Task.WhenAll(pagar, editar, criar, excluir)).Select(r => r.StatusCode).ToList();

            Assert.DoesNotContain(HttpStatusCode.InternalServerError, codigos);
            await AssertPasseioCoerenteAsync(a.EventoGrupoId);
            // pagamento e exclusão são incompatíveis: se algum lançamento ficou pago, o cadastro continua ativo
            var pago = await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE EventoId = @e AND Ativo = 1 AND Status <> 0", ("@e", a.Id));
            var ativo = await Sql<int>("SELECT CAST(Ativo AS int) FROM eventos WHERE Id = @e", ("@e", a.Id));
            Assert.False(pago > 0 && ativo == 0, "lançamento pago em cadastro excluído");
        }

        Assert.DoesNotContain(_factory.Logs.Entries, e => e.Contains("deadlock", StringComparison.OrdinalIgnoreCase) || e.Contains("1205"));
    }
}
