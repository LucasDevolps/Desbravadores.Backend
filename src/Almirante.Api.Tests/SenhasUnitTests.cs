using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Almirante.Api.Controllers;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Almirante.Api.Validation.Senhas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

/// <summary>Regras puras da issue #159: política compartilhada, matriz, token, validação, mensagens e limites.</summary>
public sealed class SenhasUnitTests
{
    private static readonly string[] TodosOsCargos =
        ["ADM", "DIR", "DIRA", "SEC", "TES", "DS", "CAP", "CONS", "CONSA", "INST", "CPT", "SECU", "TESU", "ALM", "PAD", "COREC", "CAPU"];

    [Fact]
    public void Politica_ContaPontosDeCodigo_MinimoQuinzeMaximoCentoEVinteEOito()
    {
        Assert.Equal((15, 128), (PasswordPolicy.MinLength, PasswordPolicy.MaxLength));
        Assert.NotEmpty(PasswordPolicy.Validate("Rio azul 4 gat"));
        Assert.Empty(PasswordPolicy.Validate("Rio azul 4 gato"));

        // 14 caracteres do BMP + 1 emoji (2 unidades UTF-16) = 15 pontos de código.
        var comEmoji = "Rio azul 4 gat" + "🐱";
        Assert.Equal((16, 15), (comEmoji.Length, PasswordPolicy.Comprimento(comEmoji)));
        Assert.Empty(PasswordPolicy.Validate(comEmoji));

        // 128 pontos de código (256 unidades UTF-16) passam; 129 não.
        var longa = string.Concat(Enumerable.Range(0, 64).Select(i => char.ConvertFromUtf32(0x1F600 + i % 40) + (char)('a' + i % 26)));
        Assert.Equal(128, PasswordPolicy.Comprimento(longa));
        Assert.Empty(PasswordPolicy.Validate(longa));
        Assert.Contains("A senha deve ter no máximo 128 caracteres.", PasswordPolicy.Validate(longa + "x"));
    }

    [Theory]
    [InlineData("cavalo correto bateria grampo")]
    [InlineData("so letras minusculas sem numero")]
    [InlineData("  espaços nas pontas contam  ")]
    [InlineData("Maçã-pêra-uvá-limão-côco")]
    public void Politica_AceitaFrasesEspacosESemComposicaoArtificial(string senha) => Assert.Empty(PasswordPolicy.Validate(senha, "pessoa@clube.org"));

    [Theory]
    [InlineData("abcabcabcabcabc")]
    [InlineData("Azul42azul42AZUL42")]
    [InlineData("Deus é fiel 2026!")]
    [InlineData("correct horse battery staple")]
    [InlineData("Desbravadores IASD 1")]
    [InlineData("123456789012345")]
    [InlineData("qwertyuiopasdfgh")]
    [InlineData("passwordpassword")]
    [InlineData("aaaaaaaaaaaaaaab")]
    [InlineData("123456789012345678")]
    public void Politica_RecusaRepeticoesSequenciasESenhasComunsLocais(string senha) => Assert.NotEmpty(PasswordPolicy.Validate(senha));

    [Fact]
    public void Politica_SurrogateInvalido_NaoLancaExcecao()
    {
        var senha = "Senha longa e boa \uD800 fim";
        Assert.False(SenhasComuns.Contem(senha));
        Assert.Empty(PasswordPolicy.Validate(senha));
        Assert.False(SenhasComuns.Contem("!!!***@@@"));
        Assert.False(SenhasComuns.Contem("5555-4444-3333"));
        Assert.True(SenhasComuns.Contem("Jesus me ama 777"));
    }

    [Fact]
    public void Matriz_TabelaVerdadeCompleta()
    {
        foreach (var solicitante in TodosOsCargos)
        foreach (var alvo in TodosOsCargos)
        {
            var esperado = (solicitante, alvo) switch
            {
                ("ADM" or "DIR", "SEC" or "TES" or "DS") => true,
                ("SEC", "DS") => true,
                _ => false,
            };
            Assert.True(esperado == PermissoesSenha.PodeAlterarSenhaDeTerceiro(solicitante, alvo), $"{solicitante} → {alvo}: esperado {esperado}");
        }

        Assert.Equal(["ADM", "DIR", "SEC"], PermissoesSenha.Solicitantes);
        Assert.Empty(PermissoesSenha.AlvosPermitidos("DIRA"));
        Assert.Equal(["SEC", "TES", "DS"], TodosOsCargos.Where(PermissoesSenha.PodeSerAlvo));
        Assert.DoesNotContain("DIRA", Roles.Diretoria.Where(r => PermissoesSenha.AlvosPermitidos(r).Count > 0));
    }

