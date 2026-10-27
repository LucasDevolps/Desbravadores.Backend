using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Almirante.Api.Tests.SenhasTestKit;

namespace Almirante.Api.Tests;

/// <summary>Troca própria e administrativa (issue #159) contra SQL Server real: efeitos, matriz, recusas, concorrência, rollback e logs.</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AlteracaoSenhaSqlServerTests : IAsyncLifetime
{
    private static readonly string[] Cargos = ["ADM", "DIR", "DIRA", "SEC", "TES", "DS", "CONS"];

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

    private async Task<int> SqlAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(_factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    private Task<Guid> CargoIdAsync(string role) => Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    [Fact]
    public async Task Propria_AlteraCredencial_RevogaAccessERefresh_RemoveCookie_AuditaSemNotificar()
    {
        var conta = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, conta, ip: "203.0.113.10");
        using var outra = await LoginAsync(_factory, conta);
        var antes = await EstadoAsync(_factory, conta.Id);
        var nova = NovaSenha();
        var inicio = DateTime.UtcNow.AddSeconds(-5);

        using var resposta = await AlterarPropriaAsync(sessao.Client, conta.Senha, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        AssertNoStore(resposta);
        Assert.Contains(resposta.Headers.GetValues("Set-Cookie"), c => c.StartsWith(RefreshCookie + "=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970"));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory, sessao.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory, outra.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshComCookieAsync(_factory, sessao.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshComCookieAsync(_factory, outra.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(_factory, conta.Email, conta.Senha));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, conta.Email, nova));

        var depois = await EstadoAsync(_factory, conta.Id);
        Assert.Equal(antes.SecurityVersion + 1, depois.SecurityVersion);
        Assert.Equal((1, 0), (depois.Auditorias, depois.Notificacoes));
        var auditoria = await Db(db => db.AlteracoesSenha.AsNoTracking().SingleAsync(a => a.UsuarioAfetadoId == conta.Id));
        Assert.Equal((TipoAlteracaoSenha.Propria, conta.Id, "203.0.113.10", (Guid?)null),
            (auditoria.TipoAlteracao, auditoria.UsuarioResponsavelId!.Value, auditoria.IpResponsavel, auditoria.SolicitacaoRecuperacaoId));
        Assert.Equal(DateTimeKind.Utc, auditoria.AlteradaEmUtc.Kind);
        Assert.InRange(auditoria.AlteradaEmUtc, inicio, DateTime.UtcNow.AddSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(auditoria.TraceId));
        Assert.Equal(PasswordPolicy.IteracoesHash, IteracoesDoHash(depois.SenhaHash));
        Assert.Equal(2, await Db(db => db.AuthSessions.CountAsync(s => s.UsuarioId == conta.Id && s.RevocationReason == AlteracaoSenhaService.MotivoRevogacao)));
        Assert.Equal(0, await Db(db => db.UsuariosHistorico.CountAsync(h => h.UsuarioId == conta.Id)));
    }

    [Theory]
    [InlineData("ADM")]
    [InlineData("DIR")]
    [InlineData("DIRA")]
    [InlineData("SEC")]
    [InlineData("TES")]
    [InlineData("DS")]
    [InlineData("CONS")]
    public async Task Propria_QualquerPerfilAutenticado_TrocaASenhaComASenhaAtual(string role)
    {
        var conta = await ContaAsync(_factory, role, NovaSenha());
        using var sessao = await LoginAsync(_factory, conta);
        var nova = NovaSenha();

        using var resposta = await AlterarPropriaAsync(sessao.Client, conta.Senha, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, conta.Email, nova));
    }

    [Fact]
    public async Task Propria_EntradasRecusadas_NaoAlteramCredencialSessoesAuditoriaNemNotificacoes()
    {
        var conta = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, conta);
        var antes = await EstadoAsync(_factory, conta.Id);
        var nova = NovaSenha();

        var casos = new (Func<Task<HttpResponseMessage>> Chamada, string Campo)[]
        {
            (() => AlterarPropriaAsync(sessao.Client, "senha-errada-do-titular", nova), "senhaAtualSolicitante"),
            (() => AlterarPropriaAsync(sessao.Client, conta.Senha, nova, nova.ToUpperInvariant()), "confirmacaoNovaSenha"),
            (() => AlterarPropriaAsync(sessao.Client, conta.Senha, nova, nova + " "), "confirmacaoNovaSenha"),
            (() => AlterarPropriaAsync(sessao.Client, conta.Senha, "Curta-9x"), "novaSenha"),
            (() => AlterarPropriaAsync(sessao.Client, conta.Senha, "senhasenhasenha"), "novaSenha"),
            (() => AlterarPropriaAsync(sessao.Client, conta.Senha, $"{conta.Email.Split('@')[0]}-Forte-2026"), "novaSenha"),
            (() => AlterarPropriaAsync(sessao.Client, "", nova), "senhaAtualSolicitante"),
            (() => sessao.Client.PostAsJsonAsync("/api/Auth/alterar-senha", new { novaSenha = nova }), "senhaAtualSolicitante"),
        };
        foreach (var (chamada, campo) in casos)
        {
            using var resposta = await chamada();
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            AssertNoStore(resposta);
            Assert.Contains(campo, await CamposComErroAsync(resposta));
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.DoesNotContain(nova, corpo);
            Assert.DoesNotContain(conta.Senha, corpo);
        }

        Assert.Equal(antes, await EstadoAsync(_factory, conta.Id));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, sessao.AccessToken));
    }

    [Fact]
    public async Task Propria_NovaSenhaIgualAAtual_EhRecusadaPorVerificacaoDoHash()
    {
        var conta = await ContaAsync(_factory, "TES", NovaSenha());
        using var sessao = await LoginAsync(_factory, conta);
        var antes = await EstadoAsync(_factory, conta.Id);

        using var resposta = await AlterarPropriaAsync(sessao.Client, conta.Senha, conta.Senha);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(["novaSenha"], await CamposComErroAsync(resposta));
        Assert.Equal(antes, await EstadoAsync(_factory, conta.Id));
    }

    [Fact]
    public async Task Administrativa_ResponsavelTrocaSemSenhaAntigaDoAfetado_PreservaSuaSessao_ENotificaSoOAfetado()
    {
        var secretario = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var responsavel = await LoginAsync(_factory, secretario, ip: "198.51.100.7");
        using var afetado = await LoginAsync(_factory, membro);
        var nova = NovaSenha();

        using var resposta = await AlterarDeUsuarioAsync(responsavel.Client, membro.Id, secretario.Senha, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        AssertNoStore(resposta);
        Assert.False(resposta.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory, afetado.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshComCookieAsync(_factory, afetado.Refresh));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, responsavel.AccessToken));
        Assert.Equal(HttpStatusCode.OK, (await responsavel.Client.GetAsync("/api/Usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await RefreshComCookieAsync(_factory, responsavel.Refresh));

        var auditoria = await Db(db => db.AlteracoesSenha.AsNoTracking().SingleAsync(a => a.UsuarioAfetadoId == membro.Id));
        Assert.Equal((TipoAlteracaoSenha.Administrativa, secretario.Id, "198.51.100.7"),
            (auditoria.TipoAlteracao, auditoria.UsuarioResponsavelId!.Value, auditoria.IpResponsavel));
        var notificacao = await Db(db => db.Notificacoes.AsNoTracking().SingleAsync(n => n.DestinatarioId == membro.Id));
        // O aviso pendente gravado na transação é consumido pelo publicador logo depois do commit (sem conexões abertas, nada a emitir).
        Assert.Equal((TipoNotificacao.SenhaAlterada, NotificacoesFabrica.ChaveSenhaAlterada(auditoria.Id), (DateTime?)null),
            (notificacao.Tipo, notificacao.ChaveOrigem, notificacao.LidaEmUtc));
        Assert.DoesNotContain(membro.Email, notificacao.Mensagem);
        Assert.DoesNotContain("198.51.100.7", notificacao.Mensagem);
        Assert.Equal(0, await Db(db => db.Notificacoes.CountAsync(n => n.DestinatarioId == secretario.Id)));
        Assert.Equal(1, await Db(db => db.AuthSessions.CountAsync(s => s.UsuarioId == secretario.Id && s.RevokedAtUtc == null)));

        // A notificação fica disponível depois do novo login do afetado e é lida pelo fluxo existente.
        using var novoLogin = await LoginAsync(_factory, membro with { Senha = nova });
        var pendentes = await novoLogin.Client.GetFromJsonAsync<NotificacoesPendentesResponse>("/api/Notificacoes/Pendentes", NotificacoesTestKit.Json);
        var item = Assert.Single(pendentes!.Items);
        Assert.Equal((notificacao.Id, TipoNotificacao.SenhaAlterada), (item.Id, item.Tipo));
        using var leitura = await novoLogin.Client.PostAsync($"/api/Notificacoes/{item.Id}/Leitura", null);
        Assert.Equal(HttpStatusCode.OK, leitura.StatusCode);
        Assert.NotNull(await Db(db => db.Notificacoes.Where(n => n.Id == item.Id).Select(n => n.LidaEmUtc).SingleAsync()));
    }

    [Fact]
    public async Task Administrativa_SenhaDoAfetadoComoConfirmacao_OuSenhaErrada_SaoRecusadas_SemAlterarOAlvo()
    {
        var diretor = await ContaAsync(_factory, "DIR", NovaSenha());
        var tesoureiro = await ContaAsync(_factory, "TES", NovaSenha());
        using var responsavel = await LoginAsync(_factory, diretor);
        var antes = await EstadoAsync(_factory, tesoureiro.Id);

        foreach (var confirmacao in new[] { tesoureiro.Senha, "nao-e-a-senha-do-diretor" })
        {
            using var resposta = await AlterarDeUsuarioAsync(responsavel.Client, tesoureiro.Id, confirmacao, NovaSenha());
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.Equal(["senhaAtualSolicitante"], await CamposComErroAsync(resposta));
        }

        Assert.Equal(antes, await EstadoAsync(_factory, tesoureiro.Id));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, tesoureiro.Email, tesoureiro.Senha));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, responsavel.AccessToken));
    }

    /// <summary>Todos os pares solicitante × alvo: só ADM/DIR → SEC/TES/DS e SEC → DS passam; o resto é 403 sem nenhuma alteração.</summary>
    [Fact]
    public async Task Matriz_TodosOsPares_SoOsListadosAlteram()
    {
        var divergencias = new List<string>();
        var alvosPermitidos = new List<Guid>();
        foreach (var solicitanteRole in Cargos)
        {
            var solicitante = await ContaAsync(_factory, solicitanteRole, NovaSenha());
            using var sessao = await LoginAsync(_factory, solicitante);
            foreach (var alvoRole in Cargos)
            {
                var alvo = await ContaAsync(_factory, alvoRole);
                var antes = await EstadoAsync(_factory, alvo.Id);
                using var resposta = await AlterarDeUsuarioAsync(sessao.Client, alvo.Id, solicitante.Senha, NovaSenha());
                var esperado = PermissoesSenha.PodeAlterarSenhaDeTerceiro(solicitanteRole, alvoRole) ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden;
                if (esperado == HttpStatusCode.NoContent) alvosPermitidos.Add(alvo.Id);
                if (resposta.StatusCode != esperado)
                {
                    divergencias.Add($"{solicitanteRole} → {alvoRole}: esperado {(int)esperado}, obtido {(int)resposta.StatusCode}");
                }
                else if (esperado == HttpStatusCode.Forbidden && await EstadoAsync(_factory, alvo.Id) != antes)
                {
                    divergencias.Add($"{solicitanteRole} → {alvoRole}: 403 alterou o alvo");
                }
            }
        }

        Assert.Empty(divergencias);
        // ADM→SEC, ADM→TES, ADM→DS, DIR→SEC, DIR→TES, DIR→DS e SEC→DS: uma auditoria administrativa por par permitido.
        Assert.Equal(7, alvosPermitidos.Count);
        Assert.Equal(7, await Db(db => db.AlteracoesSenha.CountAsync(a => a.TipoAlteracao == TipoAlteracaoSenha.Administrativa
            && alvosPermitidos.Contains(a.UsuarioAfetadoId))));
    }

    [Fact]
    public async Task Administrativa_AlvoInexistente_404SoParaQuemAtende_SemPermissao403_ParaSiMesmo400()
    {
        var secretario = await ContaAsync(_factory, "SEC", NovaSenha());
        var tesoureiro = await ContaAsync(_factory, "TES", NovaSenha());
        var diretorAssociado = await ContaAsync(_factory, "DIRA", NovaSenha());
        using var sec = await LoginAsync(_factory, secretario);
        using var tes = await LoginAsync(_factory, tesoureiro);
        using var dira = await LoginAsync(_factory, diretorAssociado);
        var inexistente = Guid.NewGuid();

        using var naoEncontrado = await AlterarDeUsuarioAsync(sec.Client, inexistente, secretario.Senha, NovaSenha());
        Assert.Equal(HttpStatusCode.NotFound, naoEncontrado.StatusCode);
        AssertNoStore(naoEncontrado);
        foreach (var (cliente, senha) in new[] { (tes.Client, tesoureiro.Senha), (dira.Client, diretorAssociado.Senha) })
        {
            using var semPermissao = await AlterarDeUsuarioAsync(cliente, inexistente, senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Forbidden, semPermissao.StatusCode);
            AssertNoStore(semPermissao);
        }

        using var paraSi = await AlterarDeUsuarioAsync(sec.Client, secretario.Id, secretario.Senha, NovaSenha());
        Assert.Equal(HttpStatusCode.BadRequest, paraSi.StatusCode);
        Assert.Equal(["usuarioId"], await CamposComErroAsync(paraSi));
    }

    [Fact]
    public async Task Administrativa_AlvoOuCargoInativo_409_ResponsavelInativo_401()
    {
        var diretor = await ContaAsync(_factory, "DIR", NovaSenha());
        using var sessao = await LoginAsync(_factory, diretor);
        var inativo = await ContaAsync(_factory, "DS", ativo: false);
        using (var resposta = await AlterarDeUsuarioAsync(sessao.Client, inativo.Id, diretor.Senha, NovaSenha()))
        {
            Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        }

        var secretarioAlvo = await ContaAsync(_factory, "SEC");
        var cargoSec = await CargoIdAsync("SEC");
        await SqlAsync("UPDATE dbo.Cargos SET Ativo = 0 WHERE Id = @c", ("@c", cargoSec));
        try
        {
            using var resposta = await AlterarDeUsuarioAsync(sessao.Client, secretarioAlvo.Id, diretor.Senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        }
        finally
        {
            await SqlAsync("UPDATE dbo.Cargos SET Ativo = 1 WHERE Id = @c", ("@c", cargoSec));
        }

        var alvo = await ContaAsync(_factory, "DS");
        await SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@r;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET Ativo = 0, SecurityVersion = SecurityVersion + 1 WHERE Id = @r;
            """, ("@r", diretor.Id));
        using var desativado = await AlterarDeUsuarioAsync(sessao.Client, alvo.Id, diretor.Senha, NovaSenha());
        Assert.Equal(HttpStatusCode.Unauthorized, desativado.StatusCode);
        Assert.Equal(0, (await EstadoAsync(_factory, alvo.Id)).Auditorias);
    }

    [Fact]
    public async Task CamposInternosNoCorpo_SaoIgnorados_AlvoEResponsavelVemDaRotaEDoToken()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var diretor = await ContaAsync(_factory, "DIR", NovaSenha());
        var alvo = await ContaAsync(_factory, "DS");
        var outro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, admin);
        var antesOutro = await EstadoAsync(_factory, outro.Id);
        var nova = NovaSenha();

        using var resposta = await sessao.Client.PostAsJsonAsync($"/api/Usuarios/{alvo.Id}/alterar-senha", new
        {
            senhaAtualSolicitante = admin.Senha, novaSenha = nova, confirmacaoNovaSenha = nova, usuarioAfetadoId = outro.Id,
            contexto = new { usuarioId = diretor.Id, sessaoId = Guid.NewGuid(), ip = "10.9.9.9", traceId = "forjado" },
        });

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(antesOutro, await EstadoAsync(_factory, outro.Id));
        var auditoria = await Db(db => db.AlteracoesSenha.AsNoTracking().SingleAsync(a => a.UsuarioAfetadoId == alvo.Id));
        Assert.Equal(admin.Id, auditoria.UsuarioResponsavelId);
        Assert.NotEqual("10.9.9.9", auditoria.IpResponsavel);
        Assert.NotEqual("forjado", auditoria.TraceId);
    }

    [Fact]
    public async Task Csrf_AusenteOuDeOutraIdentidade_400_SemAlterar()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, admin);
        var antes = await EstadoAsync(_factory, membro.Id);

        sessao.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using (var semToken = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, semToken.StatusCode);
            AssertNoStore(semToken);
        }

        using var anonimo = await AnonimoAsync(_factory);
        sessao.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonimo.DefaultRequestHeaders.GetValues("X-CSRF-TOKEN").Single());
        using (var tokenAnonimo = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tokenAnonimo.StatusCode);
        }

        using var propria = await AlterarPropriaAsync(sessao.Client, admin.Senha, NovaSenha());
        Assert.Equal(HttpStatusCode.BadRequest, propria.StatusCode);
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, admin.Email, admin.Senha));
    }

    [Fact]
    public async Task Administrativa_ZeraBloqueioDeLoginDoAlvo_ENaoUsaCamposDeHistoricoCadastral()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        await SqlAsync("UPDATE dbo.Usuarios SET FalhasLoginConsecutivas = 10, UltimaFalhaLoginUtc = SYSUTCDATETIME(), LoginBloqueadoAteUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME()) WHERE Id = @u",
            ("@u", membro.Id));
        using var sessao = await LoginAsync(_factory, admin);
        var nova = NovaSenha();

        using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        var estado = await EstadoAsync(_factory, membro.Id);
        Assert.Equal((0, (DateTime?)null), (estado.Falhas, estado.Bloqueio));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, membro.Email, nova));
        Assert.Equal(0, await Db(db => db.UsuariosHistorico.CountAsync(h => h.UsuarioId == membro.Id)));
    }

    [Fact]
    public async Task CargoDoAlvoMudaAntesDaTrava_409_SemAlteracao()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, admin);
        var antes = await EstadoAsync(_factory, membro.Id);
        var cargoDiretor = await CargoIdAsync("DIR");
        _gatilho.Armar("INNER JOIN [dbo].[Cargos] c WITH (ROWLOCK, HOLDLOCK)", () => SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@r;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET CargoId = @c WHERE Id = @u;
            """, ("@r", admin.Id), ("@c", cargoDiretor), ("@u", membro.Id)));

        using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha());

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        var depois = await EstadoAsync(_factory, membro.Id);
        Assert.Equal((antes.SenhaHash, antes.SecurityVersion, 0, 0), (depois.SenhaHash, depois.SecurityVersion, depois.Auditorias, depois.Notificacoes));
    }

    [Fact]
    public async Task ResponsavelDesativadoAntesDaTrava_409_SemAlteracao()
    {
        var diretor = await ContaAsync(_factory, "DIR", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, diretor);
        var antes = await EstadoAsync(_factory, membro.Id);
        var cargoDiretor = await CargoIdAsync("DIR");
        _gatilho.Armar("INNER JOIN [dbo].[Cargos] c WITH (ROWLOCK, HOLDLOCK)",
            () => SqlAsync("UPDATE dbo.Cargos SET Ativo = 0 WHERE Id = @c", ("@c", cargoDiretor)));
        try
        {
            using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, diretor.Senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        }
        finally
        {
            await SqlAsync("UPDATE dbo.Cargos SET Ativo = 1 WHERE Id = @c", ("@c", cargoDiretor));
        }

        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
    }

    [Fact]
    public async Task SessaoDoResponsavelRevogadaAntesDaFotografiaOuDaTrava_401_SemAlteracao()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        var antes = await EstadoAsync(_factory, membro.Id);
        const string revogar = "UPDATE dbo.AuthSessions SET RevokedAtUtc = SYSUTCDATETIME(), RevocationReason = 'teste' WHERE UsuarioId = @u AND RevokedAtUtc IS NULL";

        // A 1ª consulta a AuthSessions é a validação do token; a 2ª é a fotografia do serviço.
        using (var sessao = await LoginAsync(_factory, admin))
        {
            _gatilho.Armar("FROM [AuthSessions]", () => SqlAsync(revogar, ("@u", admin.Id)), ignorar: 1);
            using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }

        using (var sessao = await LoginAsync(_factory, admin))
        {
            _gatilho.Armar("INNER JOIN [dbo].[Cargos] c WITH (ROWLOCK, HOLDLOCK)", () => SqlAsync(revogar, ("@u", admin.Id)));
            using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }

        // Responsável desativado entre a validação do token e a fotografia do serviço.
        using (var sessao = await LoginAsync(_factory, admin))
        {
            _gatilho.Armar("[u].[SenhaHash]", () => SqlAsync("""
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = @u;
                """, ("@u", admin.Id)));
            using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, admin.Senha, NovaSenha());
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }

        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
    }

    [Fact]
    public async Task CargoDoResponsavelMudaDepoisDaValidacaoDoToken_403_PelaMatrizDoBanco()
    {
        var secretario = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, secretario);
        var antes = await EstadoAsync(_factory, membro.Id);
        var cargoDira = await CargoIdAsync("DIRA");
        _gatilho.Armar("[u].[SenhaHash]", () => SqlAsync("""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET CargoId = @c WHERE Id = @u;
            """, ("@u", secretario.Id), ("@c", cargoDira)));

        using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, secretario.Senha, NovaSenha());

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
    }

    [Fact]
    public async Task VersaoDoAlvoMudaDentroDaTransacao_UpdateNaoAfetaLinha_409ERollback()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var responsavel = await LoginAsync(_factory, admin);
        using var afetado = await LoginAsync(_factory, membro);
        var antes = await EstadoAsync(_factory, membro.Id);
        _gatilho.Armar("SET [u].[SenhaHash]", async comando =>
        {
            await using var mesmaTransacao = comando.Connection!.CreateCommand();
            mesmaTransacao.Transaction = comando.Transaction;
            mesmaTransacao.CommandText = "UPDATE dbo.Usuarios SET SecurityVersion = SecurityVersion + 1 WHERE Id = @u";
            mesmaTransacao.Parameters.Add(new SqlParameter("@u", membro.Id));
            await mesmaTransacao.ExecuteNonQueryAsync();
        });

        using var resposta = await AlterarDeUsuarioAsync(responsavel.Client, membro.Id, admin.Senha, NovaSenha());

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(antes, await EstadoAsync(_factory, membro.Id));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(_factory, afetado.AccessToken));
    }

    [Fact]
    public async Task UsuariosECargosFicamTravadosAteOCommit()
    {
        var secretario = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var sessao = await LoginAsync(_factory, secretario);
        var cargoDs = await CargoIdAsync("DS");
        var bloqueios = new List<int>();
        _gatilho.Armar("INSERT INTO [Usuarios_alteracoes_senha]", async () =>
        {
            foreach (var (sql, id) in new[]
            {
                ("UPDATE dbo.Cargos SET UltimaAtualizacao = SYSUTCDATETIME() WHERE Id = @id", cargoDs),
                ("UPDATE dbo.Usuarios SET FalhasLoginConsecutivas = FalhasLoginConsecutivas + 1 WHERE Id = @id", membro.Id),
                ("UPDATE dbo.Usuarios SET FalhasLoginConsecutivas = FalhasLoginConsecutivas + 1 WHERE Id = @id", secretario.Id),
            })
            {
                try { await SqlAsync("SET LOCK_TIMEOUT 1000; " + sql, ("@id", id)); bloqueios.Add(0); }
                catch (SqlException ex) { bloqueios.Add(ex.Number); }
            }
        });

        using var resposta = await AlterarDeUsuarioAsync(sessao.Client, membro.Id, secretario.Senha, NovaSenha());

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal([1222, 1222, 1222], bloqueios);
    }

    [Fact]
    public async Task TrocasParalelasDoMesmoAlvo_NaoPerdemAuditoriaNemDeixamVersaoInconsistente()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var diretor = await ContaAsync(_factory, "DIR", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var a = await LoginAsync(_factory, admin);
        using var d = await LoginAsync(_factory, diretor);
        var antes = await EstadoAsync(_factory, membro.Id);
        var novas = new[] { NovaSenha(), NovaSenha(), NovaSenha(), NovaSenha() };

        var respostas = await Task.WhenAll(novas.Select((nova, i) =>
            AlterarDeUsuarioAsync(i % 2 == 0 ? a.Client : d.Client, membro.Id, i % 2 == 0 ? admin.Senha : diretor.Senha, nova)));

        Assert.All(respostas, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }));
        var sucessos = respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.InRange(sucessos, 1, novas.Length);
        var depois = await EstadoAsync(_factory, membro.Id);
        Assert.Equal((antes.SecurityVersion + sucessos, sucessos, sucessos), (depois.SecurityVersion, depois.Auditorias, depois.Notificacoes));
        var vigentes = novas.Where((nova, i) => respostas[i].StatusCode == HttpStatusCode.NoContent).ToList();
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher<Usuario>>();
        Assert.Single(vigentes, nova => hasher.VerifyHashedPassword(new Usuario { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" },
            depois.SenhaHash, nova) != PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task LoginsERefreshesConcorrentesComATroca_NaoDeixamSessaoUtilizavelComVersaoAntiga()
    {
        var admin = await ContaAsync(_factory, "ADM", NovaSenha());
        var membro = await ContaAsync(_factory, "DS");
        using var responsavel = await LoginAsync(_factory, admin);
        var anteriores = new List<Sessao>();
        for (var i = 0; i < 4; i++) anteriores.Add(await LoginAsync(_factory, membro));
        var nova = NovaSenha();

        var logins = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = await AnonimoAsync(_factory);
            using var resposta = await client.PostAsJsonAsync("/api/Auth/login", new { email = membro.Email, senha = membro.Senha });
            return resposta.IsSuccessStatusCode ? (await resposta.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken : null;
        }).ToList();
        var refreshes = anteriores.Select(s => RefreshAsync(_factory, s.Refresh)).ToList();
        var troca = AlterarDeUsuarioAsync(responsavel.Client, membro.Id, admin.Senha, nova);

        var tokens = (await Task.WhenAll(logins)).Where(t => t is not null).Select(t => t!).ToList();
        var renovados = (await Task.WhenAll(refreshes)).Where(r => r.AccessToken is not null).ToList();
        Assert.Equal(HttpStatusCode.NoContent, (await troca).StatusCode);

        foreach (var token in tokens.Concat(renovados.Select(r => r.AccessToken!)).Concat(anteriores.Select(s => s.AccessToken)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(_factory, token));
        }
        foreach (var refresh in renovados.Select(r => r.Refresh!).Concat(anteriores.Select(s => s.Refresh)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await RefreshComCookieAsync(_factory, refresh));
        }
        anteriores.ForEach(s => s.Dispose());
        var agora = DateTime.UtcNow;
        Assert.Equal(0, await Db(db => db.AuthSessions.Where(SessoesAutenticacao.Vigente(agora)).CountAsync(s => s.UsuarioId == membro.Id)));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, membro.Email, nova));
    }

    [Fact]
    public async Task SenhasNuncaAparecemNosLogs_NemNasRespostasDeErro()
    {
        var secretario = await ContaAsync(_factory, "SEC", NovaSenha());
        var membro = await ContaAsync(_factory, "DS", NovaSenha());
        using var sessao = await LoginAsync(_factory, secretario);
        var nova = NovaSenha();
        var fraca = $"Curta{Guid.NewGuid():N}"[..12];
        var errada = $"Errada-{Guid.NewGuid():N}";

        var respostas = new List<HttpResponseMessage>
        {
            await AlterarDeUsuarioAsync(sessao.Client, membro.Id, errada, nova),
            await AlterarDeUsuarioAsync(sessao.Client, membro.Id, secretario.Senha, fraca),
            await AlterarDeUsuarioAsync(sessao.Client, membro.Id, secretario.Senha, nova, nova + "x"),
            await AlterarDeUsuarioAsync(sessao.Client, membro.Id, secretario.Senha, nova),
        };
        foreach (var resposta in respostas)
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.All(new[] { nova, fraca, errada, secretario.Senha, membro.Senha }, segredo => Assert.DoesNotContain(segredo, corpo));
            resposta.Dispose();
        }

        var logs = string.Join("\n", _factory.Logs.Entries);
        Assert.Contains("alterada (Administrativa)", logs);
        Assert.All(new[] { nova, fraca, errada, secretario.Senha, membro.Senha }, segredo => Assert.DoesNotContain(segredo, logs));
    }

    [Fact]
    public async Task HashDeCustoAntigo_ContinuaValido_EEhRegravadoComOCustoNovoNoLogin()
    {
        var membro = await ContaAsync(_factory, "DS", NovaSenha());
        var antigo = new PasswordHasher<Usuario>().HashPassword(new Usuario { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" }, membro.Senha);
        Assert.Equal(100_000, IteracoesDoHash(antigo));
        await SqlAsync("UPDATE dbo.Usuarios SET SenhaHash = @h WHERE Id = @u", ("@h", antigo), ("@u", membro.Id));

        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(_factory, membro.Email, membro.Senha));

        Assert.Equal(PasswordPolicy.IteracoesHash, IteracoesDoHash((await EstadoAsync(_factory, membro.Id)).SenhaHash));
    }
}

