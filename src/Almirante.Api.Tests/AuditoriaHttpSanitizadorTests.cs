using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using static Almirante.Api.Security.SanitizadorAuditoriaHttp;

namespace Almirante.Api.Tests;

/// <summary>Política de sanitização da auditoria HTTP (issue #158), sem servidor: nomes, valores, JSON, URL, cabeçalhos e exceções.</summary>
public class AuditoriaHttpSanitizadorTests
{
    private const string Cpf = "52998224725";
    private const string CpfFormatado = "529.982.247-25";
    private const string Email = "maria.souza@exemplo.com.br";
    private const string Telefone = "11987654321";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.segredo.assinatura";

    private static bool Nenhum(string _) => false;

    [Theory]
    [InlineData("cpf"), InlineData("CPF"), InlineData("responsavelCpf"), InlineData("responsavel_cpf"), InlineData("CpfInformado")]
    [InlineData("email"), InlineData("e-mail"), InlineData("E_Mail"), InlineData("emailNormalizado"), InlineData("mail")]
    [InlineData("telefone"), InlineData("telefoneCelular"), InlineData("phone"), InlineData("phoneNumber"), InlineData("whatsapp"), InlineData("celular")]
    [InlineData("responsavelNome"), InlineData("ResponsavelTelefone"), InlineData("guardianName")]
    [InlineData("rg"), InlineData("Rg"), InlineData("cin"), InlineData("ehCin"), InlineData("documento"), InlineData("passport"), InlineData("ssn")]
    [InlineData("tipoSanguineo"), InlineData("bloodType"), InlineData("doencasAlergias"), InlineData("allergies"), InlineData("saude"), InlineData("healthNotes")]
    [InlineData("endereco"), InlineData("address"), InlineData("cep"), InlineData("zipCode"), InlineData("logradouro"), InlineData("bairro"), InlineData("uf")]
    [InlineData("dataNascimento"), InlineData("birthDate"), InlineData("foto"), InlineData("fotoBase64"), InlineData("photoUrl"), InlineData("arquivo"), InlineData("file")]
    [InlineData("ip"), InlineData("ipResponsavel"), InlineData("nomeMae"), InlineData("dataNaScImEnTo"), InlineData("endereço"), InlineData("saúde")]
    public void DadoPessoal_EmQualquerGrafia_ViraPessoal(string nome) => Assert.Equal(CategoriaCampoAuditoria.Pessoal, Classificar(nome));

    [Theory]
    [InlineData("senha"), InlineData("senhaAtualSolicitante"), InlineData("novaSenha"), InlineData("confirmacaoNovaSenha"), InlineData("password")]
    [InlineData("accessToken"), InlineData("access_token"), InlineData("refreshToken"), InlineData("token"), InlineData("csrfToken"), InlineData("X-CSRF-TOKEN")]
    [InlineData("senhaHash"), InlineData("passwordHash"), InlineData("authorization"), InlineData("cookie"), InlineData("apiKey"), InlineData("api_key")]
    [InlineData("clientSecret"), InlineData("segredo"), InlineData("connectionString"), InlineData("jwt"), InlineData("pwd"), InlineData("refreshTokenId")]
    public void Credencial_EmQualquerGrafia_ViraCredencial(string nome) => Assert.Equal(CategoriaCampoAuditoria.Credencial, Classificar(nome));

    [Theory]
    [InlineData("id"), InlineData("Id"), InlineData("membroId"), InlineData("usuarioResponsavelId"), InlineData("eventoIds"), InlineData("traceId")]
    public void NomeTerminadoEmId_EhIdentificador(string nome) => Assert.Equal(CategoriaCampoAuditoria.Identificador, Classificar(nome));

    [Fact]
    public void ListaRevisada_NenhumNomeEhCredencialOuPessoal_ECadaUmMantemSuaCategoria()
    {
        Assert.All(Revisados, par => Assert.Equal(par.Value, Classificar(par.Key)));
    }

