using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

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
            cargoId, cpf = TestHelpers.MascararCpf(TestHelpers.NovoCpf()), ehCin = rg is null, rg, tipoSanguineo, doencasAlergias,
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
    [InlineData("ab+")]
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

    [Fact(DisplayName = "Cadastro com CIN e sem RG: consulta, altera e limpa tipo sanguíneo e doenças/alergias, exclui e guarda o histórico completo")]
    public async Task Crud_CriaSemRg_ConsultaAlteraLimpaEExclui_ComHistoricoCompleto()
    {
        //Arrange
        const string doencas1 = "Alergia fictícia de teste;\nobservação fictícia de saúde.";
        const string doencas2 = "Alergia fictícia alterada: dipirona.\r\nDiabetes fictícia tipo 1.";
        var alvo = await CriarAsync(null, "o+", doencas1);
        var salvoAposCadastro = await UsuarioAsync(alvo.Id);

        //Act
        var semMudanca = await PutAsync(alvo.Id, CorpoPut(alvo));
        var salvoSemMudanca = await UsuarioAsync(alvo.Id);
        var historicoSemMudanca = await HistoricoAsync(alvo.Id);
        var put = await PutAsync(alvo.Id, CorpoPut(alvo, c =>
        {
            c["nome"] = "Participante Teste CRUD Editado";
            c["cpf"] = TestHelpers.MascararCpf(alvo.Cpf!);
            c["tipoSanguineo"] = "AB-";
            c["doencasAlergias"] = doencas2;
        }));
        var salvoEditado = await UsuarioAsync(alvo.Id);
        var usuariosComOCpf = await Db(db => db.Usuarios.CountAsync(u => u.Cpf == alvo.Cpf));
        var item = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == alvo.Id);
        var limpeza = await PutAsync(alvo.Id, CorpoPut(item, c => { c["rg"] = ""; c["tipoSanguineo"] = null; c["doencasAlergias"] = ""; }));
        var salvoLimpo = await UsuarioAsync(alvo.Id);
        var invalido = await PutAsync(alvo.Id, CorpoPut(item, c => c["tipoSanguineo"] = "Z+"));
        var tipoAposInvalido = (await UsuarioAsync(alvo.Id)).TipoSanguineo;
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{alvo.Id}");
        delete.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        var exclusao = await Client.SendAsync(delete);
        var listaAposExclusao = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!;
        var salvoExcluido = await UsuarioAsync(alvo.Id);
        var historico = await HistoricoAsync(alvo.Id);

        //Assert
        Assert.Null(alvo.Rg);
        Assert.Equal("O+", alvo.TipoSanguineo);
        Assert.Equal(doencas1, alvo.DoencasAlergias);
        Assert.Equal((null, "O+", doencas1), (salvoAposCadastro.Rg, salvoAposCadastro.TipoSanguineo, salvoAposCadastro.DoencasAlergias));

        Assert.Equal(HttpStatusCode.OK, semMudanca.StatusCode);
        Assert.Equal((null, "O+", doencas1), (salvoSemMudanca.Rg, salvoSemMudanca.TipoSanguineo, salvoSemMudanca.DoencasAlergias));
        Assert.Empty(historicoSemMudanca);

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var dto = (await put.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal(("Participante Teste CRUD Editado", (string?)null, "AB-", doencas2), (dto.Nome, dto.Rg, dto.TipoSanguineo, dto.DoencasAlergias));
        Assert.Equal(((string?)null, "AB-", doencas2, alvo.Cpf), (salvoEditado.Rg, salvoEditado.TipoSanguineo, salvoEditado.DoencasAlergias, salvoEditado.Cpf));
        Assert.Equal(1, usuariosComOCpf);
        Assert.Equal(((string?)null, "AB-", doencas2), (item.Rg, item.TipoSanguineo, item.DoencasAlergias));

        Assert.Equal(HttpStatusCode.OK, limpeza.StatusCode);
        Assert.Equal((null, null, null), (salvoLimpo.Rg, salvoLimpo.TipoSanguineo, salvoLimpo.DoencasAlergias));
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        Assert.Null(tipoAposInvalido);

        Assert.Equal(HttpStatusCode.NoContent, exclusao.StatusCode);
        Assert.DoesNotContain(listaAposExclusao, u => u.Id == alvo.Id);
        Assert.False(salvoExcluido.Ativo);

        Assert.Equal(3, historico.Count);
        Assert.Equal((null, "O+", doencas1, UsuarioHistorico.OperacaoUpdate), (historico[0].Rg, historico[0].TipoSanguineo, historico[0].DoencasAlergias, historico[0].TipoOperacao));
        Assert.Equal(((string?)null, "AB-", doencas2), (historico[1].Rg, historico[1].TipoSanguineo, historico[1].DoencasAlergias));
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
            $"SELECT DATALENGTH(DoencasAlergias) FROM dbo.Usuarios WHERE Id = '{alvo.Id}'"));
    }
}