/// <summary>Falhas de gravação e commit de resultado desconhecido, cada caso com banco próprio (constraints alteradas, strategy de teste).</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AlteracaoSenhaAtomicidadeSqlServerTests
{
    private static async Task SqlAsync(string connectionString, string sql)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("ALTER TABLE dbo.Usuarios_alteracoes_senha WITH NOCHECK ADD CONSTRAINT CK_teste_falha CHECK ([TraceId] = 'nunca')")]
    [InlineData("ALTER TABLE dbo.notificacoes DROP CONSTRAINT CK_notificacoes_Tipo; ALTER TABLE dbo.notificacoes WITH NOCHECK ADD CONSTRAINT CK_notificacoes_Tipo CHECK ([Tipo] IN (1, 2))")]
    public async Task FalhaAoGravarAuditoriaOuNotificacao_DesfazSenhaVersaoESessoes(string sabotagem)
    {
        using var factory = new SqlServerApiFactory(SemLimites());
        var admin = await ContaAsync(factory, "ADM", NovaSenha());
        var membro = await ContaAsync(factory, "DS");
        using var responsavel = await LoginAsync(factory, admin);
        using var afetado = await LoginAsync(factory, membro);
        var antes = await EstadoAsync(factory, membro.Id);
        await SqlAsync(factory.ConnectionString, sabotagem);

        using var resposta = await AlterarDeUsuarioAsync(responsavel.Client, membro.Id, admin.Senha, NovaSenha());

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        AssertNoStore(resposta);
        Assert.Equal(antes, await EstadoAsync(factory, membro.Id));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(factory, afetado.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(factory, membro.Email, membro.Senha));
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.AlteracoesSenha.CountAsync()));
    }

    [Fact]
    public async Task CommitComResultadoDesconhecido_RepeticaoNaoDuplicaAuditoriaNotificacaoNemVersao()
    {
        var controle = new EstrategiaCommitIncerto.Controle();
        using var factory = new SqlServerApiFactory(SemLimites(), configureSql: o => o.ExecutionStrategy(d => new EstrategiaCommitIncerto(d, controle)));
        controle.ConnectionString = factory.ConnectionString;
        var admin = await ContaAsync(factory, "ADM", NovaSenha());
        var membro = await ContaAsync(factory, "DS");
        using var responsavel = await LoginAsync(factory, admin);
        var antes = await EstadoAsync(factory, membro.Id);
        var nova = NovaSenha();
        controle.Armado = 1;

        using var resposta = await AlterarDeUsuarioAsync(responsavel.Client, membro.Id, admin.Senha, nova);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(1, controle.Repeticoes);
        var depois = await EstadoAsync(factory, membro.Id);
        Assert.Equal((antes.SecurityVersion + 1, 1, 1), (depois.SecurityVersion, depois.Auditorias, depois.Notificacoes));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(factory, membro.Email, nova));
    }

    /// <summary>
    /// Depois que a operação mais externa termina e a auditoria já foi gravada, finge que a confirmação do commit se perdeu e executa a
    /// operação inteira de novo, como faria a strategy com retry. Chamadas aninhadas (SaveChanges, consultas) nunca são repetidas.
    /// </summary>
    private sealed class EstrategiaCommitIncerto(ExecutionStrategyDependencies dependencies, EstrategiaCommitIncerto.Controle controle) : IExecutionStrategy
    {
        public sealed class Controle
        {
            public readonly AsyncLocal<int> Profundidade = new();
            public int Armado;
            public int Repeticoes;
            public string ConnectionString = "";

            public bool AuditoriaGravada()
            {
                try
                {
                    using var conexao = new SqlConnection(ConnectionString);
                    conexao.Open();
                    using var comando = conexao.CreateCommand();
                    comando.CommandText = "SELECT COUNT(*) FROM dbo.Usuarios_alteracoes_senha";
                    return Convert.ToInt32(comando.ExecuteScalar()) > 0;
                }
                catch (SqlException)
                {
                    return false;
                }
            }
        }

        public bool RetriesOnFailure => true;

        public TResult Execute<TState, TResult>(TState state, Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) => operation(dependencies.CurrentContext.Context, state);

        public async Task<TResult> ExecuteAsync<TState, TResult>(TState state, Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded, CancellationToken cancellationToken = default)
        {
            var externa = controle.Profundidade.Value == 0;
            controle.Profundidade.Value++;
            try
            {
                var resultado = await operation(dependencies.CurrentContext.Context, state, cancellationToken);
                if (externa && Volatile.Read(ref controle.Armado) == 1 && controle.AuditoriaGravada() && Interlocked.Exchange(ref controle.Armado, 0) == 1)
                {
                    Interlocked.Increment(ref controle.Repeticoes);
                    resultado = await operation(dependencies.CurrentContext.Context, state, cancellationToken);
                }
                return resultado;
            }
            finally
            {
                controle.Profundidade.Value--;
            }
        }
    }
}