    [Theory]
    [InlineData("campoNovo"), InlineData("observacaoInterna2"), InlineData("dadosBancarios"), InlineData("x"), InlineData("")]
    public void CampoDesconhecido_NaoEhRevisado(string nome) => Assert.Equal(CategoriaCampoAuditoria.NaoRevisado, Classificar(nome));

    [Fact]
    public void Json_RemoveDadosPessoaisCredenciaisETextoLivre_EmObjetosAninhadosEListas()
    {
        var membroId = Guid.NewGuid();
        var entrada = new
        {
            id = membroId,
            nome = "Maria Souza",
            cpf = CpfFormatado,
            Email,
            telefone = Telefone,
            senha = "Senha-Super-Secreta-123",
            ativo = true,
            valor = 150.75m,
            dataInicial = "2026-10-01",
            status = "Pendente",
            finalidade = "Doação",
            endereco = new { cep = "01001000", logradouro = "Praça da Sé", numero = "100" },
            responsavel = new { nome = "João Souza", cpf = Cpf, email = "joao@exemplo.com" },
            membros = new object[] { membroId, new { id = membroId, telefone = Telefone, rg = "12.345.678-9" } },
            itens = new[] { new { descricao = $"ligar {Telefone}", quantidade = 2 } },
            campoNovo = "qualquer coisa",
            dadosBancarios = new { agencia = "AG-ZQX", conta = "CT-QWZ-X" },
            errors = new Dictionary<string, string[]> { ["Responsavel.Cpf"] = [$"CPF {CpfFormatado} inválido."] },
            doencasAlergias = "alergia a penicilina",
            accessToken = Token,
        };

        var (estado, json) = SanitizarJson(JsonSerializer.SerializeToUtf8Bytes(entrada), 100_000);

        Assert.Equal(EstadoCapturaHttp.Completa, estado);
        AssertSemSegredos(json, "Maria", "Souza", Cpf, CpfFormatado, "529.982", Email, "exemplo.com", Telefone, "Senha-Super-Secreta", "Praça",
            "01001000", "João", "12.345.678-9", "ligar", "qualquer coisa", "AG-ZQX", "CT-QWZ", "penicilina", Token, "segredo");
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        Assert.Equal(membroId.ToString(), raiz.GetProperty("id").GetString());
        Assert.Equal("[texto omitido: 11 caracteres]", raiz.GetProperty("nome").GetString());
        Assert.Equal(MarcadorPessoal, raiz.GetProperty("cpf").GetString());
        Assert.Equal(MarcadorPessoal, raiz.GetProperty("Email").GetString());
        Assert.Equal(MarcadorCredencial, raiz.GetProperty("senha").GetString());
        Assert.Equal(MarcadorCredencial, raiz.GetProperty("accessToken").GetString());
        Assert.True(raiz.GetProperty("ativo").GetBoolean());
        Assert.Equal(150.75m, raiz.GetProperty("valor").GetDecimal());
        Assert.Equal("2026-10-01", raiz.GetProperty("dataInicial").GetString());
        Assert.Equal("Pendente", raiz.GetProperty("status").GetString());
        Assert.Equal("Doação", raiz.GetProperty("finalidade").GetString());
        Assert.Equal(MarcadorPessoal, raiz.GetProperty("endereco").GetString());
        Assert.Equal(MarcadorPessoal, raiz.GetProperty("responsavel").GetString());
        Assert.Equal(membroId.ToString(), raiz.GetProperty("membros")[0].GetString());
        Assert.Equal(MarcadorPessoal, raiz.GetProperty("membros")[1].GetProperty("telefone").GetString());
        Assert.Equal(MarcadorNaoRevisado, raiz.GetProperty("itens").GetString());
        Assert.Equal(MarcadorNaoRevisado, raiz.GetProperty("campoNovo").GetString());
        Assert.Equal(MarcadorNaoRevisado, raiz.GetProperty("dadosBancarios").GetString());
        Assert.StartsWith("[texto omitido", raiz.GetProperty("errors").GetProperty("Responsavel.Cpf")[0].GetString());
    }

