using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Almirante.Api.Infrastructure;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

// Foto de perfil (issue #116) via HTTP sem SQL Server: autorização, recusas antes do serviço, leitura (GET), limites de
// envio e documentação. Envio/troca/exclusão gravados de verdade: UsuariosFotoSqlServerTests.
public sealed class UsuariosFotoTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static string Rota(Guid id) => $"/api/Usuarios/{id}/foto";

    // Usuário com foto gravada direto no banco (InMemory) e no armazenamento da fábrica.
    private async Task<(Guid Id, string Chave, byte[] Conteudo)> SemearComFotoAsync(AlmiranteApiFactory fabrica, string? chave = null)
    {
        var usuario = await TestHelpers.AddUsuarioAsync(fabrica, $"Com foto {Guid.NewGuid():N}", "DS");
        var foto = chave ?? FotoChave.Gerar(usuario.Id);
        var conteudo = FotosTestKit.Webp(120, 120);
        await fabrica.Services.GetRequiredService<IFileStorage>().SaveAsync(foto, conteudo, CancellationToken.None);
        await TestHelpers.WithDbAsync(fabrica, async db =>
        {
            var salvo = await db.Usuarios.SingleAsync(u => u.Id == usuario.Id);
            salvo.FotoCaminho = foto;
            return await db.SaveChangesAsync();
        });
        return (usuario.Id, foto, conteudo);
    }

    // ------------------------------------------------------------------ autorização e recusas antes do serviço

    [Fact]
    public async Task SemToken_Retorna401_EmTodasAsRotasDaFoto()
    {
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync(Rota(id), FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Rota(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(Rota(id))).StatusCode);
    }

    [Fact]
    public async Task PapelForaDaDiretoria_Retorna403()
    {
        var (client, proprioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using var _ = client;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(Rota(proprioId), FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Rota(proprioId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(Rota(proprioId))).StatusCode);
    }

    [Fact]
    public async Task ContentEncoding_Retorna415_EContentLengthAcimaDoLimite_Retorna413()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var comCompressao = FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100));
        comCompressao.Headers.ContentEncoding.Add("gzip");

        var compressao = await client.PutAsync(Rota(Guid.NewGuid()), comCompressao);
        var grande = await client.PutAsync(Rota(Guid.NewGuid()), FotosTestKit.Multipart(new byte[10 * 1024 * 1024 + 1]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, compressao.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, grande.StatusCode);
        Assert.Equal("application/problem+json", grande.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Erro500_EmProducao_NaoDevolveDetalhesInternos_ENadaChegaAoDisco()
    {
        // InMemory => o serviço recusa com NotSupportedException antes de decodificar ou gravar (falha inesperada real).
        using var fabrica = new ConfiguredApiFactory("Production");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(fabrica);
        var alvo = await TestHelpers.AddUsuarioAsync(fabrica, "Alvo da falha", "DS");

        var respostas = new[]
        {
            await client.PutAsync(Rota(alvo.Id), FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100))),
            await client.DeleteAsync(Rota(alvo.Id)),
        };

        foreach (var response in respostas)
        {
            var corpo = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            foreach (var proibido in new[] { "NotSupportedException", "exigem SQL Server", "Almirante.Api.Services", " at ", "StackTrace" })
                Assert.DoesNotContain(proibido, corpo);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(fabrica.FotosPath, "*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------ leitura

    [Fact]
    public async Task Get_UsuarioInexistenteOuSemFoto_Retorna404()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var semFoto = await TestHelpers.AddUsuarioAsync(factory, "Sem foto", "DS");

        foreach (var id in new[] { Guid.NewGuid(), semFoto.Id })
        {
            var response = await client.GetAsync(Rota(id));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Foto não encontrada.", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["title"]!);
        }
    }

    [Fact]
    public async Task Get_ComFoto_EntregaOWebpComHeadersDeSeguranca_E304ComOMesmoETag()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (id, chave, conteudo) = await SemearComFotoAsync(factory);

        var response = await client.GetAsync(Rota(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(conteudo, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FotoChave.ETag(chave), response.Headers.ETag?.ToString());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("foto.webp", response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Accept-Ranges"));

        var condicional = new HttpRequestMessage(HttpMethod.Get, Rota(id));
        condicional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(condicional)).StatusCode);
    }

    [Fact]
    public async Task Get_CaminhoGravadoForaDoFormatoOuDeOutroUsuario_NaoEhServido()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (id, _, _) = await SemearComFotoAsync(factory, chave: FotoChave.Gerar(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Rota(id))).StatusCode);
        Assert.Contains(factory.Logs.Entries, e => e.Contains("FotoCaminho inconsistente") && e.Contains(id.ToString()));
    }

    [Fact]
    public async Task Get_ArquivoQueSumiuDoDisco_Retorna404ELogaErro()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (id, chave, _) = await SemearComFotoAsync(factory);
        await factory.Services.GetRequiredService<IFileStorage>().DeleteAsync(chave, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Rota(id))).StatusCode);
        Assert.Contains(factory.Logs.Entries, e => e.Contains("não existe no armazenamento") && e.Contains(id.ToString()));
    }

    [Fact]
    public async Task Listagem_TrazFotoUrlSoParaQuemTemFoto_ESemOCaminhoFisico()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (comFoto, chave, _) = await SemearComFotoAsync(factory);
        var semFoto = await TestHelpers.AddUsuarioAsync(factory, "Lista sem foto", "DS");

        var json = await client.GetStringAsync("/api/Usuarios");
        var lista = JsonNode.Parse(json)!.AsArray();

        Assert.Equal(Rota(comFoto), (string)lista.Single(u => (string)u!["id"]! == comFoto.ToString())!["fotoUrl"]!);
        Assert.Null(lista.Single(u => (string)u!["id"]! == semFoto.Id.ToString())!["fotoUrl"]);
        Assert.DoesNotContain(chave, json);
        Assert.DoesNotContain("fotoCaminho", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("possuiFoto", json, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ limites de envio (429)

    [Fact]
    public async Task LimitePorUsuario_RecusaComRetryAfter_ContandoTambemEnviosRecusados()
    {
        using var fabrica = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Media:Images:UploadsPerUserPerWindow"] = "2" });
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(fabrica);
        HttpContent Corpo()
        {
            var corpo = FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100));
            corpo.Headers.ContentEncoding.Add("gzip");   // recusado (415) depois de contar na janela
            return corpo;
        }

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PutAsync(Rota(Guid.NewGuid()), Corpo())).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PutAsync(Rota(Guid.NewGuid()), Corpo())).StatusCode);
        var terceiro = await client.PutAsync(Rota(Guid.NewGuid()), Corpo());

        Assert.Equal(HttpStatusCode.TooManyRequests, terceiro.StatusCode);
        Assert.True(int.Parse(terceiro.Headers.RetryAfter!.ToString()) >= 1);
        Assert.Equal("Muitos envios de foto.", (string)JsonNode.Parse(await terceiro.Content.ReadAsStringAsync())!["title"]!);
        Assert.Contains(fabrica.Logs.Entries, e => e.Contains("Envio de foto recusado (429)"));
    }

    [Fact]
    public async Task FilaDeProcessamentoCheia_Recusa429_SemLerOCorpo()
    {
        using var fabrica = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Media:Images:MaxConcurrentProcessing"] = "1" });
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(fabrica);
        var limitador = fabrica.Services.GetRequiredService<LimitadorEnvioFotos>();

        // 1 vaga + fila de 2: tudo ocupado, a próxima reserva falha na hora.
        using var emUso = await limitador.ReservarProcessamentoAsync(CancellationToken.None);
        using var cancelar = new CancellationTokenSource();
        var naFila = new[] { limitador.ReservarProcessamentoAsync(cancelar.Token).AsTask(), limitador.ReservarProcessamentoAsync(cancelar.Token).AsTask() };

        var response = await client.PutAsync(Rota(Guid.NewGuid()), FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100)));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter!.ToString());
        Assert.Equal("Processamento de fotos ocupado.", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["title"]!);
        await cancelar.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(naFila));
    }

    // ------------------------------------------------------------------ identidade sem id (defesa mantida no controller)

    private sealed class SemIdentificadorHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identidade = new ClaimsIdentity([new Claim("role", "DIR")], "Teste", "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), "Teste")));
        }
    }

    [Fact]
    public async Task IdentidadeAutenticadaSemId_Retorna401NasRotasQueRegistramOResponsavel()
    {
        using var fabrica = new ConfiguredApiFactory(services: s =>
            s.AddAuthentication("Teste").AddScheme<AuthenticationSchemeOptions, SemIdentificadorHandler>("Teste", _ => { }));
        using var client = fabrica.CreateClient();
        var id = Guid.NewGuid();

        var respostas = new[]
        {
            await client.PutAsync(Rota(id), FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100))),
            await client.DeleteAsync(Rota(id)),
            await client.PutAsJsonAsync($"/api/Usuarios/{id}", new { nome = "x", email = "x@local.dev", cargoId = Guid.NewGuid() }),
            await client.DeleteAsync($"/api/Usuarios/{id}"),
        };

        foreach (var response in respostas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Identidade autenticada inválida.", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["title"]!);
        }
    }

    // ------------------------------------------------------------------ documentação

    [Fact]
    public async Task Swagger_DocumentaOCorpoMultipartComOCampoArquivoBinario()
    {
        using var client = factory.CreateClient();

        var documento = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!;
        var put = documento["paths"]!["/api/Usuarios/{id}/foto"]!["put"]!;
        var schema = put["requestBody"]!["content"]!["multipart/form-data"]!["schema"]!;

        Assert.True((bool)put["requestBody"]!["required"]!);
        Assert.Equal("binary", (string)schema["properties"]!["arquivo"]!["format"]!);
        Assert.Contains("arquivo", schema["required"]!.AsArray().Select(r => (string)r!));
        Assert.Null(documento["paths"]!["/api/Usuarios/{id}"]!["put"]!["requestBody"]!["content"]!["multipart/form-data"]);
    }
}
