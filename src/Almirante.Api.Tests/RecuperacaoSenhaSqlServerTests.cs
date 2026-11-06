using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Almirante.Api.Tests.SenhasTestKit;

namespace Almirante.Api.Tests;

/// <summary>Recuperação sem login (issue #159) contra SQL Server real: pedido público, atendimento assistido, token de uso único.</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class RecuperacaoSenhaSqlServerTests : IAsyncLifetime
{
    private readonly GatilhoComandoSql _gatilho = new();
    private SqlServerApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory(SemLimites(), configureDb: o => o.AddInterceptors(_gatilho));
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> acao) => TestHelpers.WithDbAsync(_factory, acao);

    private async Task SqlAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(_factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        await comando.ExecuteNonQueryAsync();
    }

    private Task<List<RecuperacaoSenha>> RecuperacoesAsync(Guid usuarioId) =>
        Db(db => db.RecuperacoesSenha.AsNoTracking().Where(r => r.UsuarioId == usuarioId).ToListAsync());

    [Fact]
    public async Task Solicitar_RespostaGenericaIgual_ParaQualquerConta_SemTokenSemEfeitosNaCredencial()
    {
        var membro = await ContaAsync(_factory, "DS");
        var inativo = await ContaAsync(_factory, "DS", ativo: false);
        var admin = await ContaAsync(_factory, "ADM");
        using var sessaoMembro = await LoginAsync(_factory, membro);
        var antes = await EstadoAsync(_factory, membro.Id);
        using var anonimo = await AnonimoAsync(_factory, ip: "192.0.2.44");

        var corpos = new List<string>();
        foreach (var email in new[] { membro.Email, membro.Email.ToUpperInvariant(), $"ninguem-{Guid.NewGuid():N}@local.dev", inativo.Email, admin.Email, membro.Email })
        {
            using var resposta = await SolicitarAsync(anonimo, email);
            Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
            AssertNoStore(resposta);
            corpos.Add(await resposta.Content.ReadAsStringAsync());
        }

        Assert.Single(corpos.Distinct());
        Assert.Equal(RecuperacaoSenhaSolicitadaResponse.MensagemPadrao, JsonDocument.Parse(corpos[0]).RootElement.GetProperty("mensagem").GetString());
        Assert.Equal(["mensagem"], JsonDocument.Parse(corpos[0]).RootElement.EnumerateObject().Select(p => p.Name));
        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.Equal((StatusRecuperacaoSenha.Pendente, OrigemRecuperacaoSenha.SolicitacaoPublica, 3, "192.0.2.44", (byte[]?)null),
            (registro.Status, registro.Origem, registro.Solicitacoes, registro.IpSolicitacao, registro.TokenHash));
        Assert.InRange(registro.ExpiraEmUtc, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));
        Assert.Empty(await RecuperacoesAsync(inativo.Id));
        Assert.Empty(await RecuperacoesAsync(admin.Id));
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, sessaoMembro.AccessToken));
        Assert.Equal(0, await Db(db => db.Notificacoes.CountAsync()));
    }

    [Fact]
    public async Task Solicitar_DadosCadastraisNaoSaoAceitos_NemComoProva()
    {
        var membro = await ContaAsync(_factory, "DS");
        using var anonimo = await AnonimoAsync(_factory);

        using var resposta = await anonimo.PostAsJsonAsync("/api/Auth/recuperacao-senha/solicitar",
            new { email = membro.Email, cpf = TestHelpers.NovoCpf(), dataNascimento = "2012-01-01", telefone = "11999998888", novaSenha = NovaSenha() });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Empty(await RecuperacoesAsync(membro.Id));
    }

    [Fact]
    public async Task Solicitar_PedidosParalelos_DeduplicamNumUnicoRegistroAberto()
    {
        var membro = await ContaAsync(_factory, "TES");
        using var anonimo = await AnonimoAsync(_factory);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SolicitarAsync(anonimo, membro.Email)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.InRange(registro.Solicitacoes, 1, 10);
    }

    [Fact]
    public async Task Solicitar_PedidoExpirado_EhEncerradoEUmNovoEhAberto()
    {
        var membro = await ContaAsync(_factory, "DS");
        using var anonimo = await AnonimoAsync(_factory);
        await SolicitarAsync(anonimo, membro.Email);
        await SqlAsync("UPDATE dbo.Usuarios_recuperacoes_senha SET ExpiraEmUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE UsuarioId = @u", ("@u", membro.Id));

        await SolicitarAsync(anonimo, membro.Email);

        var registros = await RecuperacoesAsync(membro.Id);
        Assert.Equal(2, registros.Count);
        Assert.Single(registros, r => r is { Status: StatusRecuperacaoSenha.Invalidada, MotivoEncerramento: RecuperacaoSenhaService.EncerramentoExpirada });
        Assert.Single(registros, r => r.Status == StatusRecuperacaoSenha.Pendente);
    }

    [Fact]
    public async Task Pendentes_FiltradasPelaMatriz_Paginadas_SemTokenIpOuContato()
    {
        var membro = await ContaAsync(_factory, "DS");
        var tesoureiro = await ContaAsync(_factory, "TES");
        var secretario = await ContaAsync(_factory, "SEC");
        using var anonimo = await AnonimoAsync(_factory, ip: "192.0.2.77");
        foreach (var conta in new[] { secretario, tesoureiro, membro }) await SolicitarAsync(anonimo, conta.Email);
        var adminConta = await ContaAsync(_factory, "ADM", NovaSenha());
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var diraConta = await ContaAsync(_factory, "DIRA", NovaSenha());
        using var admin = await LoginAsync(_factory, adminConta);
        using var sec = await LoginAsync(_factory, secConta);
        using var dira = await LoginAsync(_factory, diraConta);
        var proprios = new[] { membro.Id, tesoureiro.Id, secretario.Id };

        var vistosPeloAdmin = new List<RecuperacaoSenhaPendenteDto>();
        string? cursor = null;
        do
        {
            using var pagina = await admin.Client.GetAsync($"/api/Usuarios/recuperacao-senha/pendentes?pageSize=1{(cursor is null ? "" : $"&cursor={cursor}")}");
            Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
            var texto = await pagina.Content.ReadAsStringAsync();
            Assert.DoesNotContain("token", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("192.0.2.77", texto);
            Assert.DoesNotContain("@local.dev", texto);
            var corpo = JsonSerializer.Deserialize<RecuperacoesSenhaPendentesResponse>(texto, NotificacoesTestKit.Json)!;
            Assert.True(corpo.Items.Count <= 1);
            vistosPeloAdmin.AddRange(corpo.Items);
            cursor = corpo.ProximoCursor;
        } while (cursor is not null);

        Assert.Equal(proprios.OrderBy(x => x), vistosPeloAdmin.Where(i => proprios.Contains(i.UsuarioId)).Select(i => i.UsuarioId).OrderBy(x => x));
        var doSecretario = (await sec.Client.GetFromJsonAsync<RecuperacoesSenhaPendentesResponse>(
            "/api/Usuarios/recuperacao-senha/pendentes?pageSize=50", NotificacoesTestKit.Json))!.Items;
        Assert.All(doSecretario, i => Assert.Equal("DS", i.Cargo.Role));
        Assert.Contains(doSecretario, i => i.UsuarioId == membro.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await dira.Client.GetAsync("/api/Usuarios/recuperacao-senha/pendentes")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/api/Usuarios/recuperacao-senha/pendentes?cursor=x")).StatusCode);
    }

    [Fact]
    public async Task Pendentes_CargoDoResponsavelMudaDepoisDaValidacaoDoToken_403()
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        using var sec = await LoginAsync(_factory, secConta);
        var cargoTes = await Db(db => db.Cargos.Where(c => c.Role == "TES").Select(c => c.Id).SingleAsync());
        _gatilho.Armar("[u].[SenhaHash]", () => SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET CargoId = @c WHERE Id = @u;
            """, ("@u", secConta.Id), ("@c", cargoTes)));

        using var resposta = await sec.Client.GetAsync("/api/Usuarios/recuperacao-senha/pendentes");

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Emissao_ExigeSenhaDoResponsavelEMetodo_GuardaSoOHash_ETokenNaoReaparece()
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sec = await LoginAsync(_factory, secConta, ip: "198.51.100.20");
        using var anonimo = await AnonimoAsync(_factory);
        await SolicitarAsync(anonimo, membro.Email);

        using (var senhaErrada = await EmitirAsync(sec.Client, membro.Id, "senha-errada-do-secretario"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, senhaErrada.StatusCode);
            Assert.Equal(["senhaAtualSolicitante"], await CamposComErroAsync(senhaErrada));
        }
        using (var semMetodo = await EmitirAsync(sec.Client, membro.Id, secConta.Senha, metodo: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, semMetodo.StatusCode);
            Assert.Equal(["metodoVerificacao"], await CamposComErroAsync(semMetodo));
        }
        Assert.Equal(StatusRecuperacaoSenha.Pendente, Assert.Single(await RecuperacoesAsync(membro.Id)).Status);

        using var resposta = await EmitirAsync(sec.Client, membro.Id, secConta.Senha, "ProcedimentoIndependenteEstabelecido");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        AssertNoStore(resposta);
        var emitida = (await resposta.Content.ReadFromJsonAsync<AutorizacaoRecuperacaoSenhaResponse>(NotificacoesTestKit.Json))!;
        Assert.Equal(TokensRecuperacaoSenha.Caracteres, emitida.Token.Length);
        Assert.InRange(emitida.ExpiraEmUtc, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));

        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.True(TokensRecuperacaoSenha.TentarCalcularHash(emitida.Token, out var hash));
        Assert.Equal(hash, registro.TokenHash);
        Assert.NotEqual(SHA256.HashData(Convert.FromBase64String(emitida.Token.Replace('-', '+').Replace('_', '/') + "=")), registro.TokenHash);
        Assert.Equal((StatusRecuperacaoSenha.Autorizada, secConta.Id, "198.51.100.20", (MetodoVerificacaoIdentidade?)MetodoVerificacaoIdentidade.ProcedimentoIndependenteEstabelecido, emitida.RecuperacaoId),
            (registro.Status, registro.UsuarioAutorizadorId!.Value, registro.IpAutorizador, registro.MetodoVerificacao, registro.Id));
        Assert.Equal((await EstadoAsync(_factory, membro.Id)).SecurityVersion, registro.SecurityVersion);

        var listagem = await sec.Client.GetStringAsync("/api/Usuarios/recuperacao-senha/pendentes");
        Assert.DoesNotContain(emitida.Token, listagem);
        Assert.DoesNotContain(emitida.Token, string.Join("\n", _factory.Logs.Entries));

        // Uma nova emissão substitui a anterior: o token antigo deixa de valer.
        var segunda = await EmitirOkAsync(sec.Client, membro.Id, secConta.Senha);
        Assert.Equal(emitida.RecuperacaoId, segunda.RecuperacaoId);
        using var antigo = await ConfirmarAsync(anonimo, emitida.Token, NovaSenha());
        Assert.Equal(HttpStatusCode.BadRequest, antigo.StatusCode);
    }

    [Theory]
    [InlineData("SEC", "TES", false)]
    [InlineData("SEC", "DS", true)]
    [InlineData("DIR", "SEC", true)]
    [InlineData("ADM", "DIR", false)]
    [InlineData("ADM", "TES", true)]
    public async Task Emissao_RespeitaAMatriz(string solicitanteRole, string alvoRole, bool permitido)
    {
        var solicitante = await ContaAsync(_factory, solicitanteRole, NovaSenha());
        var alvo = await ContaAsync(_factory, alvoRole);
        using var sessao = await LoginAsync(_factory, solicitante);

        using var resposta = await EmitirAsync(sessao.Client, alvo.Id, solicitante.Senha);

        Assert.Equal(permitido ? HttpStatusCode.OK : HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(permitido ? 1 : 0, (await RecuperacoesAsync(alvo.Id)).Count);
    }

    /// <summary>Outra operação muda a recuperação entre a validação do token e a trava: a conclusão relê a linha travada e recusa.</summary>
    [Theory]
    [InlineData("Status = 4, EncerradaEmUtc = SYSUTCDATETIME(), MotivoEncerramento = 'Concluida em paralelo'")]
    [InlineData("TokenExpiraEmUtc = DATEADD(minute, -1, SYSUTCDATETIME())")]
    [InlineData("SecurityVersion = SecurityVersion + 1")]
    [InlineData("TokenHash = CRYPT_GEN_RANDOM(32)")]
    public async Task Confirmar_RecuperacaoAlteradaEntreAValidacaoEATrava_RecusaSemTrocarASenha(string alteracao)
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sec = await LoginAsync(_factory, secConta);
        using var anonimo = await AnonimoAsync(_factory);
        await SolicitarAsync(anonimo, membro.Email);
        var emitida = await EmitirOkAsync(sec.Client, membro.Id, secConta.Senha);
        var antes = await EstadoAsync(_factory, membro.Id);
        _gatilho.Armar("[Usuarios_recuperacoes_senha] WITH (UPDLOCK, ROWLOCK) WHERE [Id]", () => SqlAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios_recuperacoes_senha SET {alteracao} WHERE Id = @r;
            """, ("@u", secConta.Id), ("@r", emitida.RecuperacaoId)));

        using var resposta = await ConfirmarAsync(anonimo, emitida.Token, NovaSenha());

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Autorização de recuperação inválida ou expirada.", await TituloAsync(resposta));
        Assert.Equal(antes.SecurityVersion, (await EstadoAsync(_factory, membro.Id)).SecurityVersion);
    }

    [Fact]
    public async Task Confirmar_ConcluiSemLoginESemSenhaAntiga_RevogaSessoes_AuditaAutorizadorEIp_ENotifica()
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessaoAntiga = await LoginAsync(_factory, membro);
        using var sec = await LoginAsync(_factory, secConta, ip: "198.51.100.30");
        using var anonimo = await AnonimoAsync(_factory, ip: "192.0.2.90");
        await SolicitarAsync(anonimo, membro.Email);
        var emitida = await EmitirOkAsync(sec.Client, membro.Id, secConta.Senha);
        var antes = await EstadoAsync(_factory, membro.Id);
        var nova = NovaSenha();

        using var resposta = await ConfirmarAsync(anonimo, emitida.Token, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        AssertNoStore(resposta);
        Assert.False(resposta.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory, sessaoAntiga.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshComCookieAsync(_factory, sessaoAntiga.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(_factory, membro.Email, membro.Senha));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, membro.Email, nova));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, sec.AccessToken));

        var depois = await EstadoAsync(_factory, membro.Id);
        Assert.Equal((antes.SecurityVersion + 1, 1, 1), (depois.SecurityVersion, depois.Auditorias, depois.Notificacoes));
        var auditoria = await Db(db => db.AlteracoesSenha.AsNoTracking().SingleAsync(a => a.UsuarioAfetadoId == membro.Id));
        Assert.Equal((TipoAlteracaoSenha.RecuperacaoAssistida, secConta.Id, "192.0.2.90", emitida.RecuperacaoId),
            (auditoria.TipoAlteracao, auditoria.UsuarioResponsavelId!.Value, auditoria.IpResponsavel, auditoria.SolicitacaoRecuperacaoId!.Value));
        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.Equal((StatusRecuperacaoSenha.Concluida, AlteracaoSenhaService.EncerramentoTokenConsumido, "192.0.2.90", "198.51.100.30"),
            (registro.Status, registro.MotivoEncerramento, registro.IpConclusao, registro.IpAutorizador));
        var notificacao = await Db(db => db.Notificacoes.AsNoTracking().SingleAsync(n => n.DestinatarioId == membro.Id));
        Assert.Equal(TipoNotificacao.SenhaAlterada, notificacao.Tipo);
        Assert.Contains("autorização de recuperação", notificacao.Mensagem);

        using var reuso = await ConfirmarAsync(anonimo, emitida.Token, NovaSenha());
        Assert.Equal(HttpStatusCode.BadRequest, reuso.StatusCode);
        Assert.Equal("Autorização de recuperação inválida ou expirada.", await TituloAsync(reuso));
        Assert.Equal(depois, await EstadoAsync(_factory, membro.Id));
    }

    [Fact]
    public async Task Confirmar_TokenInvalidoExpiradoRevogadoOuObsoleto_RespostaGenericaSemAlteracao()
    {
        var adminConta = await ContaAsync(_factory, "ADM", NovaSenha());
        using var admin = await LoginAsync(_factory, adminConta);
        using var anonimo = await AnonimoAsync(_factory);
        var titulos = new List<string>();

        async Task RecusadoAsync(string token, Guid alvoId)
        {
            var antes = await EstadoAsync(_factory, alvoId);
            using var resposta = await ConfirmarAsync(anonimo, token, NovaSenha());
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            titulos.Add(await TituloAsync(resposta));
            Assert.Equal(antes, await EstadoAsync(_factory, alvoId));
        }

        var alvo = await ContaAsync(_factory, "TES");
        await RecusadoAsync(Base64UrlAleatorio(), alvo.Id);
        await RecusadoAsync("curto", alvo.Id);
        await RecusadoAsync(new string('*', 43), alvo.Id);

        var expirada = await EmitirOkAsync(admin.Client, alvo.Id, adminConta.Senha);
        await SqlAsync("UPDATE dbo.Usuarios_recuperacoes_senha SET TokenExpiraEmUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE Id = @id", ("@id", expirada.RecuperacaoId));
        await RecusadoAsync(expirada.Token, alvo.Id);

        var revogadoAlvo = await ContaAsync(_factory, "DS");
        var revogada = await EmitirOkAsync(admin.Client, revogadoAlvo.Id, adminConta.Senha);
        await EmitirOkAsync(admin.Client, revogadoAlvo.Id, adminConta.Senha);
        await RecusadoAsync(revogada.Token, revogadoAlvo.Id);

        // Senha trocada depois da emissão: SecurityVersion mudou e a autorização aberta foi encerrada pela troca.
        var obsoletoAlvo = await ContaAsync(_factory, "SEC");
        var obsoleta = await EmitirOkAsync(admin.Client, obsoletoAlvo.Id, adminConta.Senha);
        Assert.Equal(HttpStatusCode.NoContent, (await AlterarDeUsuarioAsync(admin.Client, obsoletoAlvo.Id, adminConta.Senha, NovaSenha())).StatusCode);
        Assert.Equal(StatusRecuperacaoSenha.Concluida, Assert.Single(await RecuperacoesAsync(obsoletoAlvo.Id)).Status);
        await RecusadoAsync(obsoleta.Token, obsoletoAlvo.Id);

        // Mesmo reaberta à mão, a autorização com a versão antiga não vale.
        await SqlAsync("UPDATE dbo.Usuarios_recuperacoes_senha SET Status = 2, EncerradaEmUtc = NULL, MotivoEncerramento = NULL WHERE Id = @id",
            ("@id", obsoleta.RecuperacaoId));
        await RecusadoAsync(obsoleta.Token, obsoletoAlvo.Id);

        Assert.Single(titulos.Distinct());
    }

    [Fact]
    public async Task Confirmar_AutorizadorPerdeuAPermissao_RecusaEInvalidaAAutorizacao()
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sec = await LoginAsync(_factory, secConta);
        var emitida = await EmitirOkAsync(sec.Client, membro.Id, secConta.Senha);
        var cargoTes = await Db(db => db.Cargos.Where(c => c.Role == "TES").Select(c => c.Id).SingleAsync());
        await SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET CargoId = @c WHERE Id = @u;
            """, ("@u", secConta.Id), ("@c", cargoTes));
        using var anonimo = await AnonimoAsync(_factory);
        var antes = await EstadoAsync(_factory, membro.Id);

        using var resposta = await ConfirmarAsync(anonimo, emitida.Token, NovaSenha());

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.Equal((StatusRecuperacaoSenha.Invalidada, AlteracaoSenhaService.EncerramentoAutorizadorSemPermissao), (registro.Status, registro.MotivoEncerramento));
    }

    [Fact]
    public async Task Confirmar_TitularDesativadoDepoisDaEmissao_RespostaGenerica()
    {
        var adminConta = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "TES");
        using var admin = await LoginAsync(_factory, adminConta);
        var emitida = await EmitirOkAsync(admin.Client, membro.Id, adminConta.Senha);
        await SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@r;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = @u;
            """, ("@r", adminConta.Id), ("@u", membro.Id));
        var antes = await EstadoAsync(_factory, membro.Id);
        using var anonimo = await AnonimoAsync(_factory);

        using var resposta = await ConfirmarAsync(anonimo, emitida.Token, NovaSenha());

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("Autorização de recuperação inválida ou expirada.", await TituloAsync(resposta));
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
    }

    [Fact]
    public async Task Emissao_EncerraPedidoExpirado_EEstendeValidadeDePedidoQuaseVencido()
    {
        var dirConta = await ContaAsync(_factory, "DIR", NovaSenha());
        var expirado = await ContaAsync(_factory, "DS");
        var quaseVencido = await ContaAsync(_factory, "SEC");
        using var dir = await LoginAsync(_factory, dirConta);
        using var anonimo = await AnonimoAsync(_factory);
        await SolicitarAsync(anonimo, expirado.Email);
        await SolicitarAsync(anonimo, quaseVencido.Email);
        await SqlAsync("UPDATE dbo.Usuarios_recuperacoes_senha SET ExpiraEmUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE UsuarioId = @u", ("@u", expirado.Id));
        await SqlAsync("UPDATE dbo.Usuarios_recuperacoes_senha SET ExpiraEmUtc = DATEADD(MINUTE, 2, SYSUTCDATETIME()) WHERE UsuarioId = @u", ("@u", quaseVencido.Id));

        var novaAutorizacao = await EmitirOkAsync(dir.Client, expirado.Id, dirConta.Senha);
        var estendida = await EmitirOkAsync(dir.Client, quaseVencido.Id, dirConta.Senha);

        var doExpirado = await RecuperacoesAsync(expirado.Id);
        Assert.Single(doExpirado, r => r is { Status: StatusRecuperacaoSenha.Invalidada, MotivoEncerramento: RecuperacaoSenhaService.EncerramentoExpirada });
        var atendimento = Assert.Single(doExpirado, r => r.Status == StatusRecuperacaoSenha.Autorizada);
        Assert.Equal((novaAutorizacao.RecuperacaoId, OrigemRecuperacaoSenha.Atendimento, (string?)null), (atendimento.Id, atendimento.Origem, atendimento.IpSolicitacao));
        var registro = Assert.Single(await RecuperacoesAsync(quaseVencido.Id));
        Assert.Equal(OrigemRecuperacaoSenha.SolicitacaoPublica, registro.Origem);
        Assert.InRange(registro.ExpiraEmUtc, estendida.ExpiraEmUtc.AddMilliseconds(-1), estendida.ExpiraEmUtc.AddMilliseconds(1));
    }

    [Fact]
    public async Task Confirmar_ConsumoParalelo_SoUmaConclusao()
    {
        var dirConta = await ContaAsync(_factory, "DIR", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var dir = await LoginAsync(_factory, dirConta);
        var emitida = await EmitirOkAsync(dir.Client, membro.Id, dirConta.Senha);
        var antes = await EstadoAsync(_factory, membro.Id);
        var clientes = new List<HttpClient>();
        for (var i = 0; i < 8; i++) clientes.Add(await AnonimoAsync(_factory));

        var respostas = await Task.WhenAll(clientes.Select(c => ConfirmarAsync(c, emitida.Token, NovaSenha())));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, respostas.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        var depois = await EstadoAsync(_factory, membro.Id);
        Assert.Equal((antes.SecurityVersion + 1, 1, 1), (depois.SecurityVersion, depois.Auditorias, depois.Notificacoes));
        clientes.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task TrocaAdministrativa_ConcluiPedidoAberto_ComMetodoRegistrado_ETrocaPropriaInvalidaAutorizacao()
    {
        var secConta = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sec = await LoginAsync(_factory, secConta, ip: "198.51.100.40");
        using var anonimo = await AnonimoAsync(_factory);
        await SolicitarAsync(anonimo, membro.Email);
        var registro = Assert.Single(await RecuperacoesAsync(membro.Id));

        using var troca = await AlterarDeUsuarioAsync(sec.Client, membro.Id, secConta.Senha, NovaSenha(), metodo: "AtendimentoPresencial");

        Assert.Equal(HttpStatusCode.NoContent, troca.StatusCode);
        var concluido = Assert.Single(await RecuperacoesAsync(membro.Id));
        Assert.Equal((StatusRecuperacaoSenha.Concluida, AlteracaoSenhaService.EncerramentoAlteracaoAdministrativa, secConta.Id, "198.51.100.40",
                (MetodoVerificacaoIdentidade?)MetodoVerificacaoIdentidade.AtendimentoPresencial),
            (concluido.Status, concluido.MotivoEncerramento, concluido.UsuarioAutorizadorId!.Value, concluido.IpAutorizador, concluido.MetodoVerificacao));
        Assert.Equal(registro.Id, await Db(db => db.AlteracoesSenha.Where(a => a.UsuarioAfetadoId == membro.Id).Select(a => a.SolicitacaoRecuperacaoId).SingleAsync()));

        var outro = await ContaAsync(_factory, "DS", NovaSenha());
        using var sessaoOutro = await LoginAsync(_factory, outro);
        var emitida = await EmitirOkAsync(sec.Client, outro.Id, secConta.Senha);
        Assert.Equal(HttpStatusCode.NoContent, (await AlterarPropriaAsync(sessaoOutro.Client, outro.Senha, NovaSenha())).StatusCode);
        var invalidada = Assert.Single(await RecuperacoesAsync(outro.Id));
        Assert.Equal((StatusRecuperacaoSenha.Invalidada, AlteracaoSenhaService.EncerramentoSenhaAlterada), (invalidada.Status, invalidada.MotivoEncerramento));
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmarAsync(anonimo, emitida.Token, NovaSenha())).StatusCode);
        Assert.Null(await Db(db => db.AlteracoesSenha.Where(a => a.UsuarioAfetadoId == outro.Id).Select(a => a.SolicitacaoRecuperacaoId).SingleAsync()));
    }

    [Fact]
    public async Task RecuperacaoAutomaticaEEmail_Desabilitados_NenhumaEntregaRegistrada_NemTokenNoPedidoPublico()
    {
        Assert.Null(_factory.Services.GetService<IEntregaTokenRecuperacaoSenha>());
        Assert.Null(_factory.Services.GetService<IAvisoSenhaAlteradaEmail>());
        var membro = await ContaAsync(_factory, "DS");
        using var anonimo = await AnonimoAsync(_factory);

        using var resposta = await SolicitarAsync(anonimo, membro.Email);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        Assert.Null(Assert.Single(await RecuperacoesAsync(membro.Id)).TokenHash);
        Assert.DoesNotContain("token", await resposta.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Base64UrlAleatorio() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