    [Fact]
    public void Token_TrintaEDoisBytesAleatorios_HashComFinalidade_ERecusaFormatosInvalidos()
    {
        var token = TokensRecuperacaoSenha.Gerar(out var hash);
        Assert.Equal(43, token.Length);
        Assert.Equal(32, hash.Length);
        Assert.True(TokensRecuperacaoSenha.TentarCalcularHash(token, out var recalculado));
        Assert.Equal(hash, recalculado);
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, bytes.Length);
        Assert.NotEqual(SHA256.HashData(bytes), hash);
        Assert.NotEqual(token, TokensRecuperacaoSenha.Gerar(out _));
        Assert.Equal(TimeSpan.FromMinutes(15), TokensRecuperacaoSenha.Validade);

        foreach (var invalido in new[] { null, "", token[..42], token + "A", new string('!', 43), token.Replace(token[0], '+') })
        {
            Assert.False(TokensRecuperacaoSenha.TentarCalcularHash(invalido, out var vazio));
            Assert.Empty(vazio);
        }
    }

    [Fact]
    public void Validadores_ConfirmacaoOrdinal_CamposObrigatorios_MensagensSemOsValores()
    {
        var segredo = "Segredo-" + Guid.NewGuid().ToString("N");
        var validator = new AlterarPropriaSenhaRequestValidator();

        Assert.True(validator.Validate(new AlterarPropriaSenhaRequest { SenhaAtualSolicitante = "x", NovaSenha = segredo, ConfirmacaoNovaSenha = segredo }).IsValid);
        foreach (var confirmacao in new[] { segredo.ToUpperInvariant(), segredo + " ", " " + segredo, segredo.Normalize(System.Text.NormalizationForm.FormD) + "́" })
        {
            var resultado = validator.Validate(new AlterarPropriaSenhaRequest { SenhaAtualSolicitante = "x", NovaSenha = segredo, ConfirmacaoNovaSenha = confirmacao });
            Assert.Contains(resultado.Errors, e => e.PropertyName == "confirmacaoNovaSenha");
            Assert.All(resultado.Errors, e => Assert.DoesNotContain(segredo, e.ErrorMessage, StringComparison.OrdinalIgnoreCase));
        }

        var vazio = validator.Validate(new AlterarPropriaSenhaRequest());
        Assert.Equal(["confirmacaoNovaSenha", "novaSenha", "senhaAtualSolicitante"], vazio.Errors.Select(e => e.PropertyName).Distinct().Order());
        Assert.Contains(validator.Validate(new AlterarPropriaSenhaRequest { SenhaAtualSolicitante = new string('a', RegrasSenha.SenhaAtualMaxima + 1), NovaSenha = segredo, ConfirmacaoNovaSenha = segredo }).Errors,
            e => e.PropertyName == "senhaAtualSolicitante");

        var usuario = new AlterarSenhaUsuarioRequestValidator();
        Assert.Contains(usuario.Validate(new AlterarSenhaUsuarioRequest { SenhaAtualSolicitante = "x", NovaSenha = segredo, ConfirmacaoNovaSenha = segredo, MetodoVerificacao = (MetodoVerificacaoIdentidade)9 }).Errors,
            e => e.PropertyName == "metodoVerificacao");
        Assert.True(usuario.Validate(new AlterarSenhaUsuarioRequest { SenhaAtualSolicitante = "x", NovaSenha = segredo, ConfirmacaoNovaSenha = segredo }).IsValid);

        var emissao = new EmitirAutorizacaoRecuperacaoRequestValidator();
        Assert.Equal(["metodoVerificacao", "senhaAtualSolicitante"], emissao.Validate(new EmitirAutorizacaoRecuperacaoRequest()).Errors.Select(e => e.PropertyName).Order());
        Assert.True(emissao.Validate(new EmitirAutorizacaoRecuperacaoRequest { SenhaAtualSolicitante = "x", MetodoVerificacao = MetodoVerificacaoIdentidade.AtendimentoPresencial }).IsValid);

        var confirmar = new ConfirmarRecuperacaoSenhaRequestValidator();
        Assert.Contains(confirmar.Validate(new ConfirmarRecuperacaoSenhaRequest { NovaSenha = segredo, ConfirmacaoNovaSenha = segredo }).Errors, e => e.PropertyName == "token");
        Assert.Contains(confirmar.Validate(new ConfirmarRecuperacaoSenhaRequest { Token = new string('a', 257), NovaSenha = segredo, ConfirmacaoNovaSenha = segredo }).Errors,
            e => e.PropertyName == "token");

        var solicitar = new SolicitarRecuperacaoSenhaRequestValidator();
        foreach (var email in new[] { null, " ", "sem-arroba", "acentuação@clube.org", new string('a', 95) + "@clube.org" })
        {
            Assert.Equal("email", Assert.Single(solicitar.Validate(new SolicitarRecuperacaoSenhaRequest { Email = email }).Errors).PropertyName);
        }
        Assert.True(solicitar.Validate(new SolicitarRecuperacaoSenhaRequest { Email = "pessoa@clube.org" }).IsValid);

        var lista = new ListarRecuperacoesSenhaPendentesQueryValidator();
        Assert.False(lista.Validate(new ListarRecuperacoesSenhaPendentesQuery(Guid.NewGuid(), 0, null)).IsValid);
        Assert.False(lista.Validate(new ListarRecuperacoesSenhaPendentesQuery(Guid.NewGuid(), 20, "x")).IsValid);
        Assert.True(lista.Validate(new ListarRecuperacoesSenhaPendentesQuery(Guid.NewGuid(), 20, CursorNotificacoes.Criar(DateTime.UtcNow, Guid.NewGuid()))).IsValid);
    }

    [Fact]
    public void Dtos_RedigemToString()
    {
        var segredo = "Segredo-" + Guid.NewGuid().ToString("N");
        object[] dtos =
        [
            new AlterarPropriaSenhaRequest { SenhaAtualSolicitante = segredo, NovaSenha = segredo, ConfirmacaoNovaSenha = segredo },
            new AlterarSenhaUsuarioRequest { SenhaAtualSolicitante = segredo, NovaSenha = segredo, ConfirmacaoNovaSenha = segredo },
            new SolicitarRecuperacaoSenhaRequest { Email = segredo + "@clube.org" },
            new ConfirmarRecuperacaoSenhaRequest { Token = segredo, NovaSenha = segredo, ConfirmacaoNovaSenha = segredo },
            new EmitirAutorizacaoRecuperacaoRequest { SenhaAtualSolicitante = segredo },
            new AutorizacaoRecuperacaoSenhaResponse { Token = segredo },
            new EntregaTokenRecuperacaoSenha(Guid.NewGuid(), Guid.NewGuid(), segredo, DateTime.UtcNow),
            new ContasSenha.Conta(Guid.NewGuid(), true, segredo, 1, "DS", true, "x@clube.org"),
        ];
        foreach (var dto in dtos)
        {
            Assert.DoesNotContain(segredo, dto.ToString());
        }
        Assert.Contains(ConteudoSensivelAttribute.MarcadorOmitido, dtos[0].ToString());
        Assert.Equal(RecuperacaoSenhaSolicitadaResponse.MensagemPadrao, new RecuperacaoSenhaSolicitadaResponse().Mensagem);
    }

    [Fact]
    public void RotasComSenhaOuToken_TemMarcadorNoStoreEHttpLoggingSoComMetadados()
    {
        Type[] comSegredo = [typeof(AlterarPropriaSenhaRequest), typeof(AlterarSenhaUsuarioRequest), typeof(SolicitarRecuperacaoSenhaRequest),
            typeof(ConfirmarRecuperacaoSenhaRequest), typeof(EmitirAutorizacaoRecuperacaoRequest)];
        var acoes = typeof(Program).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ControllerBase)))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => comSegredo.Contains(p.ParameterType)))
            .ToList();

        Assert.Equal(["AlterarSenha", "AlterarSenha", "ConfirmarRecuperacaoSenha", "EmitirAutorizacaoRecuperacao", "SolicitarRecuperacaoSenha"],
            acoes.Select(a => a.Name).Order());
        foreach (var acao in acoes)
        {
            Assert.NotNull(acao.GetCustomAttribute<ConteudoSensivelAttribute>());
            Assert.Equal(ConteudoSensivelAttribute.CamposRegistraveis, acao.GetCustomAttribute<HttpLoggingAttribute>()!.LoggingFields);
            Assert.NotNull(acao.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
        Assert.False(ConteudoSensivelAttribute.CamposRegistraveis.HasFlag(HttpLoggingFields.RequestBody));
        Assert.False(ConteudoSensivelAttribute.CamposRegistraveis.HasFlag(HttpLoggingFields.ResponseBody));
        Assert.False(ConteudoSensivelAttribute.CamposRegistraveis.HasFlag(HttpLoggingFields.RequestHeaders));
        Assert.False(ConteudoSensivelAttribute.Aplica(new DefaultHttpContext()));
    }

    [Fact]
    public void Notificacao_SenhaAlterada_TextoSeguroOrigemDaAuditoriaEAvisoPendente()
    {
        var destinatario = Guid.NewGuid();
        var auditoria = Guid.NewGuid();
        var agora = DateTime.UtcNow;

        var administrativa = NotificacoesFabrica.SenhaAlterada(destinatario, auditoria, TipoAlteracaoSenha.Administrativa, agora);
        var assistida = NotificacoesFabrica.SenhaAlterada(destinatario, auditoria, TipoAlteracaoSenha.RecuperacaoAssistida, agora);

        Assert.Equal((destinatario, TipoNotificacao.SenhaAlterada, $"senha:{auditoria:N}", (AvisoNotificacao?)AvisoNotificacao.Criada, agora, (Guid?)null, (Guid?)null),
            (administrativa.DestinatarioId, administrativa.Tipo, administrativa.ChaveOrigem, administrativa.AvisoPendente, administrativa.AvisoProximaTentativaEmUtc,
             administrativa.EventoId, administrativa.LancamentoId));
        Assert.StartsWith("Sua senha de acesso foi alterada por um usuário autorizado da administração.", administrativa.Mensagem);
        Assert.Contains("autorização de recuperação", assistida.Mensagem);
        Assert.Equal("[Tipo] IN (1, 2, 3)", Almirante.Api.Data.AlmiranteDbContext.CheckNotificacoesTipo);
    }

    [Fact]
    public void Email_ModeloDoAvisoProntoSemSenha_ParaCadaTipo()
    {
        var quando = new DateTime(2026, 10, 26, 18, 5, 0, DateTimeKind.Utc);
        foreach (var tipo in Enum.GetValues<TipoAlteracaoSenha>())
        {
            var mensagem = ModeloAvisoSenhaAlterada.Montar("Maria", tipo, quando);
            Assert.Equal("Sua senha de acesso foi alterada", mensagem.Assunto);
            Assert.Contains("Olá, Maria.", mensagem.Corpo);
            Assert.Contains("26/10/2026 às 18:05 UTC", mensagem.Corpo);
            Assert.Contains("não contém sua senha", mensagem.Corpo);
        }
        Assert.Contains("por você", ModeloAvisoSenhaAlterada.Montar("M", TipoAlteracaoSenha.Propria, quando).Corpo);
        Assert.Contains("administração do clube", ModeloAvisoSenhaAlterada.Montar("M", TipoAlteracaoSenha.RecuperacaoAssistida, quando).Corpo);
        Assert.Contains("recuperação de acesso", ModeloAvisoSenhaAlterada.Montar("M", TipoAlteracaoSenha.RecuperacaoAutonoma, quando).Corpo);
        Assert.Equal(new AvisoSenhaAlterada(Guid.Empty, TipoAlteracaoSenha.Propria, quando), new AvisoSenhaAlterada(Guid.Empty, TipoAlteracaoSenha.Propria, quando));
    }

    [Fact]
    public void Limitador_PorUsuarioEPorIp_EPorIpNaRecuperacao()
    {
        using var limitador = new LimitadorTentativasSenha(Microsoft.Extensions.Options.Options.Create(new SenhasOptions
        {
            TentativasPorUsuario = 2, TentativasPorIp = 3, RecuperacoesPorIp = 1, JanelaMinutos = 15,
        }));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        bool Tenta(Guid usuario, string ip) { using var lease = limitador.RegistrarTentativa(usuario, ip); return lease.IsAcquired; }

        Assert.True(Tenta(a, "10.0.0.1"));
        Assert.True(Tenta(a, "10.0.0.2"));
        Assert.False(Tenta(a, "10.0.0.3"));
        Assert.True(Tenta(b, "10.0.0.9"));
        Assert.True(Tenta(b, "10.0.0.9"));
        var c = Guid.NewGuid();
        Assert.True(Tenta(c, "10.0.0.9"));
        Assert.False(Tenta(Guid.NewGuid(), "10.0.0.9"));

        using (var primeira = limitador.RegistrarRecuperacao("10.0.0.5")) Assert.True(primeira.IsAcquired);
        using (var segunda = limitador.RegistrarRecuperacao("10.0.0.5")) Assert.False(segunda.IsAcquired);
        using (var outroIp = limitador.RegistrarRecuperacao("10.0.0.6")) Assert.True(outroIp.IsAcquired);
    }

    [Fact]
    public void RespostaMuitasTentativas_ProblemDetailsComRetryAfter()
    {
        using var limitador = new LimitadorTentativasSenha(Microsoft.Extensions.Options.Options.Create(new SenhasOptions { RecuperacoesPorIp = 1, JanelaMinutos = 2 }));
        limitador.RegistrarRecuperacao("ip").Dispose();
        using var recusa = limitador.RegistrarRecuperacao("ip");
        var contexto = new DefaultHttpContext();

        var resultado = RespostaMuitasTentativas.Criar(contexto.Response, recusa, "Título", "Detalhe");

        Assert.Equal(429, resultado.StatusCode);
        var problema = Assert.IsType<ProblemDetails>(resultado.Value);
        Assert.Equal(("Título", "Detalhe", 429), (problema.Title, problema.Detail, problema.Status));
        Assert.InRange(int.Parse(contexto.Response.Headers.RetryAfter.ToString()), 1, 120);
    }

    [Fact]
    public void RespostaMuitasTentativas_SemRetryAfterNoLimitador_UsaUmMinuto()
    {
        var contexto = new DefaultHttpContext();

        var resultado = RespostaMuitasTentativas.Criar(contexto.Response, new RecusaSemMetadados(), "T", "D");

        Assert.Equal((429, "60"), (resultado.StatusCode, contexto.Response.Headers.RetryAfter.ToString()));
    }

    private sealed class RecusaSemMetadados : System.Threading.RateLimiting.RateLimitLease
    {
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    [Fact]
    public void TraceIdSeguro_UsaOTraceW3COuOIdentificadorDaRequisicao_SoAscii()
    {
        var contexto = new DefaultHttpContext { TraceIdentifier = "0HN:00000001é com espaço" };
        Assert.Equal("0HN:00000001comespaço".Replace("ç", ""), contexto.TraceIdSeguro());

        using var atividade = new Activity("teste").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.Equal(atividade.TraceId.ToHexString(), contexto.TraceIdSeguro());
        atividade.Stop();

        Assert.Equal("sem-trace", new DefaultHttpContext { TraceIdentifier = "éé" }.TraceIdSeguro());

        using var hierarquica = new Activity("legado").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        Assert.Equal("req-1", new DefaultHttpContext { TraceIdentifier = "req-1" }.TraceIdSeguro());
    }

    [Fact]
    public void SessaoId_VemDoTokenValidado_SemEla401()
    {
        var sid = Guid.NewGuid();
        var comSessao = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sid", sid.ToString())], "Bearer"));
        Assert.Equal(sid, comSessao.SessaoId());
        var erro = Assert.Throws<ApiProblemException>(() => new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()).SessaoId());
        Assert.Equal(401, erro.Status);
    }
}
