using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Escrita em /api/Usuarios contra SQL Server REAL: índice único de e-mail, trigger TR_Usuarios_Historico,
// SESSION_CONTEXT, transação e exclusão lógica. O InMemory não comprova nenhum desses comportamentos.
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.25";

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    // ------------------------------------------------------------------ apoio

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Guid> CargoIdAsync(string role) => Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    private static string NovoEmail(string prefixo = "usuario") => $"{prefixo}-{Guid.NewGuid():N}@local.dev";

    private static string SenhaValida() => $"Forte-{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> PostAsync(object corpo) => await Client.PostAsJsonAsync("/api/Usuarios", corpo);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object corpo, string ip = IpTeste)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, ip);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id, string ip = IpTeste)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{id}");
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, ip);
        return client.SendAsync(request);
    }

    private async Task<Usuario> MembroAsync(string role = "DS") =>
        await TestHelpers.AddUsuarioAsync(Factory, $"Membro {Guid.NewGuid():N}", role);

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid usuarioId) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == usuarioId).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().Include(u => u.Cargo).SingleAsync(u => u.Id == id));

    private static async Task<JsonObject> ProblemaAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private async Task<EventoDto> EventoComMembroAsync(Guid membroId, DateOnly data, string local)
    {
        var response = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(membroId, data, local));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(response);
    }

    // A API só aceita cadastrar eventos a partir do primeiro dia do mês corrente; um evento passado é produzido
    // cadastrando-o normalmente e movendo a data direto no banco (o trigger de eventos só age em Ativo 1 -> 0).
    private async Task MoverEventoParaAsync(Guid eventoId, DateOnly data)
    {
        await using var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "UPDATE dbo.eventos SET DataEvento = @d WHERE Id = @id";
        comando.Parameters.AddWithValue("@d", data.ToDateTime(TimeOnly.MinValue));
        comando.Parameters.AddWithValue("@id", eventoId);
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    private async Task<int> ExecSqlAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ POST: inclusão e e-mail único

    [Fact]
    public async Task Post_EmailAindaNaoExiste_CriaUsuarioAtivoQueConsegueAutenticar()
    {
        var email = NovoEmail("Novo.Membro");
        var senha = SenhaValida();
        var response = await PostAsync(new { nome = "  Novo Membro  ", email = $"  {email}  ", senha, cargoId = await CargoIdAsync("SEC") });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal("Novo Membro", dto.Nome);
        Assert.Equal(email, dto.Email);
        Assert.Equal("Secretário", dto.Funcao);
        Assert.True(dto.Ativo);

        var salvo = await UsuarioAsync(dto.Id);
        Assert.Equal(email.ToUpperInvariant(), salvo.EmailNormalizado);
        Assert.NotEqual(senha, salvo.SenhaHash);
        Assert.Empty(await HistoricoAsync(dto.Id)); // inclusão não é alteração: nada no histórico

        using var novo = Factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(novo, email, senha)).StatusCode);
    }

    [Fact]
    public async Task Post_EmailJaExistente_Retorna409ComMensagemDeNegocio_ENaoCria()
    {
        var existente = await MembroAsync();
        var antes = await Db(db => db.Usuarios.CountAsync());

        var response = await PostAsync(new { nome = "Duplicado", email = existente.Email, senha = SenhaValida(), cargoId = await CargoIdAsync("DS") });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problema = await ProblemaAsync(response);
        Assert.Equal("Já existe um usuário cadastrado com o e-mail informado.", (string)problema["detail"]!);
        Assert.Equal(antes, await Db(db => db.Usuarios.CountAsync()));
    }

    [Fact]
    public async Task Post_EmailDiferenteApenasEmMaiusculasMinusculas_Retorna409()
    {
        var existente = await MembroAsync();

        var response = await PostAsync(new { nome = "Caixa Alta", email = existente.Email.ToUpperInvariant(), senha = SenhaValida(), cargoId = await CargoIdAsync("DS") });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await Db(db => db.Usuarios.CountAsync(u => u.EmailNormalizado == existente.Email.ToUpperInvariant())));
    }

    [Fact]
    public async Task Post_RequisicoesConcorrentesComMesmoEmail_CriaUmUnico_EAsDemaisRecebem409SemErroSql()
    {
        var email = NovoEmail("corrida");
        var cargoId = await CargoIdAsync("DS");

        // Variações de caixa: todas normalizam para o mesmo valor e disputam o mesmo índice único.
        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PostAsync(new
        {
            nome = $"Corrida {i}", email = i % 2 == 0 ? email : email.ToUpperInvariant(), senha = SenhaValida(), cargoId,
        })));

        Assert.Single(respostas, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var resposta in respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.Contains("Já existe um usuário cadastrado com o e-mail informado.", corpo);
            Assert.DoesNotContain("IX_Usuarios_EmailNormalizado", corpo);
            Assert.DoesNotContain("SqlException", corpo);
        }

        Assert.Equal(1, await Db(db => db.Usuarios.CountAsync(u => u.EmailNormalizado == email.ToUpperInvariant())));
    }

    [Fact]
    public async Task Banco_IndiceUnicoRecusaEmailNormalizadoDuplicado_MesmoSemPassarPelaAplicacao()
    {
        var existente = await MembroAsync();
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecSqlAsync("""
            INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas)
            VALUES (NEWID(), N'Direto', @email, @normalizado, N'x', @cargo, SYSUTCDATETIME(), 0, 0)
            """, ("@email", existente.Email.ToUpperInvariant()), ("@normalizado", existente.EmailNormalizado), ("@cargo", existente.CargoId)));
        Assert.Contains(ex.Number, new[] { 2601, 2627 });
    }

    // ------------------------------------------------------------------ PUT: alteração e histórico

    [Fact]
    public async Task Put_DadosValidos_AlteraUsuario()
    {
        var alvo = await MembroAsync();
        var novoEmail = NovoEmail("alterado");

        var response = await PutAsync(Client, alvo.Id, new { nome = "Nome Alterado", email = novoEmail, cargoId = await CargoIdAsync("TES") });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal("Nome Alterado", dto.Nome);
        Assert.Equal("Tesoureiro", dto.Funcao);

        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(novoEmail, salvo.Email);
        Assert.Equal(novoEmail.ToUpperInvariant(), salvo.EmailNormalizado);
        Assert.Equal("TES", salvo.Cargo!.Role);
        Assert.Equal(alvo.SenhaHash, salvo.SenhaHash); // PUT não mexe na senha
    }

    [Fact]
    public async Task Put_EmailDeOutroUsuario_Retorna409_ENaoAlteraNemGeraHistorico()
    {
        var alvo = await MembroAsync();
        var outro = await MembroAsync();

        var response = await PutAsync(Client, alvo.Id, new { nome = "Tentativa", email = outro.Email.ToUpperInvariant(), cargoId = alvo.CargoId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Existe outro usuário cadastrado com esse e-mail.", (string)(await ProblemaAsync(response))["detail"]!);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(alvo.Email, salvo.Email);
        Assert.Equal(alvo.Nome, salvo.Nome);
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Put_MantendoOProprioEmail_ComOutraCaixa_ConsideraOProprioUsuario()
    {
        var alvo = await MembroAsync();

        var response = await PutAsync(Client, alvo.Id, new { nome = alvo.Nome, email = alvo.Email.ToUpperInvariant(), cargoId = alvo.CargoId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(alvo.Email.ToUpperInvariant(), (await UsuarioAsync(alvo.Id)).Email);
    }

    [Fact]
    public async Task Put_SalvaEstadoAnteriorEmUsuariosHist_AntesDoUpdate()
    {
        var alvo = await MembroAsync("SEC");
        var emailAntigo = alvo.Email;

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, new { nome = "Primeira", email = NovoEmail("primeira"), cargoId = await CargoIdAsync("DIR") })).StatusCode);
        var emailIntermediario = (await UsuarioAsync(alvo.Id)).Email;
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, new { nome = "Segunda", email = NovoEmail("segunda"), cargoId = await CargoIdAsync("DIR") })).StatusCode);

        var historico = await HistoricoAsync(alvo.Id);
        Assert.Equal(2, historico.Count);

        // Cada linha guarda EXATAMENTE o estado anterior à respectiva alteração.
        Assert.Equal(alvo.Nome, historico[0].Nome);
        Assert.Equal(emailAntigo, historico[0].Email);
        Assert.Equal("Secretário", historico[0].CargoNome);
        Assert.Equal(alvo.CargoId, historico[0].CargoId);
        Assert.True(historico[0].Ativo);
        Assert.Equal(UsuarioHistorico.OperacaoUpdate, historico[0].TipoOperacao);

        Assert.Equal("Primeira", historico[1].Nome);
        Assert.Equal(emailIntermediario, historico[1].Email);
        Assert.Equal("Diretor", historico[1].CargoNome);

        Assert.Equal("Segunda", (await UsuarioAsync(alvo.Id)).Nome);
    }

    [Fact]
    public async Task Put_RegistraUsuarioResponsavelAutenticado_IpDaConexao_EDataUtc()
    {
        var (secretario, secretarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "SEC");
        var loginSecretario = (await UsuarioAsync(secretarioId)).Email;
        var alvo = await MembroAsync();
        var inicio = DateTime.UtcNow.AddSeconds(-2);

        // Campos de auditoria enviados no corpo são ignorados: responsável e IP vêm sempre do contexto da requisição.
        var response = await PutAsync(secretario, alvo.Id, new
        {
            nome = "Auditado", email = alvo.Email, cargoId = alvo.CargoId,
            usuarioResponsavelId = Guid.NewGuid(), usuarioAlteracao = "admin", ipResponsavel = "10.9.9.9",
        }, ip: "198.51.100.77");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(secretarioId, linha.UsuarioResponsavelId);
        Assert.Equal(loginSecretario, linha.UsuarioResponsavelLogin);
        Assert.Equal("198.51.100.77", linha.IpResponsavel);
        Assert.InRange(linha.AlteradoEmUtc, inicio, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(UsuarioHistorico.OperacaoUpdate, linha.TipoOperacao);
    }

    [Fact]
    public async Task Put_UsuarioInexistente_Retorna404_EUsuarioInativo_Retorna409()
    {
        var corpo = new { nome = "X", email = NovoEmail(), cargoId = await CargoIdAsync("DS") };
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(Client, Guid.NewGuid(), corpo)).StatusCode);

        var alvo = await MembroAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(Client, alvo.Id, corpo)).StatusCode);
        Assert.Single(await HistoricoAsync(alvo.Id)); // só a exclusão
    }

    // ------------------------------------------------------------------ DELETE: exclusão lógica

    [Fact]
    public async Task Delete_UsuarioSomenteComEventosPassados_ExcluiLogicamente()
    {
        var alvo = await MembroAsync();
        var evento = await EventoComMembroAsync(alvo.Id, EventosTestKit.Hoje.AddDays(5), "Acampamento Antigo");
        await MoverEventoParaAsync(evento.Id, EventosTestKit.Hoje.AddDays(-40));

        var response = await DeleteAsync(Client, alvo.Id);

        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        Assert.False((await UsuarioAsync(alvo.Id)).Ativo);
        // A participação no evento passado permanece intacta.
        Assert.True(await Db(db => db.EventosMembros.AnyAsync(m => m.MembroId == alvo.Id && m.EventoId == evento.Id && m.Ativo)));
    }

    [Fact]
    public async Task Delete_UsuarioComEventoFuturo_Retorna409_ENaoAlteraNada()
    {
        var alvo = await MembroAsync();
        await EventoComMembroAsync(alvo.Id, EventosTestKit.Hoje.AddDays(16), "Campori 2026");
        var versaoAntes = (await UsuarioAsync(alvo.Id)).SecurityVersion;

        var response = await DeleteAsync(Client, alvo.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problema = await ProblemaAsync(response);
        Assert.Equal("Não é possível excluir o usuário, pois ele está vinculado ao evento \"Campori 2026\" " +
            $"({EventosTestKit.Hoje.AddDays(16):dd/MM/yyyy}).", (string)problema["detail"]!);

        // Transação desfeita por inteiro: continua ativo, sessões preservadas e nenhuma linha de histórico.
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.True(salvo.Ativo);
        Assert.Equal(versaoAntes, salvo.SecurityVersion);
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Delete_InformaTodosOsEventosFuturosQueImpedem_InclusiveOEventoDeHoje()
    {
        var alvo = await MembroAsync();
        var hoje = EventosTestKit.Hoje;
        var passado = await EventoComMembroAsync(alvo.Id, hoje.AddDays(3), "Evento Passado");
        await MoverEventoParaAsync(passado.Id, hoje.AddDays(-1));
        var deHoje = await EventoComMembroAsync(alvo.Id, hoje, "Encontro de Hoje");
        var futuro = await EventoComMembroAsync(alvo.Id, hoje.AddDays(40), "Encontro Regional");

        var response = await DeleteAsync(Client, alvo.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problema = await ProblemaAsync(response);
        Assert.Equal("Usuário vinculado a eventos futuros.", (string)problema["title"]!);
        var eventos = problema["eventos"]!.AsArray();
        Assert.Equal(2, eventos.Count);
        Assert.Equal(deHoje.Id, Guid.Parse((string)eventos[0]!["id"]!));
        Assert.Equal("Encontro de Hoje", (string)eventos[0]!["nome"]!);
        Assert.Equal(hoje.ToString("yyyy-MM-dd"), (string)eventos[0]!["data"]!);
        Assert.Equal(futuro.Id, Guid.Parse((string)eventos[1]!["id"]!));
        Assert.Equal("Encontro Regional", (string)eventos[1]!["nome"]!);

        var detalhe = (string)problema["detail"]!;
        Assert.Contains("\"Encontro de Hoje\"", detalhe);
        Assert.Contains("\"Encontro Regional\"", detalhe);
        Assert.DoesNotContain("Evento Passado", detalhe);
    }

    [Fact]
    public async Task Delete_EventoFuturoExcluido_NaoImpedeMais()
    {
        var alvo = await MembroAsync();
        var evento = await EventoComMembroAsync(alvo.Id, EventosTestKit.Hoje.AddDays(20), "Cancelado");
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, evento.Id, "cancelado", evento.Versao)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id)).StatusCode);
    }

    [Fact]
    public async Task Delete_SalvaEstadoAnteriorEmUsuariosHist()
    {
        var alvo = await MembroAsync("TES");

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id)).StatusCode);

        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(UsuarioHistorico.OperacaoDelete, linha.TipoOperacao);
        Assert.True(linha.Ativo);                    // estado ANTERIOR: ainda ativo
        Assert.Equal(alvo.Nome, linha.Nome);
        Assert.Equal(alvo.Email, linha.Email);
        Assert.Equal("Tesoureiro", linha.CargoNome);
    }

    [Fact]
    public async Task Delete_RealizaSoftDelete_RegistroPermanece_SessoesELoginDeixamDeValer()
    {
        var (clienteDoAlvo, alvoId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DIR");
        Assert.Equal(HttpStatusCode.OK, (await clienteDoAlvo.GetAsync("/api/Usuarios")).StatusCode);
        var alvo = await UsuarioAsync(alvoId);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvoId)).StatusCode);

        var salvo = await UsuarioAsync(alvoId);          // nenhum DELETE físico
        Assert.False(salvo.Ativo);
        Assert.Equal(alvo.SecurityVersion + 1, salvo.SecurityVersion);

        Assert.Equal(HttpStatusCode.Unauthorized, (await clienteDoAlvo.GetAsync("/api/Usuarios")).StatusCode);
        using var novo = Factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(novo, alvo.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);

        var ativos = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!;
        Assert.DoesNotContain(ativos, u => u.Id == alvoId);
        var todos = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios?includeInactive=true"))!;
        Assert.Contains(todos, u => u.Id == alvoId && !u.Ativo);

        // Usuário inativo não pode ser incluído como participante de um novo evento.
        var evento = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(alvoId));
        Assert.Equal(HttpStatusCode.BadRequest, evento.StatusCode);
    }

    [Fact]
    public async Task Delete_RegistraUsuarioResponsavelAutenticado_IpDaConexao_EDataUtc()
    {
        var alvo = await MembroAsync();
        var inicio = DateTime.UtcNow.AddSeconds(-2);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id, ip: "2001:db8::15")).StatusCode);

        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(fx.AdminId, linha.UsuarioResponsavelId);
        Assert.Equal(AlmiranteApiFactory.AdminEmail, linha.UsuarioResponsavelLogin);
        Assert.Equal("2001:db8::15", linha.IpResponsavel);
        Assert.InRange(linha.AlteradoEmUtc, inicio, DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task Delete_UsuarioJaInativo_Retorna409_SemNovoHistorico()
    {
        var alvo = await MembroAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id)).StatusCode);

        var segunda = await DeleteAsync(Client, alvo.Id);

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("Usuário já está inativo.", (string)(await ProblemaAsync(segunda))["title"]!);
        Assert.Single(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Delete_Inexistente404_ProprioUsuario409_AdministradorPorOutroPapel403()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(Client, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(Client, fx.AdminId)).StatusCode);

        var (secretario, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "SEC");
        var administrador = await MembroAsync("ADM");
        var negado = await DeleteAsync(secretario, administrador.Id);
        Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        Assert.True((await UsuarioAsync(administrador.Id)).Ativo);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutAsync(secretario, administrador.Id, new { nome = "X", email = administrador.Email, cargoId = administrador.CargoId })).StatusCode);
    }

    // ------------------------------------------------------------------ trigger: garantias no banco

    [Fact]
    public async Task Trigger_AlteracaoCadastralSemContextoDeAuditoria_EhRecusada_EAlteracaoDeLoginNaoGeraHistorico()
    {
        var alvo = await MembroAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecSqlAsync("UPDATE dbo.Usuarios SET Nome = N'Sem contexto' WHERE Id = @id", ("@id", alvo.Id)));
        Assert.Equal(50021, ex.Number);
        Assert.Equal(alvo.Nome, (await UsuarioAsync(alvo.Id)).Nome);

        // Colunas de login/bloqueio/senha não são cadastrais: passam sem contexto e sem histórico.
        Assert.Equal(1, await ExecSqlAsync("UPDATE dbo.Usuarios SET FalhasLoginConsecutivas = 1, SecurityVersion = SecurityVersion + 1 WHERE Id = @id", ("@id", alvo.Id)));
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }
}
