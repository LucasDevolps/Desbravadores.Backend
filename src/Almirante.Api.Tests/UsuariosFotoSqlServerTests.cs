using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public class FotosSqlFixture : IAsyncLifetime
{
    public SqlServerApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid AdminId { get; private set; }
    public ArmazenamentoComFalhas Armazenamento { get; private set; } = null!;
    public FalhaDeComandoInterceptor Falhas { get; } = new();

    public async Task InitializeAsync()
    {
        Factory = new SqlServerApiFactory(
            new Dictionary<string, string?> { ["Media:Images:UploadsPerUserPerWindow"] = "1000" },
            configureDb: o => o.AddInterceptors(Falhas),
            configureServices: s => s.AddSingleton<IFileStorage>(sp =>
                Armazenamento = new ArmazenamentoComFalhas(new LocalFileStorage(sp.GetRequiredService<IOptions<MediaOptions>>()))));
        Client = await TestHelpers.CreateAuthenticatedClientAsync(Factory);
        AdminId = await TestHelpers.WithDbAsync(Factory, db => db.Usuarios.Where(u => u.Email == AlmiranteApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class ArmazenamentoComFalhas(IFileStorage real) : IFileStorage
{
    public Func<Exception>? FalhaAoGravar { get; set; }
    public int FalhasAoApagar { get; set; }

    public Task SaveAsync(string chave, ReadOnlyMemory<byte> conteudo, CancellationToken ct) =>
        FalhaAoGravar is { } falha ? throw falha() : real.SaveAsync(chave, conteudo, ct);

    public Task<Stream?> OpenReadAsync(string chave, CancellationToken ct) => real.OpenReadAsync(chave, ct);

    public Task DeleteAsync(string chave, CancellationToken ct)
    {
        if (FalhasAoApagar > 0)
        {
            FalhasAoApagar--;
            throw new IOException("arquivo em uso (antivírus)");
        }

        return real.DeleteAsync(chave, ct);
    }

    public Task<IReadOnlyList<ArquivoArmazenado>> ListAsync(CancellationToken ct) => real.ListAsync(ct);
    public Task<int> PurgeIncompleteAsync(DateTime anterioresAUtc, CancellationToken ct) => real.PurgeIncompleteAsync(anterioresAUtc, ct);
}

public sealed class FalhaDeComandoInterceptor : DbCommandInterceptor
{
    public volatile Func<string, bool>? Predicado;

    private void Talvez(DbCommand command)
    {
        if (Predicado?.Invoke(command.CommandText) == true) throw new InvalidOperationException("falha injetada no banco");
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Talvez(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Talvez(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Talvez(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosFotoSqlServerTests(FotosSqlFixture fx) : IClassFixture<FotosSqlFixture>, IDisposable
{
    private const string IpTeste = "203.0.113.42";

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    public void Dispose()
    {
        fx.Falhas.Predicado = null;
        fx.Armazenamento.FalhaAoGravar = null;
        fx.Armazenamento.FalhasAoApagar = 0;
    }

    private static string Rota(Guid id) => $"/api/Usuarios/{id}/foto";

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private async Task<Guid> CriarAsync(string role = "DS")
    {
        var cargoId = await Db(db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());
        var response = await Client.PostAsJsonAsync("/api/Usuarios", new
        {
            nome = $"Foto SQL {Guid.NewGuid():N}", email = $"foto-{Guid.NewGuid():N}@local.dev", senha = $"Forte-{Guid.NewGuid():N}", cargoId,
            cpf = TestHelpers.NovoCpf(), rg = TestHelpers.RgFicticio,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Id;
    }

    private Task<HttpResponseMessage> EnviarAsync(Guid id, byte[]? arquivo = null, HttpClient? client = null, string nome = "foto.jpg", string tipo = "image/jpeg")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Rota(id)) { Content = FotosTestKit.Multipart(arquivo ?? FotosTestKit.Jpeg(400, 300), nome, tipo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return (client ?? Client).SendAsync(request);
    }

    private Task<HttpResponseMessage> ExcluirAsync(Guid id, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, Rota(id));
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return (client ?? Client).SendAsync(request);
    }

    private Task<string?> CaminhoAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().Where(u => u.Id == id).Select(u => u.FotoCaminho).SingleAsync());

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid id) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == id).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private string NoDisco(string chave) => Path.Combine(Factory.FotosPath, chave.Replace('/', Path.DirectorySeparatorChar));

    private string PastaDoUsuario(Guid id) => Path.Combine(Factory.FotosPath, "usuarios", id.ToString("N"));

    private string[] ArquivosDoUsuario(Guid id) => Directory.Exists(PastaDoUsuario(id)) ? Directory.GetFiles(PastaDoUsuario(id)) : [];

    [Fact]
    public async Task Enviar_Ler_Trocar_Excluir_ComHistoricoEDiscoConsistentes()
    {
        var id = await CriarAsync();

        var envio = await EnviarAsync(id, FotosTestKit.ComExif(FotosTestKit.Jpeg(1600, 1200), 6));
        Assert.True(envio.StatusCode == HttpStatusCode.OK, await envio.Content.ReadAsStringAsync());
        Assert.Equal(Rota(id), (string)JsonNode.Parse(await envio.Content.ReadAsStringAsync())!["fotoUrl"]!);
        var primeira = await CaminhoAsync(id);
        Assert.True(FotoChave.PertenceA(primeira, id));
        Assert.True(File.Exists(NoDisco(primeira!)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(NoDisco(primeira!)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(PastaDoUsuario(id)));
        }

        var baixada = await Client.GetByteArrayAsync(Rota(id));
        Assert.Equal(await File.ReadAllBytesAsync(NoDisco(primeira!)), baixada);
        Assert.Equal((768, 1024), FotosTestKit.Dimensoes(baixada));
        Assert.False(FotosTestKit.Contem(baixada, FotosTestKit.MarcadorExif));

        var troca = await EnviarAsync(id, FotosTestKit.Png(300, 300), nome: "outra.png", tipo: "image/png");
        Assert.Equal(HttpStatusCode.OK, troca.StatusCode);
        var segunda = await CaminhoAsync(id);
        Assert.NotEqual(primeira, segunda);
        Assert.False(File.Exists(NoDisco(primeira!)));
        Assert.Equal(new[] { NoDisco(segunda!) }, ArquivosDoUsuario(id));

        Assert.Equal(HttpStatusCode.NoContent, (await ExcluirAsync(id)).StatusCode);
        Assert.Null(await CaminhoAsync(id));
        Assert.False(Directory.Exists(PastaDoUsuario(id)));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(Rota(id))).StatusCode);
        var denovo = await ExcluirAsync(id);
        Assert.Equal(HttpStatusCode.NotFound, denovo.StatusCode);
        Assert.Contains("não tem foto", (string)JsonNode.Parse(await denovo.Content.ReadAsStringAsync())!["detail"]!);

        var historico = await HistoricoAsync(id);
        Assert.Equal(new string?[] { null, primeira, segunda }, historico.Select(h => h.FotoCaminho));
        Assert.All(historico, h =>
        {
            Assert.Equal(UsuarioHistorico.OperacaoUpdate, h.TipoOperacao);
            Assert.Equal(fx.AdminId, h.UsuarioResponsavelId);
            Assert.Equal(IpTeste, h.IpResponsavel);
        });
    }

    [Fact]
    public async Task UsuarioInexistente_404_EInativo_409NoEnvio_MasExclusaoDaFotoContinuaPermitida()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ExcluirAsync(Guid.NewGuid())).StatusCode);

        var id = await CriarAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        var excluirUsuario = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{id}");
        excluirUsuario.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(excluirUsuario)).StatusCode);
        Assert.NotNull(await CaminhoAsync(id));

        var envio = await EnviarAsync(id);
        Assert.Equal(HttpStatusCode.Conflict, envio.StatusCode);
        Assert.Equal("Usuário inativo.", (string)JsonNode.Parse(await envio.Content.ReadAsStringAsync())!["title"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await ExcluirAsync(id)).StatusCode);
        Assert.Null(await CaminhoAsync(id));
        Assert.Empty(ArquivosDoUsuario(id));
    }

    [Fact]
    public async Task SoAdministradorGerenciaAFotoDeAdministrador()
    {
        var admin = await CriarAsync("ADM");
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin)).StatusCode);
        var caminho = await CaminhoAsync(admin);
        var (diretor, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DIR");
        using var _ = diretor;

        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(admin, client: diretor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ExcluirAsync(admin, diretor)).StatusCode);
        Assert.Equal(caminho, await CaminhoAsync(admin));
        Assert.Equal(HttpStatusCode.OK, (await diretor.GetAsync(Rota(admin))).StatusCode);

        var membro = await CriarAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(membro, client: diretor)).StatusCode);
    }

    [Fact]
    public async Task ImagemRecusada_NaoGravaNadaNoDiscoNemNoBanco()
    {
        var id = await CriarAsync();

        var bomba = await EnviarAsync(id, FotosTestKit.PngBomba(50_000, 50_000), nome: "bomba.png", tipo: "image/png");
        var disfarcado = await EnviarAsync(id, "MZ"u8.ToArray().Concat(new byte[512]).ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bomba.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, disfarcado.StatusCode);
        Assert.Null(await CaminhoAsync(id));
        Assert.Empty(ArquivosDoUsuario(id));
        Assert.Empty(await HistoricoAsync(id));
    }

    [Fact]
    public async Task EnviosConcorrentesDoMesmoUsuario_SeSerializam_ESobraExatamenteUmArquivo()
    {
        var id = await CriarAsync();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => EnviarAsync(id, FotosTestKit.Jpeg(200 + i, 200))));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var caminho = await CaminhoAsync(id);
        Assert.Equal(new[] { NoDisco(caminho!) }, ArquivosDoUsuario(id));
        Assert.Equal(5, (await HistoricoAsync(id)).Count);
    }

    [Theory]
    [InlineData("outro")]
    [InlineData("maiusculo")]
    [InlineData("fora")]
    [InlineData("extensao")]
    public async Task Banco_RecusaCaminhoForaDoFormatoOuDeOutroUsuario(string caso)
    {
        var id = await CriarAsync();
        var aleatorio = Guid.NewGuid().ToString("N");
        var caminho = caso switch
        {
            "outro" => FotoChave.Gerar(Guid.NewGuid()),
            "maiusculo" => $"usuarios/{id:N}/{aleatorio.ToUpperInvariant()}.webp",
            "fora" => $"usuarios/{id:N}/../../../../{aleatorio[..20]}.webp",
            _ => $"usuarios/{id:N}/{aleatorio}.webq",
        };

        var erro = await Assert.ThrowsAsync<SqlException>(() => EventosSqlSupport.EscalarAsync<int>(Factory, """
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET FotoCaminho = @caminho WHERE Id = @id;
            """, ("@resp", fx.AdminId), ("@caminho", caminho), ("@id", id)));

        Assert.Equal(547, erro.Number);
        Assert.Contains("CK_Usuarios_FotoCaminho", erro.Message);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("permissao")]
    public async Task FalhaAoGravarNoDisco_Retorna503_EOCadastroFicaIntacto(string tipo)
    {
        var id = await CriarAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        var anterior = await CaminhoAsync(id);
        fx.Armazenamento.FalhaAoGravar = () => tipo == "io" ? new IOException("disco cheio") : new UnauthorizedAccessException("sem permissão");

        var response = await EnviarAsync(id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Armazenamento de fotos indisponível.", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["title"]!);
        Assert.Equal(anterior, await CaminhoAsync(id));
        Assert.True(File.Exists(NoDisco(anterior!)));
    }

    [Fact]
    public async Task FalhaInesperadaAoGravar_NaoViraErroDeArmazenamento()
    {
        var id = await CriarAsync();
        fx.Armazenamento.FalhaAoGravar = () => new InvalidOperationException("bug");

        Assert.Equal(HttpStatusCode.InternalServerError, (await EnviarAsync(id)).StatusCode);
        Assert.Null(await CaminhoAsync(id));
    }

    [Fact]
    public async Task FalhaNoBancoDepoisDeGravarOArquivo_ApagaOArquivoNovo_EMantemOAnterior()
    {
        var id = await CriarAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        var anterior = await CaminhoAsync(id);
        fx.Falhas.Predicado = sql => sql.StartsWith("UPDATE dbo.Usuarios SET FotoCaminho", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.InternalServerError, (await EnviarAsync(id)).StatusCode);

        fx.Falhas.Predicado = null;
        Assert.Equal(anterior, await CaminhoAsync(id));
        Assert.Equal(new[] { NoDisco(anterior!) }, ArquivosDoUsuario(id));
    }

    [Fact]
    public async Task FalhaNoBancoSemComoConfirmar_MantemOArquivoNovoParaAFaxina()
    {
        var id = await CriarAsync();
        fx.Falhas.Predicado = sql => sql.StartsWith("UPDATE dbo.Usuarios SET FotoCaminho", StringComparison.Ordinal)
                                     || (sql.Contains("EXISTS", StringComparison.Ordinal) && sql.Contains("[FotoCaminho] =", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.InternalServerError, (await EnviarAsync(id)).StatusCode);

        fx.Falhas.Predicado = null;
        Assert.Null(await CaminhoAsync(id));
        Assert.Single(ArquivosDoUsuario(id));
        Assert.Contains(Factory.Logs.Entries, e => e.Contains("Não foi possível confirmar se a foto"));
    }

    [Fact]
    public async Task FalhaAoApagarAFotoAnterior_TentaDeNovo_EDesisteComAvisoSemFalharOEnvio()
    {
        var id = await CriarAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        var primeira = await CaminhoAsync(id);

        fx.Armazenamento.FalhasAoApagar = 1;
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        Assert.False(File.Exists(NoDisco(primeira!)));

        var segunda = await CaminhoAsync(id);
        fx.Armazenamento.FalhasAoApagar = 3;
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(id)).StatusCode);
        Assert.True(File.Exists(NoDisco(segunda!)));
        Assert.Contains(Factory.Logs.Entries, e => e.Contains("a faxina diária tenta de novo"));
    }

    [Fact]
    public async Task Servico_ForaDoSqlServer_RecusaEnviarEExcluir_AntesDeTocarNoDisco()
    {
        using var emMemoria = new AlmiranteApiFactory();
        using var scope = emMemoria.Services.CreateScope();
        var servico = scope.ServiceProvider.GetRequiredService<UsuarioFotoService>();

        await Assert.ThrowsAsync<NotSupportedException>(() => servico.EnviarAsync(
            new EnviarFotoUsuarioRequest(Guid.NewGuid(), FotosTestKit.Jpeg(100, 100), FormatoFoto.Jpeg, Guid.NewGuid(), IpTeste, "ADM"), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => servico.ExcluirAsync(
            new ExcluirFotoUsuarioRequest(Guid.NewGuid(), Guid.NewGuid(), IpTeste, "ADM"), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(emMemoria.FotosPath, "*", SearchOption.AllDirectories));
    }
}