    [Fact]
    public void Json_ValorForaDoFormato_EmCampoRevisado_ViraMarcador()
    {
        var (_, json) = SanitizarJson(Encoding.UTF8.GetBytes(
            $$"""{"id":"{{Email}}","membroId":{{Cpf}},"status":"Maria da Silva","dataInicial":"{{Telefone}}","finalidade":"Pix para {{Email}}","title":"CPF {{CpfFormatado}} duplicado","valor":"{{Cpf}}","page":"{{Telefone}}"}"""), 10_000);

        AssertSemSegredos(json, Email, Cpf, CpfFormatado, Telefone, "Maria");
        Assert.Equal(8, Regex.Matches(json, Regex.Escape(MarcadorFormato)).Count);
    }

    [Fact]
    public void Json_ProblemDetails_MantemTituloStatusTipoETrace_SemMensagens()
    {
        var traceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00";
        var problema = $$"""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Dados inválidos.","status":400,
             "detail":"Usuário {{Email}} já existe","errors":{"Email":["O e-mail {{Email}} já está em uso."],"Responsavel.Telefone":["Inválido"]},
             "traceId":"{{traceId}}"}
            """;

        var (estado, json) = SanitizarJson(Encoding.UTF8.GetBytes(problema), 10_000);

        Assert.Equal(EstadoCapturaHttp.Completa, estado);
        AssertSemSegredos(json, Email);
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        Assert.Equal("Dados inválidos.", raiz.GetProperty("title").GetString());
        Assert.Equal(400, raiz.GetProperty("status").GetInt32());
        Assert.Equal(traceId, raiz.GetProperty("traceId").GetString());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", raiz.GetProperty("type").GetString());
        Assert.StartsWith("[texto omitido", raiz.GetProperty("detail").GetString());
        Assert.True(raiz.GetProperty("errors").TryGetProperty("Responsavel.Telefone", out _));
    }

    [Fact]
    public void Json_ChaveComDadoPessoal_ViraChaveOmitida()
    {
        var (_, json) = SanitizarJson(Encoding.UTF8.GetBytes($$"""{"{{Email}}":1,"{{CpfFormatado}}":"x","a b":true}"""), 10_000);

        AssertSemSegredos(json, Email, CpfFormatado);
        Assert.Contains("[chave omitida 3]", json);
    }

    [Theory]
    [InlineData("{\"cpf\": \"" + Cpf + "\"")]
    [InlineData("not json " + Cpf)]
    [InlineData("{\"a\":1}{\"cpf\":\"" + Cpf + "\"}")]
    [InlineData("")]
    public void Json_Invalido_NaoCaiEmGravacaoBruta(string entrada)
    {
        var (estado, json) = SanitizarJson(Encoding.UTF8.GetBytes(entrada), 10_000);

        Assert.Equal(EstadoCapturaHttp.FalhaSanitizacao, estado);
        AssertSemSegredos(json, Cpf);
        Assert.Contains("omitido", json);
    }

    [Fact]
    public void Json_GrandeDemais_SanitizaAntesDeCortar_ECorteContinuaSemDados()
    {
        var itens = Enumerable.Range(0, 500).Select(i => new { id = Guid.NewGuid(), cpf = Cpf, valor = i }).ToArray();

        var (estado, json) = SanitizarJson(JsonSerializer.SerializeToUtf8Bytes(new { items = itens }), 2_000);

        Assert.Equal(EstadoCapturaHttp.Truncada, estado);
        AssertSemSegredos(json, Cpf);
        using var documento = JsonDocument.Parse(json);
        Assert.True(documento.RootElement.GetProperty("truncado").GetBoolean());
        Assert.Equal(2_000, documento.RootElement.GetProperty("conteudoParcial").GetString()!.Length);
    }

    [Fact]
    public void Json_ListaLonga_ELimiteDeProfundidade_NaoEstouram()
    {
        var aninhado = string.Concat(Enumerable.Repeat("{\"items\":", 60)) + "1" + new string('}', 60);
        var (estadoProfundo, profundo) = SanitizarJson(Encoding.UTF8.GetBytes(aninhado), 100_000);
        var (estadoLista, lista) = SanitizarJson(JsonSerializer.SerializeToUtf8Bytes(new { items = Enumerable.Range(0, 250) }), 100_000);

        Assert.Equal(EstadoCapturaHttp.Completa, estadoProfundo);
        Assert.Contains("[profundidade excedida]", profundo);
        Assert.Equal(EstadoCapturaHttp.Completa, estadoLista);
        Assert.Contains("[50 itens omitidos]", lista);
    }

