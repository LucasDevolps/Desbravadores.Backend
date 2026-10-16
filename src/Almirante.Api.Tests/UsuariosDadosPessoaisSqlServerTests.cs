using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// CPF, data de nascimento e telefone em /api/Usuarios contra SQL Server REAL: índice único filtrado UX_Usuarios_Cpf,
// tipos das colunas, trigger TR_Usuarios_Historico com o estado anterior completo e atomicidade alteração + histórico.
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosDadosPessoaisSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.40";

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    // ------------------------------------------------------------------ apoio

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Guid> CargoIdAsync(string role) => Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    private static string NovoEmail(string prefixo = "pessoal") => $"{prefixo}-{Guid.NewGuid():N}@local.dev";

    // 11 dígitos começando por zero: prova que o CPF é texto (zero à esquerda preservado).
    private static string NovoCpf() => "0" + Random.Shared.NextInt64(0, 10_000_000_000).ToString("D10");

    private static string Mascarar(string cpf) => $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";

    private async Task<HttpResponseMessage> PostAsync(object corpo) => await Client.PostAsJsonAsync("/api/Usuarios", corpo);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object corpo)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{id}");
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return client.SendAsync(request);
    }

    private async Task<UsuarioListItemDto> CriarAsync(string? cpf = null, DateOnly? nascimento = null, string? telefone = null, string role = "DS")
    {
        var response = await PostAsync(new
        {
            nome = $"Pessoal {Guid.NewGuid():N}", email = NovoEmail(), senha = $"Forte-{Guid.NewGuid():N}", cargoId = await CargoIdAsync(role),
            cpf, dataNascimento = nascimento, telefone,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    // Corpo de PUT com os campos obrigatórios do usuário atual mais os campos extras informados (omitidos ficam fora do JSON).
    private static JsonObject CorpoPut(UsuarioListItemDto u, Action<JsonObject>? extras = null)
    {
        var corpo = new JsonObject { ["nome"] = u.Nome, ["email"] = u.Email, ["cargoId"] = u.Cargo.Id.ToString() };
        extras?.Invoke(corpo);
        return corpo;
    }

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid usuarioId) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == usuarioId).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private static async Task<string> DetalheAsync(HttpResponseMessage response) =>
        (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!;

    private async Task<int> ExecSqlAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    private static void AssertSemDetalheInterno(string corpo)
    {
        Assert.DoesNotContain("UX_Usuarios_Cpf", corpo);
        Assert.DoesNotContain("SqlException", corpo);
        Assert.DoesNotContain("duplicate key", corpo, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 1. cadastro e consulta

    [Fact]
    public async Task Post_ComOsQuatroCampos_PersisteNormalizado_ERetornaNoCadastroENaConsulta()
    {
        var cpf = NovoCpf();
        var email = NovoEmail("Completo");
        var response = await PostAsync(new
        {
            nome = "Completo", email, senha = $"Forte-{Guid.NewGuid():N}", cargoId = await CargoIdAsync("DS"),
            cpf = $"  {Mascarar(cpf)}  ", dataNascimento = "2001-05-17", telefone = "  (11) 98765-4321  ",
        });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(cpf, (string)json["cpf"]!);
        Assert.Equal("2001-05-17", (string)json["dataNascimento"]!);   // só a data, sem horário nem fuso
        Assert.Equal("(11) 98765-4321", (string)json["telefone"]!);
        Assert.Equal(email, (string)json["email"]!);
        var id = Guid.Parse((string)json["id"]!);

        var salvo = await UsuarioAsync(id);
        Assert.Equal(cpf, salvo.Cpf);
        Assert.StartsWith("0", salvo.Cpf);
        Assert.Equal(new DateOnly(2001, 5, 17), salvo.DataNascimento);
        Assert.Equal("(11) 98765-4321", salvo.Telefone);

        // GET /api/Usuarios (fonte da tela de edição) devolve os mesmos valores.
        var lista = JsonNode.Parse(await Client.GetStringAsync("/api/Usuarios"))!.AsArray();
        var item = lista.Single(u => (string)u!["id"]! == id.ToString())!;
        Assert.Equal(cpf, (string)item["cpf"]!);
        Assert.Equal("2001-05-17", (string)item["dataNascimento"]!);
        Assert.Equal("(11) 98765-4321", (string)item["telefone"]!);
        Assert.Equal(email, (string)item["email"]!);
    }

    [Fact]
    public async Task Post_SemDadosPessoais_OuComVazios_GravaNull_EDoisUsuariosSemCpfNaoColidem()
    {
        var primeiro = await CriarAsync();
        var segundo = await CriarAsync(cpf: "   ", telefone: "");

        foreach (var id in new[] { primeiro.Id, segundo.Id })
        {
            var salvo = await UsuarioAsync(id);
            Assert.Null(salvo.Cpf);
            Assert.Null(salvo.DataNascimento);
            Assert.Null(salvo.Telefone);
        }
        Assert.Null(segundo.Cpf);
    }

    [Fact]
    public async Task Colunas_TemOsTiposEsperados_EIndiceDeCpfEhUnicoEFiltrado()
    {
        var tipos = await Db(async db =>
        {
            var conexao = db.Database.GetDbConnection();
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                SELECT OBJECT_NAME(c.object_id) + '.' + c.name + '=' + t.name + '(' + CAST(c.max_length AS varchar(10)) + ')' + CASE c.is_nullable WHEN 1 THEN ' NULL' ELSE '' END
                FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist'))
                  AND c.name IN ('Cpf', 'DataNascimento', 'Telefone', 'Email', 'EmailNormalizado')
                """;
            var lista = new List<string>();
            await using var reader = await comando.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lista.Add(reader.GetString(0));
            return lista;
        });

        Assert.Equal(
            new[]
            {
                "Usuarios.Cpf=varchar(20) NULL", "Usuarios.DataNascimento=date(3) NULL", "Usuarios.Email=varchar(100)",
                "Usuarios.EmailNormalizado=varchar(100)", "Usuarios.Telefone=varchar(20) NULL",
                "_usuarios_hist.Cpf=varchar(20) NULL", "_usuarios_hist.DataNascimento=date(3) NULL", "_usuarios_hist.Email=nvarchar(512)",
                "_usuarios_hist.Telefone=varchar(20) NULL",
            }.Order(),
            tipos.Order());

        var indice = await EventosSqlSupport.EscalarAsync<string>(Factory, """
            SELECT CONCAT(i.is_unique, '|', i.has_filter, '|', i.filter_definition) FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID('dbo.Usuarios') AND i.name = 'UX_Usuarios_Cpf'
            """);
        Assert.Equal("1|1|([Cpf] IS NOT NULL)", indice);

        // Nenhum índice único na tabela histórica (o mesmo CPF/e-mail se repete entre versões).
        Assert.Equal(0, await EventosSqlSupport.EscalarAsync<int>(Factory,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo._usuarios_hist') AND is_unique = 1 AND is_primary_key = 0"));
    }

    // ------------------------------------------------------------------ 2. atualização e omissão x limpeza

    [Fact]
    public async Task Put_AtualizaOsQuatroCampos_EPersiste()
    {
        var alvo = await CriarAsync(NovoCpf(), new DateOnly(1990, 1, 2), "1111-1111");
        var novoCpf = NovoCpf();
        var novoEmail = NovoEmail("Atualizado");

        var response = await PutAsync(Client, alvo.Id, new
        {
            nome = alvo.Nome, email = novoEmail, cargoId = alvo.Cargo.Id,
            cpf = Mascarar(novoCpf), dataNascimento = "1985-12-31", telefone = "+55 21 99999-0000",
        });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal(novoCpf, dto.Cpf);
        Assert.Equal(new DateOnly(1985, 12, 31), dto.DataNascimento);
        Assert.Equal("+55 21 99999-0000", dto.Telefone);
        Assert.Equal(novoEmail, dto.Email);

        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(novoCpf, salvo.Cpf);
        Assert.Equal(new DateOnly(1985, 12, 31), salvo.DataNascimento);
        Assert.Equal("+55 21 99999-0000", salvo.Telefone);
        Assert.Equal(novoEmail.ToUpperInvariant(), salvo.EmailNormalizado);
    }

    [Fact]
    public async Task Put_CamposPessoaisOmitidos_SaoPreservados_ESemMudancaNaoGeraHistorico()
    {
        var cpf = NovoCpf();
        var alvo = await CriarAsync(cpf, new DateOnly(2000, 2, 29), "3333-3333");

        // Contrato anterior (só nome/email/cargoId): não apaga nada.
        var response = await PutAsync(Client, alvo.Id, CorpoPut(alvo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(cpf, salvo.Cpf);
        Assert.Equal(new DateOnly(2000, 2, 29), salvo.DataNascimento);
        Assert.Equal("3333-3333", salvo.Telefone);
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Put_NullOuVazioExplicito_LimpaOCampo_EHistoricoGuardaOValorAnterior()
    {
        var cpf = NovoCpf();
        var alvo = await CriarAsync(cpf, new DateOnly(1999, 9, 9), "4444-4444");

        var response = await PutAsync(Client, alvo.Id, CorpoPut(alvo, c =>
        {
            c["cpf"] = "";
            c["dataNascimento"] = null;
            c["telefone"] = null;
        }));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Null(salvo.Cpf);
        Assert.Null(salvo.DataNascimento);
        Assert.Null(salvo.Telefone);

        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(cpf, linha.Cpf);
        Assert.Equal(new DateOnly(1999, 9, 9), linha.DataNascimento);
        Assert.Equal("4444-4444", linha.Telefone);

        // O CPF liberado pode ser usado por outro usuário.
        var outro = await CriarAsync(cpf);
        Assert.Equal(cpf, outro.Cpf);
    }

    // ------------------------------------------------------------------ 3-6. CPF único

    [Fact]
    public async Task Post_CpfDuplicado_ComESemMascara_Retorna409_ENaoCria()
    {
        var cpf = NovoCpf();
        await CriarAsync(cpf);
        var antes = await Db(db => db.Usuarios.CountAsync());

        foreach (var variante in new[] { cpf, Mascarar(cpf), $"  {Mascarar(cpf)} " })
        {
            var response = await PostAsync(new
            {
                nome = "Duplicado", email = NovoEmail(), senha = $"Forte-{Guid.NewGuid():N}", cargoId = await CargoIdAsync("DS"), cpf = variante,
            });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Já existe um usuário cadastrado com o CPF informado.", await DetalheAsync(response));
        }

        Assert.Equal(antes, await Db(db => db.Usuarios.CountAsync()));
    }

    [Fact]
    public async Task Put_CpfDeOutroUsuario_ComMascara_Retorna409_ENaoAlteraNemGeraHistorico()
    {
        var cpfOutro = NovoCpf();
        await CriarAsync(cpfOutro);
        var alvo = await CriarAsync(NovoCpf());

        var response = await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => { c["cpf"] = Mascarar(cpfOutro); c["telefone"] = "9999"; }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Existe outro usuário cadastrado com esse CPF.", await DetalheAsync(response));
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(alvo.Cpf, salvo.Cpf);
        Assert.Null(salvo.Telefone);
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Put_MantendoOProprioCpf_ComOuSemMascara_NaoEhDuplicidade()
    {
        var cpf = NovoCpf();
        var alvo = await CriarAsync(cpf);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => c["cpf"] = Mascarar(cpf)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => { c["cpf"] = cpf; c["telefone"] = "5555"; }))).StatusCode);

        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(cpf, salvo.Cpf);
        Assert.Equal("5555", salvo.Telefone);
    }

    [Fact]
    public async Task CpfDeUsuarioInativo_ContinuaReservado_NoCadastroENaAlteracao()
    {
        var cpf = NovoCpf();
        var inativo = await CriarAsync(cpf);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, inativo.Id)).StatusCode);

        var post = await PostAsync(new { nome = "Reuso", email = NovoEmail(), senha = $"Forte-{Guid.NewGuid():N}", cargoId = await CargoIdAsync("DS"), cpf = Mascarar(cpf) });
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);

        var alvo = await CriarAsync();
        var put = await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => c["cpf"] = cpf));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Null((await UsuarioAsync(alvo.Id)).Cpf);
    }

    // ------------------------------------------------------------------ 7. índice no banco e concorrência

    [Fact]
    public async Task Banco_IndiceUnicoRecusaCpfDuplicado_MesmoSemPassarPelaAplicacao_InclusiveDeInativo()
    {
        var existente = await CriarAsync(NovoCpf());
        await ExecSqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = @id;
            """, ("@resp", fx.AdminId), ("@id", existente.Id));

        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecSqlAsync("""
            INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Cpf)
            VALUES (NEWID(), N'Direto', @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, @cpf)
            """, ("@email", NovoEmail("direto")), ("@cargo", existente.Cargo.Id), ("@cpf", existente.Cpf!)));
        Assert.Contains(ex.Number, new[] { 2601, 2627 });
        Assert.Contains("UX_Usuarios_Cpf", ex.Message);
    }

    [Fact]
    public async Task Post_RequisicoesConcorrentesComMesmoCpf_CriaUmUnico_EAsDemaisRecebem409SemErroSql()
    {
        var cpf = NovoCpf();
        var cargoId = await CargoIdAsync("DS");

        // E-mails distintos: só o CPF (com e sem máscara) disputa o índice.
        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PostAsync(new
        {
            nome = $"Corrida CPF {i}", email = NovoEmail($"corrida{i}"), senha = $"Forte-{Guid.NewGuid():N}", cargoId,
            cpf = i % 2 == 0 ? cpf : Mascarar(cpf),
        })));

        Assert.Single(respostas, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var resposta in respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.Contains("Já existe um usuário cadastrado com o CPF informado.", corpo);
            AssertSemDetalheInterno(corpo);
        }

        Assert.Equal(1, await Db(db => db.Usuarios.CountAsync(u => u.Cpf == cpf)));
    }

    [Fact]
    public async Task Put_UsuariosDiferentesDisputandoOMesmoCpf_UmVence_OOutroRecebe409_SemHistoricoDoPerdedor()
    {
        var cpf = NovoCpf();
        var alvos = new List<UsuarioListItemDto>();
        for (var i = 0; i < 6; i++) alvos.Add(await CriarAsync());

        var respostas = await Task.WhenAll(alvos.Select((a, i) => PutAsync(Client, a.Id, CorpoPut(a, c => c["cpf"] = i % 2 == 0 ? cpf : Mascarar(cpf)))));

        var vencedores = alvos.Where((_, i) => respostas[i].StatusCode == HttpStatusCode.OK).ToList();
        var vencedor = Assert.Single(vencedores);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var resposta in respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.Contains("Existe outro usuário cadastrado com esse CPF.", corpo);
            AssertSemDetalheInterno(corpo);
        }

        Assert.Equal(1, await Db(db => db.Usuarios.CountAsync(u => u.Cpf == cpf)));
        Assert.Single(await HistoricoAsync(vencedor.Id));
        foreach (var perdedor in alvos.Where(a => a.Id != vencedor.Id))
        {
            Assert.Empty(await HistoricoAsync(perdedor.Id));
        }
    }

    [Fact]
    public async Task Put_ViolacaoDoIndiceNoUpdate_ViraConflito_MesmoQuandoAConsultaPreviaNaoEncontra()
    {
        // Intercalamento exato da corrida: a consulta prévia do PUT não vê o CPF (ainda livre); entre ela e o UPDATE, outra
        // conexão o grava. Só o índice protege, e a violação precisa virar o mesmo 409 de negócio.
        var cpf = NovoCpf();
        var pausa = new PausaDeComandoInterceptor();
        using var fabrica = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(pausa));
        using var cliente = await TestHelpers.CreateAuthenticatedClientAsync(fabrica);
        var alvoLocal = await CriarNaFabricaAsync(fabrica, cliente);
        var outroLocal = await CriarNaFabricaAsync(fabrica, cliente);

        var gate = pausa.Armar(sql => sql.StartsWith("UPDATE dbo.Usuarios SET Nome", StringComparison.Ordinal));
        var put = PutAsync(cliente, alvoLocal.Id, CorpoPut(alvoLocal, c => c["cpf"] = cpf));
        await gate.Atingida;
        await using (var conexao = new SqlConnection(fabrica.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                UPDATE dbo.Usuarios SET Cpf = @cpf WHERE Id = @id;
                """;
            comando.Parameters.AddWithValue("@resp", outroLocal.Id); // não o alvo: a linha dele está sob XLOCK do PUT pausado
            comando.Parameters.AddWithValue("@cpf", cpf);
            comando.Parameters.AddWithValue("@id", outroLocal.Id);
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }
        gate.Liberar();

        var resposta = await put;
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("Existe outro usuário cadastrado com esse CPF.", corpo);
        AssertSemDetalheInterno(corpo);
        Assert.Null((await TestHelpers.WithDbAsync(fabrica, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alvoLocal.Id))).Cpf);
        Assert.Empty(await TestHelpers.WithDbAsync(fabrica, db => db.UsuariosHistorico.Where(h => h.UsuarioId == alvoLocal.Id).ToListAsync()));
    }

    private static async Task<UsuarioListItemDto> CriarNaFabricaAsync(SqlServerApiFactory fabrica, HttpClient cliente)
    {
        var cargoId = await TestHelpers.WithDbAsync(fabrica, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var response = await cliente.PostAsJsonAsync("/api/Usuarios", new { nome = "Local", email = NovoEmail(), senha = $"Forte-{Guid.NewGuid():N}", cargoId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    // ------------------------------------------------------------------ 8-9. histórico completo

    [Fact]
    public async Task Put_HistoricoGuardaOEstadoAnteriorCompleto_ACadaAlteracao()
    {
        var cpf1 = NovoCpf();
        var alvo = await CriarAsync(cpf1, new DateOnly(1980, 3, 4), "1000-0001", role: "SEC");
        var email1 = alvo.Email;
        var cpf2 = NovoCpf();
        var email2 = NovoEmail("segunda-versao");

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, new
        {
            nome = "Versão 2", email = email2, cargoId = await CargoIdAsync("DIR"), cpf = cpf2, dataNascimento = "1981-04-05", telefone = "2000-0002",
        })).StatusCode);
        // Segunda alteração muda SÓ o telefone (os demais omitidos/iguais): ainda assim é mudança cadastral auditada.
        var v2 = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == alvo.Id);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, CorpoPut(v2, c => c["telefone"] = "3000-0003"))).StatusCode);

        var historico = await HistoricoAsync(alvo.Id);
        Assert.Equal(2, historico.Count);

        Assert.Equal(alvo.Nome, historico[0].Nome);
        Assert.Equal(email1, historico[0].Email);
        Assert.Equal(cpf1, historico[0].Cpf);
        Assert.Equal(new DateOnly(1980, 3, 4), historico[0].DataNascimento);
        Assert.Equal("1000-0001", historico[0].Telefone);
        Assert.Equal("Secretário", historico[0].CargoNome);
        Assert.True(historico[0].Ativo);
        Assert.Equal(UsuarioHistorico.OperacaoUpdate, historico[0].TipoOperacao);
        Assert.Equal(fx.AdminId, historico[0].UsuarioResponsavelId);
        Assert.Equal(AlmiranteApiFactory.AdminEmail, historico[0].UsuarioResponsavelLogin);
        Assert.Equal(IpTeste, historico[0].IpResponsavel);

        Assert.Equal("Versão 2", historico[1].Nome);
        Assert.Equal(email2, historico[1].Email);
        Assert.Equal(cpf2, historico[1].Cpf);
        Assert.Equal(new DateOnly(1981, 4, 5), historico[1].DataNascimento);
        Assert.Equal("2000-0002", historico[1].Telefone);
        Assert.Equal("Diretor", historico[1].CargoNome);

        var atual = await UsuarioAsync(alvo.Id);
        Assert.Equal(cpf2, atual.Cpf);
        Assert.Equal("3000-0003", atual.Telefone);
    }

    [Fact]
    public async Task Delete_HistoricoGuardaOEstadoAnteriorCompleto_InclusiveDadosPessoais()
    {
        var cpf = NovoCpf();
        var alvo = await CriarAsync(cpf, new DateOnly(1975, 7, 8), "7777-7777", role: "TES");

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(Client, alvo.Id)).StatusCode);

        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(UsuarioHistorico.OperacaoDelete, linha.TipoOperacao);
        Assert.True(linha.Ativo);
        Assert.Equal(alvo.Nome, linha.Nome);
        Assert.Equal(alvo.Email, linha.Email);
        Assert.Equal(cpf, linha.Cpf);
        Assert.Equal(new DateOnly(1975, 7, 8), linha.DataNascimento);
        Assert.Equal("7777-7777", linha.Telefone);
        Assert.Equal("Tesoureiro", linha.CargoNome);
        Assert.Equal(fx.AdminId, linha.UsuarioResponsavelId);
        Assert.Equal(IpTeste, linha.IpResponsavel);

        // Exclusão lógica preserva os dados pessoais no próprio registro.
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.False(salvo.Ativo);
        Assert.Equal(cpf, salvo.Cpf);
    }

    [Fact]
    public async Task Trigger_UpdateDeVariasLinhas_GeraUmHistoricoPorUsuario_ComOValorAnteriorDeCada()
    {
        var a = await CriarAsync(NovoCpf(), telefone: "1111");
        var b = await CriarAsync(null, new DateOnly(1995, 5, 5), "2222");

        Assert.Equal(2, await ExecSqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='192.0.2.9';
            UPDATE dbo.Usuarios SET Telefone = '9999' WHERE Id IN (@a, @b);
            """, ("@resp", fx.AdminId), ("@a", a.Id), ("@b", b.Id)));

        var ha = Assert.Single(await HistoricoAsync(a.Id));
        var hb = Assert.Single(await HistoricoAsync(b.Id));
        Assert.Equal("1111", ha.Telefone);
        Assert.Equal(a.Cpf, ha.Cpf);
        Assert.Null(ha.DataNascimento);
        Assert.Equal("2222", hb.Telefone);
        Assert.Null(hb.Cpf);
        Assert.Equal(new DateOnly(1995, 5, 5), hb.DataNascimento);
        Assert.All(new[] { ha, hb }, h => Assert.Equal("192.0.2.9", h.IpResponsavel));
    }

    [Fact]
    public async Task Trigger_MudancaSoDeDadoPessoalSemContextoDeAuditoria_EhRecusada()
    {
        var alvo = await CriarAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecSqlAsync("UPDATE dbo.Usuarios SET DataNascimento = '2001-01-01' WHERE Id = @id", ("@id", alvo.Id)));

        Assert.Equal(50021, ex.Number);
        Assert.Null((await UsuarioAsync(alvo.Id)).DataNascimento);
    }

    // ------------------------------------------------------------------ 10. atomicidade: falha no histórico desfaz a alteração

    [Fact]
    public async Task FalhaAoGravarHistorico_DesfazAAlteracao_NoPutENaExclusaoLogica()
    {
        var cpf = NovoCpf();
        var alvo = await CriarAsync(cpf, new DateOnly(1970, 1, 1), "8888-8888");
        var versaoAntes = (await UsuarioAsync(alvo.Id)).SecurityVersion;

        // Falha injetada só para ESTE usuário: o INSERT do trigger em _usuarios_hist viola a constraint.
        var constraint = $"CK_teste_falha_{Guid.NewGuid():N}";
        await ExecSqlAsync($"ALTER TABLE dbo._usuarios_hist WITH NOCHECK ADD CONSTRAINT [{constraint}] CHECK (UsuarioId <> '{alvo.Id}')");
        try
        {
            var put = await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => { c["cpf"] = NovoCpf(); c["telefone"] = "0000"; c["dataNascimento"] = "1971-01-01"; }));
            Assert.Equal(HttpStatusCode.InternalServerError, put.StatusCode);
            Assert.DoesNotContain(constraint, await put.Content.ReadAsStringAsync());

            var delete = await DeleteAsync(Client, alvo.Id);
            Assert.Equal(HttpStatusCode.InternalServerError, delete.StatusCode);
        }
        finally
        {
            await ExecSqlAsync($"ALTER TABLE dbo._usuarios_hist DROP CONSTRAINT [{constraint}]");
        }

        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(cpf, salvo.Cpf);
        Assert.Equal(new DateOnly(1970, 1, 1), salvo.DataNascimento);
        Assert.Equal("8888-8888", salvo.Telefone);
        Assert.True(salvo.Ativo);
        Assert.Equal(versaoAntes, salvo.SecurityVersion);
        Assert.Empty(await HistoricoAsync(alvo.Id));

        // Sem a falha, a mesma operação passa normalmente.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, alvo.Id, CorpoPut(alvo, c => c["telefone"] = "0000"))).StatusCode);
        Assert.Single(await HistoricoAsync(alvo.Id));
    }

    // ------------------------------------------------------------------ 12. login por e-mail preservado

    [Fact]
    public async Task Login_PorEmail_ContinuaFuncionandoAposAlterarDadosPessoais()
    {
        var email = NovoEmail("Login.Pessoal");
        var senha = $"Forte-{Guid.NewGuid():N}";
        var post = await PostAsync(new { nome = "Login", email, senha, cargoId = await CargoIdAsync("DS"), cpf = NovoCpf(), dataNascimento = "2002-02-02" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var criado = (await post.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(Client, criado.Id, CorpoPut(criado, c => c["telefone"] = "1234"))).StatusCode);

        using var novo = Factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(novo, email.ToUpperInvariant(), senha)).StatusCode);
    }
}
