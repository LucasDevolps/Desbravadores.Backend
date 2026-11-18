using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Issue #193: PATCH /api/Usuarios/{id} contra SQL Server real (transação, linha travada e trigger de _usuarios_hist).
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosEdicaoParcialSqlServerTests : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.193";
    private readonly EventosSqlFixture fx;

    public UsuariosEdicaoParcialSqlServerTests(EventosSqlFixture fx)
    {
        this.fx = fx;
        fx.Factory.ViaCep.Reiniciar();
    }

    private HttpClient Admin => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().Include(u => u.Cargo).SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid id) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == id).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private Task<Guid> CargoIdAsync(string role) => Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    private Task<Usuario> AlvoAsync(Action<Usuario>? ajustar = null) =>
        TestHelpers.AddUsuarioAsync(Factory, $"Alvo parcial {Guid.NewGuid():N}", "DS", u =>
        {
            u.Cpf = TestHelpers.NovoCpf();
            u.Rg = TestHelpers.RgFicticio;
            u.Telefone = "1132345678";
            u.TipoSanguineo = "O+";
            u.DoencasAlergias = "Asma leve";
            ajustar?.Invoke(u);
        });

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, object corpo)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return client.SendAsync(request);
    }

    private static async Task<UsuarioListItemDto> PatchOkAsync(HttpClient client, Guid id, object corpo)
    {
        var response = await PatchAsync(client, id, corpo);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    private static object Retrato(Usuario u) => new
    {
        u.Nome, u.Email, u.EmailNormalizado, u.CargoId, u.Cpf, u.DataNascimento, u.Telefone, u.ResponsavelNome, u.ResponsavelEmail,
        u.ResponsavelCpf, u.ResponsavelTelefone, u.Rg, u.TipoSanguineo, u.DoencasAlergias, u.EhCin, u.DataExpiracao, u.EnderecoCep,
        u.EnderecoNumero, u.EnderecoComplemento, u.Ativo, u.SenhaHash, u.SecurityVersion, u.FotoCaminho,
    };

    private async Task AssertNadaAlteradoAsync(Guid id, object antes, int historicoAntes)
    {
        Assert.Equal(antes, Retrato(await UsuarioAsync(id)));
        Assert.Equal(historicoAntes, (await HistoricoAsync(id)).Count);
    }

    [Fact]
    public async Task SoTelefone_ConservaNomeEmailCargoEDemais_EAuditaOEstadoAnterior()
    {
        var alvo = await AlvoAsync();
        var antes = await UsuarioAsync(alvo.Id);
        var inicio = DateTime.UtcNow.AddSeconds(-2);

        var dto = await PatchOkAsync(Admin, alvo.Id, new { telefone = "(11) 98765-4321" });

        Assert.Equal((alvo.Id, antes.Nome, antes.Email, "11987654321", antes.Cpf, antes.Rg), (dto.Id, dto.Nome, dto.Email, dto.Telefone, dto.Cpf, dto.Rg));
        var depois = await UsuarioAsync(alvo.Id);
        Assert.Equal("11987654321", depois.Telefone);
        depois.Telefone = antes.Telefone;
        Assert.Equal(Retrato(antes), Retrato(depois));

        var linha = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal((UsuarioHistorico.OperacaoUpdate, fx.AdminId, IpTeste, antes.Nome, antes.Email, antes.CargoId, "1132345678"),
            (linha.TipoOperacao, linha.UsuarioResponsavelId, linha.IpResponsavel, linha.Nome, linha.Email, linha.CargoId, linha.Telefone));
        Assert.InRange(linha.AlteradoEmUtc, inicio, DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task FormularioAbertoDuranteOutraAlteracao_SalvarSoOTelefone_NaoDesfazCargoNemNomeNovos()
    {
        var alvo = await AlvoAsync();
        var (outroAdmin, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "ADM");
        var cargoTes = await CargoIdAsync("TES");
        var lidoPeloFormulario = await UsuarioAsync(alvo.Id);

        await PatchOkAsync(outroAdmin, alvo.Id, new { cargoId = cargoTes, nome = "Nome trocado por outro admin" });
        await PatchOkAsync(Admin, alvo.Id, new { telefone = "11987654321" });

        var depois = await UsuarioAsync(alvo.Id);
        Assert.Equal(("TES", "Nome trocado por outro admin", lidoPeloFormulario.Email, "11987654321"),
            (depois.Cargo!.Role, depois.Nome, depois.Email, depois.Telefone));
        Assert.Equal(2, (await HistoricoAsync(alvo.Id)).Count);
    }

    [Fact]
    public async Task Concorrencia_CamposIndependentesEmParalelo_TodosPersistem()
    {
        var alvo = await AlvoAsync();
        var (outroAdmin, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "ADM");
        var cargoTes = await CargoIdAsync("TES");

        var respostas = await Task.WhenAll(
            PatchAsync(Admin, alvo.Id, new { telefone = "11987654321" }),
            PatchAsync(outroAdmin, alvo.Id, new { cargoId = cargoTes }),
            PatchAsync(Admin, alvo.Id, new { nome = "Nome concorrente" }),
            PatchAsync(outroAdmin, alvo.Id, new { doencasAlergias = "Alergia a amendoim" }),
            PatchAsync(Admin, alvo.Id, new { tipoSanguineo = "ab-" }));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var depois = await UsuarioAsync(alvo.Id);
        Assert.Equal(("11987654321", "TES", "Nome concorrente", "Alergia a amendoim", "AB-", alvo.Email, alvo.Cpf),
            (depois.Telefone, depois.Cargo!.Role, depois.Nome, depois.DoencasAlergias, depois.TipoSanguineo, depois.Email, depois.Cpf));
        Assert.Equal(5, (await HistoricoAsync(alvo.Id)).Count);
    }

    [Fact]
    public async Task NullRemoveSoOpcionais_NomeEmailCargoNullSaoRecusados_SemGravar()
    {
        var alvo = await AlvoAsync();

        var dto = await PatchOkAsync(Admin, alvo.Id, new { telefone = (string?)null, doencasAlergias = "" });
        Assert.Equal(((string?)null, (string?)null, "O+"), (dto.Telefone, dto.DoencasAlergias, dto.TipoSanguineo));

        var antes = Retrato(await UsuarioAsync(alvo.Id));
        var historico = (await HistoricoAsync(alvo.Id)).Count;
        foreach (var corpo in new object[]
        {
            new { nome = (string?)null, telefone = "11987654321" },
            new { email = "", telefone = "11987654321" },
            new { cargoId = (Guid?)null, telefone = "11987654321" },
            new { cpf = (string?)null },
            new { ehCin = (bool?)null },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(Admin, alvo.Id, corpo)).StatusCode);
        }

        await AssertNadaAlteradoAsync(alvo.Id, antes, historico);
    }

    [Fact]
    public async Task RegrasNoEstadoResultante_QualquerFalhaRecusaTudo()
    {
        var outro = await AlvoAsync();
        var alvo = await AlvoAsync();
        var menor = await AlvoAsync(u =>
        {
            u.DataNascimento = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-10);
            u.ResponsavelNome = "Responsável Teste";
            u.ResponsavelEmail = "resp@local.dev";
        });

        async Task Recusa(Usuario usuario, object corpo, HttpStatusCode status, string? campo = null)
        {
            var antes = Retrato(await UsuarioAsync(usuario.Id));
            var historico = (await HistoricoAsync(usuario.Id)).Count;
            var response = await PatchAsync(Admin, usuario.Id, corpo);
            Assert.True(response.StatusCode == status, $"{status} esperado: {await response.Content.ReadAsStringAsync()}");
            if (campo is not null)
            {
                Assert.Contains(campo, (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
            }

            await AssertNadaAlteradoAsync(usuario.Id, antes, historico);
        }

        await Recusa(alvo, new { telefone = "11987654321", email = outro.Email.ToUpperInvariant() }, HttpStatusCode.Conflict);
        // Com documento completo o CPF nem pode mudar (400); a unicidade (409) vale para o cadastro legado ainda sem documento.
        await Recusa(alvo, new { telefone = "11987654321", cpf = outro.Cpf }, HttpStatusCode.BadRequest, "Cpf");
        var legado = await TestHelpers.AddUsuarioAsync(Factory, $"Legado sem documento {Guid.NewGuid():N}", "DS");
        await Recusa(legado, new { telefone = "11987654321", cpf = outro.Cpf }, HttpStatusCode.Conflict);
        await Recusa(alvo, new { doencasAlergias = "Nova", rg = "SP-99.999.999" }, HttpStatusCode.BadRequest, "Rg");
        await Recusa(alvo, new { telefone = "11987654321", ehCin = true }, HttpStatusCode.BadRequest, "EhCin");
        await Recusa(alvo, new { telefone = "11987654321", dataNascimento = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-10) },
            HttpStatusCode.BadRequest, "ResponsavelNome");
        await Recusa(menor, new { telefone = "11987654321", responsavelEmail = (string?)null }, HttpStatusCode.BadRequest, "ResponsavelEmail");
        await Recusa(alvo, new { telefone = "11987654321", cargoId = Guid.NewGuid() }, HttpStatusCode.BadRequest, "CargoId");

        var inativo = await AlvoAsync(u => u.Ativo = false);
        await Recusa(inativo, new { telefone = "11987654321" }, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(Admin, Guid.NewGuid(), new { telefone = "11987654321" })).StatusCode);

        // O responsável vale para o estado final: com a data de nascimento de adulto no mesmo PATCH, removê-lo é aceito.
        var adulto = await PatchOkAsync(Admin, menor.Id, new { dataNascimento = "1990-01-01", responsavelNome = (string?)null, responsavelEmail = (string?)null });
        Assert.Equal(((string?)null, (string?)null), (adulto.ResponsavelNome, adulto.ResponsavelEmail));
    }

    [Fact]
    public async Task Endereco_OmitidoMantem_ENumeroSozinhoMudaSoONumero()
    {
        var alvo = await AlvoAsync();

        var comEndereco = await PatchOkAsync(Admin, alvo.Id, new { endereco = new { cep = "04382-160", numero = "12", complemento = "casa 2" } });
        Assert.Equal(("04382160", "12", "Rua Fictícia 04382160"), (comEndereco.Endereco!.Cep, comEndereco.Endereco.Numero, comEndereco.Endereco.Logradouro));
        var consultas = Factory.ViaCep.Consultas.Count;

        var soTelefone = await PatchOkAsync(Admin, alvo.Id, new { telefone = "11987654321" });
        var soNumero = await PatchOkAsync(Admin, alvo.Id, new { endereco = new { numero = "99" } });

        Assert.Equal(("04382160", "12", "casa 2"), (soTelefone.Endereco!.Cep, soTelefone.Endereco.Numero, soTelefone.Endereco.Complemento));
        Assert.Equal(("04382160", "99", "casa 2"), (soNumero.Endereco!.Cep, soNumero.Endereco.Numero, soNumero.Endereco.Complemento));
        Assert.Equal(consultas, Factory.ViaCep.Consultas.Count);
    }

    [Fact]
    public async Task Autorizacao_PoliticaPorAlvoVaiPeloCargoAtual_EPeloNovoSoQuandoEnviado()
    {
        var (secretario, secretarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "SEC");
        var membro = await AlvoAsync();
        var tesoureiro = await TestHelpers.AddUsuarioAsync(Factory, "Tesoureiro alvo do patch", "TES");
        var (cargoDs, cargoTes) = (await CargoIdAsync("DS"), await CargoIdAsync("TES"));

        Assert.Equal("11987654321", (await PatchOkAsync(secretario, membro.Id, new { telefone = "11987654321" })).Telefone);
        Assert.Equal("11987654321", (await PatchOkAsync(secretario, membro.Id, new { cargoId = cargoDs })).Telefone);

        var antesMembro = Retrato(await UsuarioAsync(membro.Id));
        var antesTesoureiro = Retrato(await UsuarioAsync(tesoureiro.Id));
        var antesSecretario = Retrato(await UsuarioAsync(secretarioId));
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(secretario, membro.Id, new { cargoId = cargoTes })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(secretario, tesoureiro.Id, new { telefone = "11912345678" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(secretario, secretarioId, new { telefone = "11912345678" })).StatusCode);
        Assert.Equal(antesMembro, Retrato(await UsuarioAsync(membro.Id)));
        Assert.Equal(antesTesoureiro, Retrato(await UsuarioAsync(tesoureiro.Id)));
        Assert.Equal(antesSecretario, Retrato(await UsuarioAsync(secretarioId)));

        var (desbravador, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(desbravador, membro.Id, new { telefone = "11912345678" })).StatusCode);
    }

    [Fact]
    public async Task Put_ContinuaComOContratoCompleto()
    {
        var alvo = await AlvoAsync();
        var cargoTes = await CargoIdAsync("TES");

        var semNome = await Admin.PutAsJsonAsync($"/api/Usuarios/{alvo.Id}", new { telefone = "11987654321" });
        Assert.Equal(HttpStatusCode.BadRequest, semNome.StatusCode);
        Assert.Equal(["Nome", "Email", "CargoId"], (await semNome.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);

        var put = await Admin.PutAsJsonAsync($"/api/Usuarios/{alvo.Id}", new { nome = "Nome pelo PUT", email = $"put-{Guid.NewGuid():N}@local.dev", cargoId = cargoTes });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var depois = await UsuarioAsync(alvo.Id);
        Assert.Equal(("Nome pelo PUT", "TES", "1132345678", "O+"), (depois.Nome, depois.Cargo!.Role, depois.Telefone, depois.TipoSanguineo));
    }
}