    [Fact]
    public void Json_GravadoCabeEmVarchar_SemCaractereForaDoLatin1()
    {
        var (_, json) = SanitizarJson(Encoding.UTF8.GetBytes("""{"title":"Ação concluída","area":"Ciência 🚀","nome":"Zoë 🚀","status":"Ok"}"""), 10_000);

        Assert.All(json, c => Assert.True(c <= 0xFF, $"caractere U+{(int)c:X4} fora do Latin-1"));
        Assert.Contains("Ação concluída", json);
        Assert.Equal(MarcadorFormato, JsonDocument.Parse(json).RootElement.GetProperty("area").GetString());
    }

    [Fact]
    public void Caminho_RotaResolvida_MantemLiteraisEGuid_OmiteParametroPessoal()
    {
        var id = Guid.NewGuid();
        var padraoUsuario = RoutePatternFactory.Parse("api/Usuarios/{id:guid}/foto");
        var padraoCep = RoutePatternFactory.Parse("api/Cep/{cep}");

        Assert.Equal($"/api/Usuarios/{id}/foto",
            SanitizarCaminho($"/api/Usuarios/{id}/foto", padraoUsuario, new RouteValueDictionary { ["id"] = id.ToString() }, Nenhum));
        Assert.Equal("/api/Cep/[omitido]", SanitizarCaminho("/api/Cep/01001000", padraoCep, new RouteValueDictionary { ["cep"] = "01001000" }, Nenhum));
    }

    [Fact]
    public void Caminho_SemRota_SoMantemLiteraisConhecidosGuidEInteirosCurtos()
    {
        var id = Guid.NewGuid();
        var conhecidos = new HashSet<string>(["api", "Usuarios"], StringComparer.OrdinalIgnoreCase);

        var caminho = SanitizarCaminho($"/api/usuarios/{CpfFormatado}/{Email}/{id}/42/{Telefone}/wp-admin.php", null, null, conhecidos.Contains);

        Assert.Equal($"/api/usuarios/[omitido]/[omitido]/{id}/42/[omitido]/[omitido]", caminho);
    }

    [Fact]
    public void Query_MantemParametrosRevisados_OmiteTokenEPessoais_NaOrdem()
    {
        var membroId = Guid.NewGuid();
        var query = new QueryString($"?page=2&pageSize=20&access_token={Token}&busca={Uri.EscapeDataString(Email)}&membroId={membroId}" +
            $"&cpf={Cpf}&status=Pago&{Uri.EscapeDataString(Email)}=1&campoNovo=x&dataInicial=2026-01-01");

        var sanitizada = SanitizarQuery(query);

        AssertSemSegredos(sanitizada, Token, Email, Uri.EscapeDataString(Email), Cpf, "eyJ");
        Assert.Equal($"?page=2&pageSize=20&access_token=[omitido]&busca=[omitido]&membroId={membroId}&cpf=[omitido]&status=Pago" +
            "&[omitido]=[omitido]&campoNovo=[omitido]&dataInicial=2026-01-01", sanitizada);
    }

