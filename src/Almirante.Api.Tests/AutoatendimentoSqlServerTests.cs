using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>Autoatendimento (/api/Me) e hierarquia de cadastros contra SQL Server real: transação, trigger de auditoria e fotos em disco.</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AutoatendimentoSqlServerTests(FotosSqlFixture fx) : IClassFixture<FotosSqlFixture>
{
    private const string IpTeste = "203.0.113.127";

    private SqlServerApiFactory Factory => fx.Factory;
    private HttpClient Admin => fx.Client;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().Include(u => u.Cargo).SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid id) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == id).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private Task<Guid> CargoIdAsync(string role) => Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    private Task<(HttpClient Client, Guid Id)> UsuarioAsync(string role) => TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, role);

    private static Task<HttpResponseMessage> EnviarComIp(HttpClient client, HttpMethod metodo, string rota, HttpContent? conteudo = null)
    {
        var request = new HttpRequestMessage(metodo, rota) { Content = conteudo };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PutMeAsync(HttpClient client, object corpo) =>
        EnviarComIp(client, HttpMethod.Put, "/api/Me", JsonContent.Create(corpo));

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = Factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var login = await TestHelpers.PostLoginAsync(client, email, TestHelpers.SenhaPadraoTeste);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken);
        return client;
    }

    private IEnumerable<string> ArquivosDe(Guid id) =>
        Directory.Exists(Factory.FotosPath)
            ? Directory.EnumerateFiles(Factory.FotosPath, "*", SearchOption.AllDirectories).Where(f => f.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || f.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            : [];

    [Fact]
    public async Task PutMe_AtualizaSoDadosPessoais_MantemNomeEmailCargo_EAuditaComOProprioUsuario()
    {
        var (desbravador, id) = await UsuarioAsync("DS");
        var (_, outroId) = await UsuarioAsync("DS");
        var antes = await UsuarioAsync(id);
        var outroAntes = await UsuarioAsync(outroId);
        var cpf = TestHelpers.NovoCpf();
        var inicio = DateTime.UtcNow.AddSeconds(-2);

        var resposta = await PutMeAsync(desbravador, new
        {
            cpf = TestHelpers.MascararCpf(cpf), dataNascimento = "1990-05-04", telefone = "(11) 98765-4321", rg = "MG-12.345.678",
            ehCin = false, dataExpiracao = "2031-01-31", tipoSanguineo = "ab+", doencasAlergias = "  Alergia a amendoim  ",
            responsavelNome = "Responsável Teste", responsavelEmail = "resp@local.dev", responsavelTelefone = "1134567890",
            endereco = new { cep = "04382-160", numero = "12", complemento = "casa 2" },
            usuarioId = outroId, ipResponsavel = "10.9.9.9",
        });

        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var dto = (await resposta.Content.ReadFromJsonAsync<MeusDadosDto>())!;
        Assert.Equal((id, cpf, "11987654321", "AB+", "Alergia a amendoim", "04382160", "12", "casa 2", "Rua Fictícia 04382160"),
            (dto.Id, dto.Cpf, dto.Telefone, dto.TipoSanguineo, dto.DoencasAlergias, dto.Endereco!.Cep, dto.Endereco.Numero, dto.Endereco.Complemento, dto.Endereco.Logradouro));

        var depois = await UsuarioAsync(id);
        Assert.Equal((antes.Nome, antes.Email, antes.EmailNormalizado, antes.CargoId, true, antes.SenhaHash, antes.SecurityVersion, antes.FotoCaminho),
            (depois.Nome, depois.Email, depois.EmailNormalizado, depois.CargoId, depois.Ativo, depois.SenhaHash, depois.SecurityVersion, depois.FotoCaminho));
        Assert.Equal((cpf, new DateOnly(1990, 5, 4), "MG-12.345.678", new DateOnly(2031, 1, 31), "Responsável Teste", "resp@local.dev"),
            (depois.Cpf, depois.DataNascimento, depois.Rg, depois.DataExpiracao, depois.ResponsavelNome, depois.ResponsavelEmail));

        var linha = Assert.Single(await HistoricoAsync(id));
        Assert.Equal((id, antes.Email, IpTeste, UsuarioHistorico.OperacaoUpdate, (string?)null, antes.Nome),
            (linha.UsuarioResponsavelId, linha.UsuarioResponsavelLogin, linha.IpResponsavel, linha.TipoOperacao, linha.Cpf, linha.Nome));
        Assert.InRange(linha.AlteradoEmUtc, inicio, DateTime.UtcNow.AddSeconds(2));

        var outroDepois = await UsuarioAsync(outroId);
        Assert.Equal((outroAntes.Cpf, outroAntes.Telefone), (outroDepois.Cpf, outroDepois.Telefone));
        Assert.Empty(await HistoricoAsync(outroId));
    }

    [Fact]
    public async Task PutMe_OmitidoMantem_NullRemove_ENomeEmailCargoContinuamDaSecretaria()
    {
        var (desbravador, id) = await UsuarioAsync("DS");
        Assert.Equal(HttpStatusCode.OK, (await PutMeAsync(desbravador, new { cpf = TestHelpers.NovoCpf(), telefone = "11987654321", tipoSanguineo = "O-", ehCin = true })).StatusCode);

        var resposta = await PutMeAsync(desbravador, new { telefone = (string?)null, doencasAlergias = "Asma" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var salvo = await UsuarioAsync(id);
        Assert.Equal(((string?)null, "O-", "Asma", true), (salvo.Telefone, salvo.TipoSanguineo, salvo.DoencasAlergias, salvo.EhCin));
        Assert.Equal(2, (await HistoricoAsync(id)).Count);

        var cargoDiretor = await CargoIdAsync("DIR");
        var recusado = await PutMeAsync(desbravador, new { cargoId = cargoDiretor, nome = "Diretor", email = "novo@local.dev" });
        Assert.Equal(HttpStatusCode.BadRequest, recusado.StatusCode);
        Assert.Equal(("DS", salvo.Nome, salvo.Email), ((await UsuarioAsync(id)).Cargo!.Role, (await UsuarioAsync(id)).Nome, (await UsuarioAsync(id)).Email));
        Assert.Equal(2, (await HistoricoAsync(id)).Count);
    }

    [Fact]
    public async Task PutMe_ReutilizaRegrasDoBanco_CpfUnico_CpfNaoRemovivel_Responsavel_Rg_EViaCep()
    {
        var (desbravador, id) = await UsuarioAsync("DS");
        var (outro, _) = await UsuarioAsync("DS");
        var cpfDoOutro = TestHelpers.NovoCpf();
        Assert.Equal(HttpStatusCode.OK, (await PutMeAsync(outro, new { cpf = cpfDoOutro })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutMeAsync(desbravador, new { cpf = TestHelpers.NovoCpf(), rg = TestHelpers.RgFicticio })).StatusCode);
        var historico = (await HistoricoAsync(id)).Count;

        var cpfDuplicado = await PutMeAsync(desbravador, new { cpf = cpfDoOutro });
        Assert.Equal(HttpStatusCode.Conflict, cpfDuplicado.StatusCode);
        Assert.Equal("CPF já cadastrado.", (string)(await JsonAsync(cpfDuplicado))["title"]!);

        var semCpf = await PutMeAsync(desbravador, new { cpf = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, semCpf.StatusCode);
        Assert.Contains("Cpf", (await JsonAsync(semCpf))["errors"]!.AsObject().Select(e => e.Key));

        var menorSemResponsavel = await PutMeAsync(desbravador, new { dataNascimento = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-12).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.BadRequest, menorSemResponsavel.StatusCode);
        Assert.Contains("ResponsavelNome", (await JsonAsync(menorSemResponsavel))["errors"]!.AsObject().Select(e => e.Key));

        var semRg = await PutMeAsync(desbravador, new { rg = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, semRg.StatusCode);
        Assert.Contains("Rg", (await JsonAsync(semRg))["errors"]!.AsObject().Select(e => e.Key));

        var cepInexistente = await PutMeAsync(desbravador, new { endereco = new { cep = ViaCepFalso.CepInexistente } });
        Assert.Equal(HttpStatusCode.BadRequest, cepInexistente.StatusCode);

        Factory.ViaCep.Responder = (_, _) => throw new HttpRequestException("ViaCEP fora do ar");
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PutMeAsync(desbravador, new { endereco = new { cep = "01001000" } })).StatusCode);
        }
        finally
        {
            Factory.ViaCep.Reiniciar();
        }

        var salvo = await UsuarioAsync(id);
        Assert.Equal(((DateOnly?)null, TestHelpers.RgFicticio, (string?)null), (salvo.DataNascimento, salvo.Rg, salvo.EnderecoCep));
        Assert.Equal(historico, (await HistoricoAsync(id)).Count);

        var comResponsavel = await PutMeAsync(desbravador, new
        {
            dataNascimento = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-12).ToString("yyyy-MM-dd"),
            responsavelNome = "Mãe Teste", responsavelEmail = "mae@local.dev", ehCin = true, rg = (string?)null,
        });
        Assert.True(comResponsavel.StatusCode == HttpStatusCode.OK, await comResponsavel.Content.ReadAsStringAsync());
        Assert.Equal(("Mãe Teste", (string?)null, true), ((await UsuarioAsync(id)).ResponsavelNome, (await UsuarioAsync(id)).Rg, (await UsuarioAsync(id)).EhCin));
    }

    [Fact]
    public async Task FotoPropria_EnviaConsultaSubstituiERemove_ComAuditoriaEOMesmoArmazenamento()
    {
        var (desbravador, id) = await UsuarioAsync("DS");
        Assert.Equal(HttpStatusCode.NotFound, (await desbravador.GetAsync("/api/Me/foto")).StatusCode);

        var enviada = await EnviarComIp(desbravador, HttpMethod.Put, "/api/Me/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(300, 200)));
        Assert.True(enviada.StatusCode == HttpStatusCode.OK, await enviada.Content.ReadAsStringAsync());
        var dados = await JsonAsync(enviada);
        Assert.Equal(("/api/Me/foto", id), ((string)dados["fotoUrl"]!, (Guid)dados["id"]!));
        Assert.Null(dados["cargo"]!["descricao"]);
        var primeira = (await UsuarioAsync(id)).FotoCaminho!;

        var baixada = await desbravador.GetAsync("/api/Me/foto");
        Assert.Equal(("image/webp", true, true, false), (baixada.Content.Headers.ContentType!.MediaType, baixada.Headers.CacheControl!.Private,
            baixada.Headers.CacheControl.NoCache, baixada.Headers.CacheControl.NoStore));
        var etag = baixada.Headers.ETag!;
        var condicional = new HttpRequestMessage(HttpMethod.Get, "/api/Me/foto");
        condicional.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await desbravador.SendAsync(condicional)).StatusCode);
        Assert.Equal(await (await Admin.GetAsync($"/api/Usuarios/{id}/foto")).Content.ReadAsByteArrayAsync(), await baixada.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, (await EnviarComIp(desbravador, HttpMethod.Put, "/api/Me/foto", FotosTestKit.Multipart(FotosTestKit.Png(200, 200), "f.png", "image/png"))).StatusCode);
        var segunda = (await UsuarioAsync(id)).FotoCaminho!;
        Assert.NotEqual(primeira, segunda);
        Assert.Single(ArquivosDe(id));

        var invalida = await desbravador.PutAsync("/api/Me/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(32, 32)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalida.StatusCode);
        Assert.Equal(segunda, (await UsuarioAsync(id)).FotoCaminho);

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarComIp(desbravador, HttpMethod.Delete, "/api/Me/foto")).StatusCode);
        Assert.Null((await UsuarioAsync(id)).FotoCaminho);
        Assert.Empty(ArquivosDe(id));
        Assert.Equal(HttpStatusCode.NotFound, (await desbravador.GetAsync("/api/Me/foto")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await desbravador.DeleteAsync("/api/Me/foto")).StatusCode);
        Assert.Null((string?)JsonNode.Parse(await desbravador.GetStringAsync("/api/Me"))!["fotoUrl"]);

        var historico = await HistoricoAsync(id);
        Assert.Equal(3, historico.Count);
        Assert.All(historico, h => Assert.Equal((id, IpTeste), (h.UsuarioResponsavelId, h.IpResponsavel)));
        Assert.Equal(new string?[] { null, primeira, segunda }, historico.Select(h => h.FotoCaminho));
    }

    [Fact]
    public async Task FotoDeTerceiro_DesbravadorETesoureiroRecebem403_MasGerenciamAPropria()
    {
        var alvo = await TestHelpers.AddUsuarioAsync(Factory, "Alvo da foto", "DS");
        Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsync($"/api/Usuarios/{alvo.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        var caminho = (await UsuarioAsync(alvo.Id)).FotoCaminho;

        foreach (var role in new[] { "DS", "TES", "CONS" })
        {
            var (client, proprioId) = await UsuarioAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync($"/api/Usuarios/{alvo.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/Usuarios/{alvo.Id}/foto")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/Usuarios/{alvo.Id}/foto")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync($"/api/Usuarios/{proprioId}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/Me/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
            Assert.NotNull((await UsuarioAsync(proprioId)).FotoCaminho);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/Me/foto")).StatusCode);
        }

        Assert.Equal(caminho, (await UsuarioAsync(alvo.Id)).FotoCaminho);
    }

    [Fact]
    public async Task Hierarquia_SecretarioGerenciaSoCargosSemGestao_DiretoriaGerenciaTodosMenosAdm()
    {
        var (secretario, secretarioId) = await UsuarioAsync("SEC");
        var (diretorAssociado, _) = await UsuarioAsync("DIRA");
        var membro = await TestHelpers.AddUsuarioAsync(Factory, "Membro comum", "DS");
        var tesoureiro = await TestHelpers.AddUsuarioAsync(Factory, "Tesoureiro alvo", "TES");
        var diretor = await TestHelpers.AddUsuarioAsync(Factory, "Diretor alvo", "DIR");
        var administrador = await TestHelpers.AddUsuarioAsync(Factory, "Administrador alvo", "ADM");
        var (cargoDs, cargoTes, cargoCons) = (await CargoIdAsync("DS"), await CargoIdAsync("TES"), await CargoIdAsync("CONS"));

        Assert.Equal(HttpStatusCode.OK, (await secretario.PutAsJsonAsync($"/api/Usuarios/{membro.Id}", new { nome = "Membro editado", email = membro.Email, cargoId = cargoCons })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.PutAsJsonAsync($"/api/Usuarios/{membro.Id}", new { nome = "Promovido", email = membro.Email, cargoId = cargoTes })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.PutAsJsonAsync($"/api/Usuarios/{secretarioId}", new { nome = "Eu", email = (await UsuarioAsync(secretarioId)).Email, cargoId = cargoTes })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.PutAsJsonAsync($"/api/Usuarios/{tesoureiro.Id}", new { nome = "Rebaixado", email = tesoureiro.Email, cargoId = cargoDs })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.DeleteAsync($"/api/Usuarios/{diretor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.PutAsync($"/api/Usuarios/{diretor.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secretario.PutAsync($"/api/Usuarios/{membro.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await secretario.DeleteAsync($"/api/Usuarios/{membro.Id}/foto")).StatusCode);

        Assert.Equal(("CONS", "Membro editado"), ((await UsuarioAsync(membro.Id)).Cargo!.Role, (await UsuarioAsync(membro.Id)).Nome));
        Assert.Equal(("TES", "SEC", "DIR"), ((await UsuarioAsync(tesoureiro.Id)).Cargo!.Role, (await UsuarioAsync(secretarioId)).Cargo!.Role, (await UsuarioAsync(diretor.Id)).Cargo!.Role));
        Assert.True((await UsuarioAsync(diretor.Id)).Ativo);
        Assert.Null((await UsuarioAsync(diretor.Id)).FotoCaminho);

        Assert.Equal(HttpStatusCode.OK, (await diretorAssociado.PutAsJsonAsync($"/api/Usuarios/{membro.Id}", new { nome = "Novo tesoureiro", email = membro.Email, cargoId = cargoTes })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await diretorAssociado.PutAsync($"/api/Usuarios/{diretor.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await diretorAssociado.DeleteAsync($"/api/Usuarios/{tesoureiro.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await diretorAssociado.PutAsJsonAsync($"/api/Usuarios/{administrador.Id}", new { nome = "X", email = administrador.Email, cargoId = cargoDs })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await diretorAssociado.DeleteAsync($"/api/Usuarios/{administrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await diretorAssociado.PutAsync($"/api/Usuarios/{administrador.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(("TES", false, "ADM"), ((await UsuarioAsync(membro.Id)).Cargo!.Role, (await UsuarioAsync(tesoureiro.Id)).Ativo, (await UsuarioAsync(administrador.Id)).Cargo!.Role));
    }

    [Fact]
    public async Task MudancaDeCargoPelaApi_RemovePrivilegiosAntigos_NoTokenNoRefreshENaConexaoDoHub()
    {
        var (tesoureiro, tesoureiroId) = await UsuarioAsync("TES");
        var (diretor, _) = await UsuarioAsync("DIR");
        var email = (await UsuarioAsync(tesoureiroId)).Email;
        var token = NotificacoesTestKit.Token(tesoureiro);
        Assert.Equal(HttpStatusCode.OK, (await tesoureiro.GetAsync("/api/Lancamentos")).StatusCode);
        await using var hub = NotificacoesTestKit.Hub(Factory, token);
        var fechada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { fechada.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();

        var troca = await diretor.PutAsJsonAsync($"/api/Usuarios/{tesoureiroId}", new { nome = "Ex-tesoureiro", email, cargoId = await CargoIdAsync("DS") });
        Assert.Equal(HttpStatusCode.OK, troca.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await tesoureiro.GetAsync("/api/Lancamentos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tesoureiro.GetAsync("/api/Me")).StatusCode);
        var encerradas = await Factory.Services.GetRequiredService<Almirante.Api.Services.MonitorSessoesNotificacoes>().VerificarAsync(CancellationToken.None);
        Assert.True(encerradas >= 1);
        await fechada.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var reconexao = NotificacoesTestKit.Hub(Factory, token);
        await Assert.ThrowsAnyAsync<Exception>(() => reconexao.StartAsync());

        using var novo = await LoginAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await novo.GetAsync("/api/Lancamentos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await novo.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal("DS", (string)JsonNode.Parse(await novo.GetStringAsync("/api/Me"))!["cargo"]!["role"]!);
        await using var hubNovo = NotificacoesTestKit.Hub(Factory, NotificacoesTestKit.Token(novo));
        await hubNovo.StartAsync();
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, hubNovo.State);
    }

    [Fact]
    public async Task Membros_NoSqlServer_ListaSoAtivos_EmOrdemDeNome()
    {
        var (tesoureiro, _) = await UsuarioAsync("TES");
        var ativo = await TestHelpers.AddUsuarioAsync(Factory, $"Aaa Ativo {Guid.NewGuid():N}", "DS");
        var inativo = await TestHelpers.AddUsuarioAsync(Factory, $"Aaa Inativo {Guid.NewGuid():N}", "DS");
        await Db(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={inativo.Id};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = {inativo.Id};
            """));

        var itens = (await tesoureiro.GetFromJsonAsync<JsonArray>("/api/Membros"))!;

        var ids = itens.Select(i => Guid.Parse((string)i!["id"]!)).ToList();
        Assert.Contains(ativo.Id, ids);
        Assert.DoesNotContain(inativo.Id, ids);
        var nomes = itens.Select(i => (string)i!["nome"]!).ToList();
        Assert.Equal(nomes.Order(StringComparer.Create(new System.Globalization.CultureInfo("pt-BR"), ignoreCase: true)), nomes);
    }
}
