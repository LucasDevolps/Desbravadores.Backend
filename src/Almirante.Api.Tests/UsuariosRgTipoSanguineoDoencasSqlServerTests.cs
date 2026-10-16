using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// RG, tipo sanguíneo e doenças/alergias contra SQL Server REAL: tipos das colunas (varchar), CHECK do tipo sanguíneo,
// cadastro -> consulta -> alteração -> exclusão lógica com o histórico (trigger) guardando o estado anterior completo.
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosRgTipoSanguineoDoencasSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.41";

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid usuarioId) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == usuarioId).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private async Task<UsuarioListItemDto> CriarAsync(string? rg, string? tipoSanguineo, string? doencasAlergias)
    {
        var cargoId = await Db(db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var response = await Client.PostAsJsonAsync("/api/Usuarios", new
        {
            nome = $"Participante Teste {Guid.NewGuid():N}", email = $"saude-{Guid.NewGuid():N}@local.dev", senha = $"Forte-{Guid.NewGuid():N}",
            cargoId, cpf = TestHelpers.MascararCpf(TestHelpers.NovoCpf()), rg, tipoSanguineo, doencasAlergias,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    private Task<HttpResponseMessage> PutAsync(Guid id, JsonObject corpo)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return Client.SendAsync(request);
    }

    private static JsonObject CorpoPut(UsuarioListItemDto u, Action<JsonObject>? extras = null)
    {
        var corpo = new JsonObject { ["nome"] = u.Nome, ["email"] = u.Email, ["cargoId"] = u.Cargo.Id.ToString() };
        extras?.Invoke(corpo);
        return corpo;
    }

    [Fact]
    public async Task Colunas_SaoVarcharNulaveis_NasDuasTabelas_EOCheckDoTipoSanguineoExiste()
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
                  AND c.name IN ('Rg', 'TipoSanguineo', 'DoencasAlergias')
                """;
            var lista = new List<string>();
            await using var reader = await comando.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lista.Add(reader.GetString(0));
            return lista;
        });

        Assert.Equal(
            new[]
            {
                "Usuarios.Rg=varchar(20) NULL", "Usuarios.TipoSanguineo=varchar(3) NULL", "Usuarios.DoencasAlergias=varchar(2000) NULL",
                "_usuarios_hist.Rg=varchar(20) NULL", "_usuarios_hist.TipoSanguineo=varchar(3) NULL", "_usuarios_hist.DoencasAlergias=varchar(2000) NULL",
            }.Order(),
            tipos.Order());
        Assert.Equal(1, await EventosSqlSupport.EscalarAsync<int>(Factory,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Usuarios_TipoSanguineo' AND parent_object_id = OBJECT_ID('dbo.Usuarios')"));
    }

    [Theory]
    [InlineData("X+")]
    [InlineData("ab+")]   // BIN2: só maiúsculas, como a API grava
    public async Task Banco_CheckRecusaTipoSanguineoInvalido_MesmoSemPassarPelaAplicacao(string tipo)
    {
        var alvo = await CriarAsync(null, null, null);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var conexao = new SqlConnection(Factory.ConnectionString);
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                UPDATE dbo.Usuarios SET TipoSanguineo = @tipo WHERE Id = @id;
                """;
            comando.Parameters.AddWithValue("@resp", fx.AdminId);
            comando.Parameters.AddWithValue("@tipo", tipo);
            comando.Parameters.AddWithValue("@id", alvo.Id);
            await comando.ExecuteNonQueryAsync();
        });

        Assert.Equal(547, ex.Number);
        Assert.Null((await UsuarioAsync(alvo.Id)).TipoSanguineo);
    }

    [Fact]
    public async Task Crud_CriaSemRg_ConsultaAlteraLimpaEExclui_ComHistoricoCompleto()
    {
        // 1. Cadastro sem RG (opcional), com tipo sanguíneo e doenças/alergias.
        const string doencas1 = "Alergia fictícia de teste;\nobservação fictícia de saúde.";
        var alvo = await CriarAsync(null, "o+", doencas1);
        Assert.Null(alvo.Rg);
        Assert.Equal("O+", alvo.TipoSanguineo);
        Assert.Equal(doencas1, alvo.DoencasAlergias);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((null, "O+", doencas1), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias));

        // 2. PUT sem os campos novos (contrato anterior): preserva tudo e não gera histórico.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(alvo.Id, CorpoPut(alvo))).StatusCode);
        salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((null, "O+", doencas1), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias));
        Assert.Empty(await HistoricoAsync(alvo.Id));

        // 3. PUT altera nome, RG, tipo e texto (o próprio CPF reenviado com máscara não é duplicidade).
        const string doencas2 = "Alergia fictícia alterada: dipirona.\r\nDiabetes fictícia tipo 1.";
        var put = await PutAsync(alvo.Id, CorpoPut(alvo, c =>
        {
            c["nome"] = "Participante Teste CRUD Editado";
            c["cpf"] = TestHelpers.MascararCpf(alvo.Cpf!);
            c["rg"] = " 12.345.678-9 ";
            c["tipoSanguineo"] = "AB-";
            c["doencasAlergias"] = doencas2;
        }));
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var dto = (await put.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal(("Participante Teste CRUD Editado", "12.345.678-9", "AB-", doencas2), (dto.Nome, dto.Rg, dto.TipoSanguineo, dto.DoencasAlergias));
        salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(("12.345.678-9", "AB-", doencas2, alvo.Cpf), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias, salvo.Cpf));
        Assert.Equal(1, await Db(db => db.Usuarios.CountAsync(u => u.Cpf == alvo.Cpf)));   // nenhum registro novo

        // 4. Consulta posterior (nova requisição, dados do banco).
        var lista = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!;
        var item = lista.Single(u => u.Id == alvo.Id);
        Assert.Equal(("12.345.678-9", "AB-", doencas2), (item.Rg, item.TipoSanguineo, item.DoencasAlergias));

        // 5. null/"" limpa RG, tipo e texto.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(alvo.Id, CorpoPut(item, c => { c["rg"] = ""; c["tipoSanguineo"] = null; c["doencasAlergias"] = ""; }))).StatusCode);
        salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((null, null, null), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias));

        // 6. Valor inválido no PUT: 400 sem alterar nada.
        var invalido = await PutAsync(alvo.Id, CorpoPut(item, c => c["tipoSanguineo"] = "Z+"));
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        Assert.Null((await UsuarioAsync(alvo.Id)).TipoSanguineo);

        // 7. Exclusão lógica: o registro some da listagem padrão, continua no banco inativo.
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{alvo.Id}");
        delete.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(delete)).StatusCode);
        Assert.DoesNotContain((await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!, u => u.Id == alvo.Id);
        Assert.False((await UsuarioAsync(alvo.Id)).Ativo);

        // Histórico: estado ANTERIOR de cada alteração (3 PUTs com mudança + DELETE).
        var historico = await HistoricoAsync(alvo.Id);
        Assert.Equal(3, historico.Count);
        Assert.Equal((null, "O+", doencas1, UsuarioHistorico.OperacaoUpdate), (historico[0].Rg, historico[0].TipoSanguineo, historico[0].DoencasAlergias, historico[0].TipoOperacao));
        Assert.Equal(("12.345.678-9", "AB-", doencas2), (historico[1].Rg, historico[1].TipoSanguineo, historico[1].DoencasAlergias));
        Assert.Equal((null, null, null, UsuarioHistorico.OperacaoDelete), (historico[2].Rg, historico[2].TipoSanguineo, historico[2].DoencasAlergias, historico[2].TipoOperacao));
        Assert.All(historico, h => Assert.Equal(IpTeste, h.IpResponsavel));
    }

    [Fact]
    public async Task DoencasAlergias_2000Caracteres_RoundTripIntegralNoSqlServer()
    {
        var texto = string.Concat(Enumerable.Repeat("Alergia fictícia ção; ", 100))[..1999] + ".";
        var alvo = await CriarAsync("1234567", "B-", texto);

        Assert.Equal(texto, (await UsuarioAsync(alvo.Id)).DoencasAlergias);
        Assert.Equal(2000, await EventosSqlSupport.EscalarAsync<int>(Factory,
            $"SELECT DATALENGTH(DoencasAlergias) FROM dbo.Usuarios WHERE Id = '{alvo.Id}'"));   // 1 byte por caractere (varchar, página 1252)
    }
}