    [Fact]
    public void Cabecalhos_SoListaPermitida_CredenciaisComoPresenca_LocationSanitizada()
    {
        var id = Guid.NewGuid();
        var request = new HeaderDictionary
        {
            ["Authorization"] = $"Bearer {Token}",
            ["Cookie"] = "__Host-almirante-refresh=abc123",
            ["X-CSRF-TOKEN"] = "CfDJ8-csrf-segredo",
            ["X-Forwarded-For"] = "203.0.113.9",
            ["X-Usuario-Email"] = Email,
            ["Accept"] = "application/json",
            ["Content-Type"] = "multipart/form-data; boundary=----segredo",
            ["User-Agent"] = $"Teste/1.0 ({Email})",
        };
        var response = new HeaderDictionary
        {
            ["Set-Cookie"] = "__Host-almirante-refresh=novo-token; path=/; secure; httponly",
            ["Location"] = $"https://api.exemplo.com/api/Usuarios/{id}?cpf={Cpf}",
            ["WWW-Authenticate"] = $"Bearer error=\"invalid_token\", error_description=\"The token {Token} expired\"",
            ["Content-Type"] = "application/problem+json; charset=utf-8",
            ["Retry-After"] = "30",
        };

        var json = SanitizarCabecalhos(request, response, s => s is "api" or "Usuarios");

        AssertSemSegredos(json, Token, "abc123", "csrf-segredo", "203.0.113.9", Email, "boundary", "novo-token", Cpf, "eyJ");
        using var documento = JsonDocument.Parse(json);
        var req = documento.RootElement.GetProperty("request");
        var resp = documento.RootElement.GetProperty("response");
        Assert.Equal(MarcadorCredencial, req.GetProperty("Authorization").GetString());
        Assert.Equal(MarcadorCredencial, req.GetProperty("Cookie").GetString());
        Assert.Equal(MarcadorCredencial, req.GetProperty("X-CSRF-TOKEN").GetString());
        Assert.False(req.TryGetProperty("X-Forwarded-For", out _));
        Assert.False(req.TryGetProperty("X-Usuario-Email", out _));
        Assert.Equal("multipart/form-data", req.GetProperty("Content-Type").GetString());
        Assert.Equal(MarcadorFormato, req.GetProperty("User-Agent").GetString());
        Assert.Equal(MarcadorCredencial, resp.GetProperty("Set-Cookie").GetString());
        Assert.Equal($"https://api.exemplo.com/api/Usuarios/{id}?cpf=[omitido]", resp.GetProperty("Location").GetString());
        Assert.Equal("Bearer error=\"invalid_token\"", resp.GetProperty("WWW-Authenticate").GetString());
        Assert.Equal("30", resp.GetProperty("Retry-After").GetString());
    }

    [Theory]
    [InlineData("Accept-Language", "pt-BR," + Cpf)]
    [InlineData("Accept-Language", "pt-BR," + CpfFormatado)]
    [InlineData("Accept", "application/json;v=" + Telefone)]
    [InlineData("Accept", "application/json, " + CpfFormatado)]
    [InlineData("X-Requested-With", "app-" + Cpf)]
    [InlineData("User-Agent", "Teste/1.0 (CPF " + CpfFormatado + ")")]
    [InlineData("User-Agent", "Teste/1.0 (tel 11 98765-4321)")]
    public void Cabecalhos_DoCliente_ComCpfOuTelefone_ViramMarcador(string nome, string valor)
    {
        var json = SanitizarCabecalhos(new HeaderDictionary { [nome] = valor }, null, Nenhum);

        AssertSemSegredos(json, Cpf, CpfFormatado, Telefone, "98765-4321");
        using var documento = JsonDocument.Parse(json);
        Assert.Equal(MarcadorFormato, documento.RootElement.GetProperty("request").GetProperty(nome).GetString());
    }

    [Theory]
    [InlineData("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7,es-419;q=0.6")]
    [InlineData("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8")]
    [InlineData("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:144.0) Gecko/20100101 Firefox/144.0")]
    [InlineData("User-Agent", "Mozilla/5.0 (Linux; Android 15; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.7390.122 Mobile Safari/537.36")]
    public void Cabecalhos_DeNavegadoresReais_SaoMantidos(string nome, string valor)
    {
        var json = SanitizarCabecalhos(new HeaderDictionary { [nome] = valor }, null, Nenhum);

        using var documento = JsonDocument.Parse(json);
        Assert.Equal(valor,documento.RootElement.GetProperty("request").GetProperty(nome).GetString());
    }

