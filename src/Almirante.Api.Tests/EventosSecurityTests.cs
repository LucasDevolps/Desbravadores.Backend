using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Testes de segurança de /api/Eventos que reproduzem o comportamento real da aplicação (SQL Server real):
// injeção, overposting, manipulação de IDs, spoofing de IP, idempotência sob concorrência e restrição no banco,
// vazamento em logs/erros e concorrência entre eventos e lançamentos.
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosSecurityTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private async Task<List<Guid>> MembrosAsync(int n)
    {
        var lista = new List<Guid>();
        for (var i = 0; i < n; i++) lista.Add((await TestHelpers.AddUsuarioAsync(Factory, $"Seg {Guid.NewGuid():N}", "DS")).Id);
        return lista;
    }

    private async Task<EventoDto> CriarAsync(HttpClient client, object? membros, string? chave = null, string local = "Parque Ibirapuera")
    {
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membros, local: local), chave);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await EventosTestKit.LerAsync(response);
    }

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> f) => TestHelpers.WithDbAsync(Factory, f);

    private async Task<SqlConnection> ConexaoAsync(SqlServerApiFactory? factory = null)
    {
        var c = new SqlConnection((factory ?? Factory).ConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task<int> ExecAsync(SqlConnection c, string sql, params (string, object)[] p)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> EscalarAsync<T>(SqlConnection c, string sql, params (string, object)[] p)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)r;
    }

    // Contador acumulado de deadlocks da instância (lido pelo harness, que tem VIEW SERVER STATE).
    private static async Task<long> DeadlocksAcumuladosAsync()
    {
        await using var h = await SqlIdentityEnvironment.OpenHarnessAsync();
        await using var cmd = h.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters WHERE counter_name LIKE N'Number of Deadlocks%' AND instance_name = N'_Total'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<int> TabelasAsync()
    {
        await using var c = await ConexaoAsync();
        return await EscalarAsync<int>(c, "SELECT COUNT(*) FROM sys.tables");
    }

    // ------------------------------------------------------------------ 1. SQL injection

    [Fact]
    public async Task SqlInjection_PayloadsEmLocalMotivoChaveEQuery_SaoGravadosComoTextoOuRecusados_SemAfetarOBanco()
    {
        var tabelas = await TabelasAsync();
        var lancamentosAntes = await Db(db => db.Lancamentos.CountAsync());
        var m = await MembrosAsync(2);
        const string local = "x'); DROP TABLE dbo.eventos; --";
        const string motivo = "'); UPDATE dbo.Lancamentos SET Valor = 0; DELETE FROM dbo.Usuarios; --";

        // texto malicioso em "local": gravado literalmente (parâmetro), nada é executado
        var dto = await CriarAsync(Client, m, local: local);
        Assert.Equal(local, dto.Local);
        Assert.Equal(local, await Db(db => db.Eventos.Where(e => e.Id == dto.Id).Select(e => e.Local).SingleAsync()));

        // PUT e DELETE: "motivo" com SQL vira dado do SESSION_CONTEXT/auditoria, jamais SQL
        var put = await EventosTestKit.PutAsync(Client, dto.Id, EventosTestKit.Corpo(new[] { m[0] }, dto.DataEvento, local, versao: dto.Versao, motivo: motivo));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var atual = await EventosTestKit.LerAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, motivo, atual.Versao)).StatusCode);
        Assert.Equal(motivo, await Db(db => db.HistoricoEventos.Where(h => h.EventoId == dto.Id).Select(h => h.Motivo).SingleAsync()));
        Assert.Equal(motivo, await Db(db => db.LancamentosDeletados.Where(d => d.EventoId == dto.Id).Select(d => d.Motivo).FirstAsync()));

        // Idempotency-Key, datas e IDs com SQL: recusados na validação/binding (400/404), nunca 500
        var chave = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0]), "1'; DROP TABLE dbo.eventos;--");
        Assert.Equal(HttpStatusCode.BadRequest, chave.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/Eventos?dataInicial=2026-09-01';DROP TABLE dbo.eventos;--&dataFinal=2026-09-30")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/Eventos/1;DROP TABLE dbo.eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PostJsonBrutoAsync(Client, """{"membros":"1'; DROP TABLE dbo.eventos;--"}""")).StatusCode);
        var busca = await Client.GetAsync("/api/Lancamentos?search=' OR 1=1; DROP TABLE dbo.Lancamentos;--&status=x'&finalidade=';--");
        Assert.NotEqual(HttpStatusCode.InternalServerError, busca.StatusCode);

        Assert.Equal(tabelas, await TabelasAsync());
        Assert.True(await Db(db => db.Eventos.AnyAsync(e => e.Id == dto.Id)));
        Assert.True(await Db(db => db.Usuarios.CountAsync(u => m.Contains(u.Id))) == 2);
        Assert.Equal(lancamentosAntes + 2, await Db(db => db.Lancamentos.CountAsync()));   // só os 2 criados aqui
        Assert.DoesNotContain(await Db(db => db.Lancamentos.Where(l => l.EventoId == dto.Id).ToListAsync()), l => l.Valor == 0);
    }

    // ------------------------------------------------------------------ 2/3/4. autorização, overposting, manipulação de IDs

    [Fact]
    public async Task Overposting_Post_CamposSensiveisNoJsonSaoIgnorados()
    {
        var falso = Guid.NewGuid();
        var m = (await MembrosAsync(1))[0];
        var corpo = EventosTestKit.Corpo(m);
        foreach (var (k, v) in new (string, object?)[]
        {
            ("id", falso), ("usuarioSolicitanteId", falso), ("usuarioResponsavelId", falso), ("criadoPorUsuarioId", falso),
            ("ipResponsavel", "1.2.3.4"), ("ativo", false), ("status", "Pago"), ("total", 999999m), ("valorPorMembro", 1m),
            ("idempotencyKey", "forjada"), ("eventoGrupoId", falso), ("versao", "AAAAAAAAB9E="), ("categoria", "Clube"),
        }) corpo[k] = v;

        var chave = Guid.NewGuid().ToString();
        var response = await EventosTestKit.PostAsync(Client, corpo, chave);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await EventosTestKit.LerAsync(response);

        Assert.NotEqual(falso, dto.Id);
        Assert.NotEqual(falso, dto.EventoGrupoId);
        Assert.True(dto.Ativo);
        Assert.Equal((20m, 20m), (dto.ValorPorMembro, dto.Total));
        var linha = await Db(db => db.Eventos.AsNoTracking().SingleAsync(e => e.Id == dto.Id));
        Assert.Equal(fx.AdminId, linha.CriadoPorUsuarioId);
        var op = await Db(db => db.EventosOperacoes.AsNoTracking().SingleAsync(o => o.EventoId == dto.Id));
        Assert.Equal((fx.AdminId, chave), (op.UsuarioId, op.IdempotencyKey));   // a chave é a do header, não a do corpo
        var lanc = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.EventoId == dto.Id));
        Assert.Equal((StatusLancamento.Pendente, CategoriaLancamento.Evento, true, 20m), (lanc.Status, lanc.Categoria, lanc.Ativo, lanc.Valor));
    }

    [Fact]
    public async Task Overposting_PutEDelete_ResponsavelEIpSaoDoServidor_ECamposProtegidosNaoMudam()
    {
        var m = await MembrosAsync(2);
        var alvo = await CriarAsync(Client, m);
        var outro = await CriarAsync(Client, (await MembrosAsync(1))[0]);
        var forjado = Guid.NewGuid();

        // PUT com id/grupo/responsável/ip/ativo/status/total forjados, e o id de OUTRO evento no corpo
        var corpo = EventosTestKit.Corpo(new[] { m[0] }, alvo.DataEvento, alvo.Local, 12m, versao: alvo.Versao, motivo: "remoção");
        foreach (var (k, v) in new (string, object?)[]
        {
            ("id", outro.Id), ("eventoGrupoId", outro.EventoGrupoId), ("eventoReferenciaId", outro.Id), ("usuarioResponsavelId", forjado),
            ("ipResponsavel", "6.6.6.6"), ("ativo", false), ("status", "Pago"), ("total", 1m), ("valorPorMembro", 1m),
        }) corpo[k] = v;
        var put = await EventosTestKit.PutAsync(Client, alvo.Id, corpo);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var atual = await EventosTestKit.LerAsync(put);

        Assert.Equal((alvo.Id, alvo.EventoGrupoId, (Guid?)null, true), (atual.Id, atual.EventoGrupoId, atual.EventoReferenciaId, atual.Ativo));
        Assert.Equal(22m, atual.ValorPorMembro);                             // recalculado no servidor
        Assert.Equal(outro.Versao, (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{outro.Id}"))!.Versao);   // o outro evento não foi tocado
        var removido = await Db(db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.EventoId == alvo.Id));
        Assert.Equal(fx.AdminId, removido.UsuarioResponsavelId);
        Assert.NotEqual("6.6.6.6", removido.IpResponsavel);
        Assert.Equal(fx.AdminId, await Db(db => db.Lancamentos.Where(l => l.EventoId == alvo.Id && l.Ativo).Select(l => l.AtualizadoPorUsuarioId!.Value).FirstAsync()));

        // DELETE com responsável e IP forjados no corpo
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Eventos/{alvo.Id}")
        {
            Content = JsonContent.Create(new { motivo = "forja", versao = atual.Versao, usuarioResponsavelId = forjado, ipResponsavel = "6.6.6.6", id = outro.Id }),
        };
        delete.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, "198.51.100.10");
        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(delete)).StatusCode);
        var h = await Db(db => db.HistoricoEventos.AsNoTracking().SingleAsync(x => x.EventoId == alvo.Id));
        Assert.Equal((fx.AdminId, "198.51.100.10"), (h.UsuarioResponsavelId, h.IpResponsavel));
        Assert.True((await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{outro.Id}"))!.Ativo);   // id do corpo não redireciona a exclusão
    }

    [Fact]
    public async Task ManipulacaoDeIds_RotaManda_IdsAleatoriosOuDeOutroEventoNaoAfetamNada()
    {
        var a = await CriarAsync(Client, (await MembrosAsync(1))[0]);
        var b = await CriarAsync(Client, (await MembrosAsync(1))[0]);

        // versão do A aplicada à rota do B: 409 (rowversion não coincide), e nenhum dos dois é alterado
        var cruzado = await EventosTestKit.PutAsync(Client, b.Id, EventosTestKit.Corpo(b.Membros, b.DataEvento, b.Local, 99m, versao: a.Versao));
        Assert.True(cruzado.StatusCode is HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.DeleteAsync(Client, b.Id, "cruzado", a.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.DeleteAsync(Client, Guid.NewGuid(), "x", a.Versao)).StatusCode);
        Assert.Equal((a.Versao, b.Versao), ((await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{a.Id}"))!.Versao, (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{b.Id}"))!.Versao));

        // lançamento de outro evento/qualquer: MembroId/EventoId no corpo do PUT de lançamentos são ignorados
        var lancB = b.Lancamentos[0].Id;
        var forjar = await Client.PutAsJsonAsync($"/api/Lancamentos/{lancB}", new { status = "Pago", membroId = a.Membros[0], eventoId = a.Id });
        Assert.Equal(HttpStatusCode.OK, forjar.StatusCode);
        var lanc = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == lancB));
        Assert.Equal((b.Membros[0], b.Id, StatusLancamento.Pago), (lanc.MembroId!.Value, lanc.EventoId!.Value, lanc.Status));
    }

    [Fact]
    public async Task Autorizacao_SemTokenOuComCargoSemPermissao_NuncaTocaOsDados()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(Client, m);

        using var anonimo = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EventosTestKit.PostAsync(anonimo, EventosTestKit.Corpo(m))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EventosTestKit.PutAsync(anonimo, dto.Id, EventosTestKit.Corpo(m, versao: dto.Versao))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EventosTestKit.DeleteAsync(anonimo, dto.Id, "x", dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/Eventos/{dto.Id}")).StatusCode);

        var (ds, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        Assert.Equal(HttpStatusCode.Forbidden, (await ds.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ds.GetAsync($"/api/Eventos/{dto.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EventosTestKit.PostAsync(ds, EventosTestKit.Corpo(m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EventosTestKit.PutAsync(ds, dto.Id, EventosTestKit.Corpo(m, versao: dto.Versao))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EventosTestKit.DeleteAsync(ds, dto.Id, "x", dto.Versao)).StatusCode);

        var depois = await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{dto.Id}");
        Assert.Equal((dto.Versao, true), (depois!.Versao, depois.Ativo));
        // métodos que não existem no recurso: nada de PATCH nem de DELETE em coleção
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Client.PatchAsync($"/api/Eventos/{dto.Id}", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Client.DeleteAsync("/api/Eventos")).StatusCode);
    }

    // ------------------------------------------------------------------ 5. IP de auditoria / proxy

    private static async Task<string> IpAuditadoAsync(SqlServerApiFactory factory, HttpClient client, string remoteIp, string? xForwardedFor)
    {
        var membro = (await TestHelpers.AddUsuarioAsync(factory, "IP", "DS")).Id;
        var dto = await EventosTestKit.LerAsync(await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membro)));
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Eventos/{dto.Id}")
        {
            Content = JsonContent.Create(new { motivo = "ip", versao = dto.Versao }),
        };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, remoteIp);
        if (xForwardedFor is not null) request.Headers.Add("X-Forwarded-For", xForwardedFor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        return await TestHelpers.WithDbAsync(factory, db => db.HistoricoEventos.Where(h => h.EventoId == dto.Id).Select(h => h.IpResponsavel).SingleAsync());
    }

    [Fact]
    public async Task IpDeAuditoria_SemProxyConfiavel_XForwardedForForjadoNuncaEhAuditado()
    {
        // padrão (fora do Docker/IIS): ForwardedHeaders desligado; o header do cliente é ignorado
        var ip = await IpAuditadoAsync(Factory, Client, "198.51.100.10", "127.0.0.1");
        Assert.Equal("198.51.100.10", ip);
    }

    [Fact]
    public async Task IpDeAuditoria_ComProxyConfiavel_SoAceitaOHeaderVindoDaRedeDoProxy_ELimitaAoUltimoSalto()
    {
        using var factory = new SqlServerApiFactory(new Dictionary<string, string?> { ["ReverseProxy:TrustedNetworkCidr"] = "172.30.0.0/16" });
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        // cliente externo direto (fora da rede do proxy) forjando 127.0.0.1: o header é ignorado
        Assert.Equal("203.0.113.5", await IpAuditadoAsync(factory, client, "203.0.113.5", "127.0.0.1"));
        // nginx legítimo (dentro da rede confiável) repassando o IP real do cliente
        Assert.Equal("198.51.100.77", await IpAuditadoAsync(factory, client, "172.30.0.2", "198.51.100.77"));
        // cliente tentou prefixar um IP forjado: vale só o valor mais à direita (o que o nginx observou)
        Assert.Equal("198.51.100.78", await IpAuditadoAsync(factory, client, "172.30.0.2", "127.0.0.1, 198.51.100.78"));
    }

    // ------------------------------------------------------------------ 6. idempotência: escopo, concorrência e restrição no banco

    [Fact]
    public async Task Idempotencia_UsuarioAeB_MesmaChave_ConcorrentementeCadaUmCriaExatamenteUmEvento()
    {
        var (b, bId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "TES");
        var m = await MembrosAsync(2);
        var chave = Guid.NewGuid().ToString();

        // A×3 e B×3 ao mesmo tempo, com a MESMA chave: uma criação por usuário, o resto é replay 200 do próprio usuário
        var respostasA = Enumerable.Range(0, 3).Select(_ => EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0]), chave)).ToList();
        var respostasB = Enumerable.Range(0, 3).Select(_ => EventosTestKit.PostAsync(b, EventosTestKit.Corpo(m[1]), chave)).ToList();
        var a = await Task.WhenAll(respostasA);
        var bb = await Task.WhenAll(respostasB);

        Assert.Equal(1, a.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, bb.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(a.Concat(bb), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        var idsA = (await Task.WhenAll(a.Select(EventosTestKit.LerAsync))).Select(e => e.Id).Distinct().ToList();
        var idsB = (await Task.WhenAll(bb.Select(EventosTestKit.LerAsync))).Select(e => e.Id).Distinct().ToList();
        Assert.Single(idsA);
        Assert.Single(idsB);
        Assert.NotEqual(idsA[0], idsB[0]);

        // nenhum usuário enxerga a resposta do outro: B (mesma chave, membro de A) NÃO recebe o evento de A
        Assert.Equal(2, await Db(db => db.EventosOperacoes.CountAsync(o => o.IdempotencyKey == chave)));
        var doA = await Db(db => db.EventosOperacoes.SingleAsync(o => o.IdempotencyKey == chave && o.UsuarioId == fx.AdminId));
        var doB = await Db(db => db.EventosOperacoes.SingleAsync(o => o.IdempotencyKey == chave && o.UsuarioId == bId));
        Assert.Equal((idsA[0], idsB[0]), (doA.EventoId, doB.EventoId));
        // e o mesmo membro de A enviado por B com a mesma chave é a operação de B (não é conflito 409 nem replay do A)
        var trocado = await EventosTestKit.PostAsync(b, EventosTestKit.Corpo(m[0]), chave);
        Assert.Equal(HttpStatusCode.Conflict, trocado.StatusCode);   // dados diferentes da operação DE B (m[1]) com a chave de B
    }

    [Fact]
    public async Task Idempotencia_UnicidadeEGarantidaPeloBanco_NaoSoPelaAplicacao()
    {
        var m = (await MembrosAsync(1))[0];
        var chave = Guid.NewGuid().ToString();
        var dto = await CriarAsync(Client, m, chave);
        await using var c = await ConexaoAsync();

        // INSERT direto da mesma (UsuarioId, chave): o índice único recusa, sem passar por nenhum SELECT prévio
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(c,
            "INSERT INTO dbo.eventos_operacoes (Id, UsuarioId, IdempotencyKey, RequestHash, EventoId, CriadoEmUtc) VALUES (NEWID(), @u, @k, N'x', @e, SYSUTCDATETIME())",
            ("@u", fx.AdminId), ("@k", chave), ("@e", dto.Id)));
        Assert.Equal(2601, ex.Number);
        // outro usuário com a mesma chave é permitido pelo banco
        Assert.Equal(1, await ExecAsync(c,
            "INSERT INTO dbo.eventos_operacoes (Id, UsuarioId, IdempotencyKey, RequestHash, EventoId, CriadoEmUtc) SELECT TOP 1 NEWID(), Id, @k, N'x', @e, SYSUTCDATETIME() FROM dbo.Usuarios WHERE Id <> @u",
            ("@u", fx.AdminId), ("@k", chave), ("@e", dto.Id)));
    }

    // ------------------------------------------------------------------ 8. vazamento em logs e erros

    [Fact]
    public async Task Logs_NaoContemTokenSenhaChaveJwtNemConnectionString_EmOperacoesDeEventos()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(Client, m);
        await EventosTestKit.PutAsync(Client, dto.Id, EventosTestKit.Corpo(m, dto.DataEvento, dto.Local, 30m, versao: "AAAAAAAAB9E="));   // 409 (recusa logada)
        var atual = await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{dto.Id}");
        await EventosTestKit.DeleteAsync(Client, dto.Id, "log", atual!.Versao);
        await EventosTestKit.DeleteAsync(Client, dto.Id, "log", atual.Versao);   // 404 (recusa logada)

        var token = Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var senhaSql = new SqlConnectionStringBuilder(Factory.ConnectionString).Password;
        var logs = string.Join('\n', Factory.Logs.Entries);
        Assert.Contains("Operação recusada", logs);                        // eventos de recusa existem no log estruturado
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain(AlmiranteApiFactory.AdminSenha, logs);
        Assert.DoesNotContain(Factory.JwtKeyBase64, logs);
        Assert.DoesNotContain(senhaSql, logs);
        Assert.DoesNotContain("Password=", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(m[0].ToString() + "\"", logs);               // nenhum corpo de requisição em log
    }

    [Fact]
    public async Task Erro500_EmProducao_NaoDevolveStackTraceSqlNemDetalhesInternos()
    {
        // InMemory + evento => o serviço lança NotSupportedException (falha inesperada real no pipeline de produção)
        using var factory = new ConfiguredApiFactory("Production");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(Guid.NewGuid()));
        var corpo = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        foreach (var proibido in new[] { "NotSupportedException", "Eventos exigem SQL Server", "Almirante.Api.Services", " at ", "StackTrace",
            "SELECT", "Server=", "Password", AlmiranteApiFactory.AdminSenha, factory.JwtKeyBase64 })
            Assert.DoesNotContain(proibido, corpo);
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    // ------------------------------------------------------------------ 7. desserialização: nunca 500

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("{\"membros\":[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]}")]
    [InlineData("{\"membros\":[{\"a\":1}]}")]
    [InlineData("{\"membros\":[\"11111111-1111-4111-8111-111111111111\",1]}")]
    [InlineData("{\"membros\":[true]}")]
    [InlineData("{\"membros\":1e999}")]
    [InlineData("{\"membros\":\"11111111-1111-4111-8111-11111111111Z\"}")]
    [InlineData("{\"dataEvento\":\"2026-02-30\",\"membros\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("{\"seguroObrigatorio\":\"abc\",\"membros\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("{\"transporte\":[],\"membros\":\"11111111-1111-4111-8111-111111111111\"}")]
    public async Task Deserializacao_Malformada_Retorna400_NuncaErro500(string json)
    {
        var response = await EventosTestKit.PostJsonBrutoAsync(Client, json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        foreach (var proibido in new[] { "Exception", "System.", "Almirante.", "StackTrace", "   at " })
            Assert.DoesNotContain(proibido, corpo);
    }

    [Fact]
    public async Task Deserializacao_ContentTypeErradoOuSemCorpo_NaoGera500()
    {
        var texto = new HttpRequestMessage(HttpMethod.Post, "/api/Eventos") { Content = new StringContent("membros=1", System.Text.Encoding.UTF8, "text/plain") };
        texto.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Client.SendAsync(texto)).StatusCode);

        var vazio = new HttpRequestMessage(HttpMethod.Post, "/api/Eventos");
        vazio.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Contains((await Client.SendAsync(vazio)).StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType });
    }

    // ------------------------------------------------------------------ 9/10. lançamentos vinculados e concorrência (estresse)

    [Fact]
    public async Task Concorrencia_EstresseMisto_PagarEditarExcluirEmVariosEventos_SemDeadlockNemEstadoIncoerente()
    {
        const int eventos = 16;
        var deadlocksAntes = await DeadlocksAcumuladosAsync();
        var dtos = new List<EventoDto>();
        for (var i = 0; i < eventos; i++) dtos.Add(await CriarAsync(Client, await MembrosAsync(2)));

        var tarefas = new List<Task<(int Evento, string Op, HttpStatusCode Status)>>();
        for (var i = 0; i < eventos; i++)
        {
            var (e, idx) = (dtos[i], i);
            tarefas.Add(Task.Run(async () => (idx, "pagar", (await Client.PutAsJsonAsync($"/api/Lancamentos/{e.Lancamentos[0].Id}", new { status = "Pago" })).StatusCode)));
            tarefas.Add(Task.Run(async () => (idx, "editar", (await EventosTestKit.PutAsync(Client, e.Id,
                EventosTestKit.Corpo(e.Membros, e.DataEvento, e.Local, 50m, versao: e.Versao))).StatusCode)));
            if (i % 2 == 0)
                tarefas.Add(Task.Run(async () => (idx, "excluir", (await EventosTestKit.DeleteAsync(Client, e.Id, "estresse", e.Versao)).StatusCode)));
            else
                tarefas.Add(Task.Run(async () => (idx, "atrasar", (await Client.PutAsJsonAsync($"/api/Lancamentos/{e.Lancamentos[1].Id}", new { status = "Atrasado" })).StatusCode)));
        }

        var resultados = await Task.WhenAll(tarefas);
        Assert.DoesNotContain(resultados, r => r.Status == HttpStatusCode.InternalServerError);   // deadlock/exceção inesperada = 500
        Assert.Equal(deadlocksAntes, await DeadlocksAcumuladosAsync());   // contador do próprio SQL Server: zero deadlocks, inclusive os já retentados

        foreach (var e in dtos)
        {
            var evento = await Db(db => db.Eventos.AsNoTracking().SingleAsync(x => x.Id == e.Id));
            var lancs = await Db(db => db.Lancamentos.AsNoTracking().Where(l => l.EventoId == e.Id).ToListAsync());
            var ativos = lancs.Where(l => l.Ativo).ToList();
            var historicos = await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == e.Id));

            if (!evento.Ativo)
            {
                Assert.Empty(ativos);                                              // excluído: nada ativo sobrou
                Assert.All(lancs, l => Assert.Equal(StatusLancamento.Pendente, l.Status));   // e nenhum pagamento foi engolido
                Assert.Equal(1, historicos);
            }
            else
            {
                Assert.Equal(2, ativos.Count);
                Assert.All(ativos, l => Assert.Equal(evento.ValorPorMembro, l.Valor));   // total/valor coerentes com o cadastro
                Assert.Equal(evento.ValorPorMembro * 2, ativos.Sum(l => l.Valor));
                Assert.Equal(0, historicos);
                // se o edit ganhou, o valor é o novo; se perdeu (409), continua o original: nunca uma mistura
                Assert.Contains(evento.ValorPorMembro, new[] { 20m, 60m });
            }

            // pagamento e exclusão nunca convivem: exclusão só passa sem Pago/Atrasado
            var pagos = lancs.Count(l => l.Status != StatusLancamento.Pendente);
            if (!evento.Ativo) Assert.Equal(0, pagos);
        }
    }

    [Fact]
    public async Task Lancamentos_Evento_NaoHaCaminhoParaContornarOBloqueio()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(Client, m);
        var id = dto.Lancamentos[0].Id;

        // valor igual ao atual + status é permitido; qualquer mudança real de campo financeiro é 409, em campos isolados ou combinados
        foreach (var corpo in new object[]
        {
            new { valor = 21m }, new { valor = 0.01m, status = "Pago" }, new { vencimento = dto.DataEvento.AddDays(3).ToString("yyyy-MM-dd") },
            new { categoria = "Clube", status = "Atrasado" }, new { finalidade = "Doação" }, new { tipoFluxo = "Saida" },
        })
            Assert.Equal(HttpStatusCode.Conflict, (await Client.PutAsJsonAsync($"/api/Lancamentos/{id}", corpo)).StatusCode);

        // nenhuma das tentativas mudou o status (a validação é atômica: 409 antes de qualquer gravação)
        var lanc = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id));
        Assert.Equal((StatusLancamento.Pendente, 20m, dto.DataEvento, CategoriaLancamento.Evento), (lanc.Status, lanc.Valor, lanc.Vencimento, lanc.Categoria));

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}") { Content = JsonContent.Create(new { motivo = "contorno" }) };
        Assert.Equal(HttpStatusCode.Conflict, (await Client.SendAsync(delete)).StatusCode);
        // marcado como Pago pelo fluxo permitido, continua bloqueado para exclusão isolada
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status = "Pago" })).StatusCode);
        var delete2 = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}") { Content = JsonContent.Create(new { motivo = "contorno" }) };
        Assert.Equal(HttpStatusCode.Conflict, (await Client.SendAsync(delete2)).StatusCode);
        Assert.Equal(0, await Db(db => db.LancamentosDeletados.CountAsync(d => d.LancamentoId == id)));
        // o registro geral "para todos os membros" nunca cria nem altera lançamentos de evento
        var eventoAntes = await Db(db => db.Lancamentos.CountAsync(l => l.EventoId == dto.Id));
        var geral = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(new
            {
                finalidade = "Mensalidade", descricao = "geral", categoria = "Clube", tipoFluxo = "Entrada", valor = 5m,
                vencimento = EventosTestKit.Hoje.AddDays(4).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = true,
            }),
        };
        geral.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(geral)).StatusCode);
        Assert.Equal(eventoAntes, await Db(db => db.Lancamentos.CountAsync(l => l.EventoId == dto.Id)));
    }
}