    [Fact]
    public void Excecao_GuardaTipoCodigoEPilha_SemMensagemNemDadosInternos()
    {
        Exception capturada;
        try
        {
            try
            {
                throw new InvalidOperationException($"Falha ao gravar {Email} com CPF {Cpf}");
            }
            catch (InvalidOperationException interna)
            {
                var externa = new DbUpdateException($"Erro no INSERT ... VALUES ('{Cpf}')", interna);
                externa.Data["cpf"] = Cpf;
                throw externa;
            }
        }
        catch (DbUpdateException ex)
        {
            capturada = ex;
        }

        var json = SanitizarExcecao(capturada, tratada: true);

        AssertSemSegredos(json, Email, Cpf, "INSERT", "Falha ao gravar");
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        Assert.Equal(typeof(DbUpdateException).FullName, raiz.GetProperty("tipo").GetString());
        Assert.Equal(MarcadorMensagem, raiz.GetProperty("mensagem").GetString());
        Assert.True(raiz.GetProperty("tratadaPelaApi").GetBoolean());
        Assert.Contains(raiz.GetProperty("pilha").EnumerateArray(), f => f.GetString()!.Contains(nameof(Excecao_GuardaTipoCodigoEPilha_SemMensagemNemDadosInternos)));
        Assert.Equal(typeof(InvalidOperationException).FullName, raiz.GetProperty("internas")[0].GetProperty("tipo").GetString());
    }

    [Fact]
    public void Excecao_ApiProblem_GuardaStatus_SemTitulo()
    {
        var json = SanitizarExcecao(ApiProblemException.Conflict($"O membro {Email} já está no evento."), tratada: true);

        AssertSemSegredos(json, Email);
        Assert.Equal(409, JsonDocument.Parse(json).RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("/api/Auth/login", true)]
    [InlineData("/API/AUTH/LOGIN", true)]
    [InlineData("/api/Auth/login/", true)]
    [InlineData("//api//Auth//login//", true)]
    [InlineData("/api/Auth/login.", true)]
    [InlineData("/api/Auth/logout", false)]
    [InlineData("/api/Auth/loginx", false)]
    [InlineData("/api/Usuarios/login", false)]
    public void Login_ExcluidoPorCaminhoNormalizado(string caminho, bool excluido)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = caminho;

        Assert.Equal(excluido, PoliticaAuditoriaHttp.Excluida(context));
    }

    [Theory]
    [InlineData("/api/Auth/refresh", true)]
    [InlineData("/api/auth/CSRF", true)]
    [InlineData("/api/Auth", true)]
    [InlineData("/api/Authx", false)]
    [InlineData("/api/Usuarios", false)]
    public void RotaDeCredenciais_PorPrefixo(string caminho, bool credenciais)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = caminho;

        Assert.Equal(credenciais, PoliticaAuditoriaHttp.RotaDeCredenciais(context));
    }

    internal static void AssertSemSegredos(string texto, params string[] segredos)
    {
        foreach (var segredo in segredos)
        {
            Assert.DoesNotContain(segredo, texto, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Cabecalhos_ValoresForaDoFormatoEsperado_ViramMarcador_EValidosPassam()
    {
        var request = new HeaderDictionary
        {
            ["Content-Length"] = "12a", ["Sec-WebSocket-Version"] = "13", ["Origin"] = "null",
        };
        var response = new HeaderDictionary
        {
            ["Content-Length"] = "dez", ["Content-Encoding"] = "gzip", ["Cache-Control"] = "no-store", ["Retry-After"] = "logo",
            ["Allow"] = "GET, POST", ["WWW-Authenticate"] = "(nada)",
        };
        using var aceitos = JsonDocument.Parse(SanitizarCabecalhos(request, response, Nenhum));
        var req = aceitos.RootElement.GetProperty("request");
        var resp = aceitos.RootElement.GetProperty("response");
        Assert.Equal((MarcadorFormato, "13", "null"), (req.GetProperty("Content-Length").GetString(), req.GetProperty("Sec-WebSocket-Version").GetString(),
            req.GetProperty("Origin").GetString()));
        Assert.Equal((MarcadorFormato, "gzip", "no-store", MarcadorFormato, "GET, POST", MarcadorFormato),
            (resp.GetProperty("Content-Length").GetString(), resp.GetProperty("Content-Encoding").GetString(), resp.GetProperty("Cache-Control").GetString(),
                resp.GetProperty("Retry-After").GetString(), resp.GetProperty("Allow").GetString(), resp.GetProperty("WWW-Authenticate").GetString()));

        var invalidos = new HeaderDictionary
        {
            ["Sec-WebSocket-Version"] = "treze", ["Origin"] = "javascript:alert(1)", ["Content-Length"] = "40",
        };
        var respostaInvalida = new HeaderDictionary
        {
            ["Content-Encoding"] = "gzip; x=@", ["Cache-Control"] = "max-age=<1>", ["Allow"] = "GET;@", ["Content-Length"] = "40",
        };
        using var recusados = JsonDocument.Parse(SanitizarCabecalhos(invalidos, respostaInvalida, Nenhum));
        var req2 = recusados.RootElement.GetProperty("request");
        var resp2 = recusados.RootElement.GetProperty("response");
        Assert.Equal((MarcadorFormato, MarcadorFormato, "40"), (req2.GetProperty("Sec-WebSocket-Version").GetString(), req2.GetProperty("Origin").GetString(),
            req2.GetProperty("Content-Length").GetString()));
        Assert.Equal((MarcadorFormato, MarcadorFormato, MarcadorFormato, "40"), (resp2.GetProperty("Content-Encoding").GetString(),
            resp2.GetProperty("Cache-Control").GetString(), resp2.GetProperty("Allow").GetString(), resp2.GetProperty("Content-Length").GetString()));
    }

    [Fact]
    public void ManterTexto_Url_SoPublicaECurta_EMarcadorDeTextoLivre_InformaOTamanhoQuandoHouver()
    {
        Assert.Equal("https://exemplo.org/a.png", ManterTexto(CategoriaCampoAuditoria.Url, "https://exemplo.org/a.png"));
        Assert.Null(ManterTexto(CategoriaCampoAuditoria.Url, "https://exemplo.org/" + new string('a', 300)));
        Assert.Null(ManterTexto(CategoriaCampoAuditoria.Url, "file:///etc/passwd"));
        Assert.Equal("[texto omitido]", Marcador(CategoriaCampoAuditoria.TextoLivre, null));
        Assert.Equal("[texto omitido: 5 caracteres]", Marcador(CategoriaCampoAuditoria.TextoLivre, "abcde"));
    }

    [Fact]
    public void Caminho_ComPadraoMasSemValoresDeRota_NaoReconheceParametro()
    {
        var padrao = RoutePatternFactory.Parse("api/Cep/{cep}");
        Assert.Equal("/api/Cep/[omitido]", SanitizarCaminho("/api/Cep/rua-das-flores", padrao, null, s => s is "api" or "Cep"));
    }

    [Fact]
    public void Excecao_RegistraCodigoSqlStatusDaApiOuSoOTipo()
    {
        string Codigo(Exception e) => JsonDocument.Parse(SanitizarExcecao(e, tratada: true)).RootElement.ToString();
        Assert.Contains("\"codigo\":2627", Codigo(SqlExceptionDeTeste.Criar(2627)));
        Assert.Contains("\"codigo\":547", Codigo(new DbUpdateException("x", SqlExceptionDeTeste.Criar(547))));
        Assert.Contains("\"status\":409", Codigo(ApiProblemException.Conflict("conflito")));
        var generica = Codigo(new TimeoutException("detalhe que não pode sair"));
        Assert.DoesNotContain("\"codigo\"", generica);
        Assert.DoesNotContain("\"status\"", generica);
        Assert.DoesNotContain("detalhe que não pode sair", generica);
    }

    [Fact]
    public void Json_IdentificadorNumericoNaoInteiro_ViraMarcador()
    {
        var (_, json) = SanitizarJson(Encoding.UTF8.GetBytes("""{"membroId":1.5,"eventoId":42}"""), 10_000);
        using var documento = JsonDocument.Parse(json);
        Assert.Equal(MarcadorFormato, documento.RootElement.GetProperty("membroId").GetString());
        Assert.Equal(42, documento.RootElement.GetProperty("eventoId").GetInt32());
    }
}
