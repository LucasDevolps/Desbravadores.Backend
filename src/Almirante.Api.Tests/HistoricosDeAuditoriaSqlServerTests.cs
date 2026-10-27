using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>
/// Colunas gravadas só pelos triggers de auditoria (ou só pelo banco) lidas pelo mapeamento do EF: o que a API nunca consulta no fluxo
/// normal continua mapeado para a coluna certa e com o valor do estado auditado.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class HistoricosDeAuditoriaSqlServerTests : IAsyncLifetime
{
    private const string Ip = "203.0.113.160";
    private SqlServerApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory();
        _admin = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        _admin.DefaultRequestHeaders.Add(AlmiranteApiFactory.TestRemoteIpHeader, Ip);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<T> Db<T>(Func<Almirante.Api.Data.AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(_factory, consulta);

    [Fact]
    public async Task HistoricoDeUsuario_GuardaOEnderecoCompletoEADataDeCriacaoDoEstadoAnterior()
    {
        var (membro, id) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, "DS");
        Assert.Equal(HttpStatusCode.OK, (await membro.PutAsJsonAsync("/api/Me", new { endereco = new { cep = "01001000", numero = "1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await membro.PutAsJsonAsync("/api/Me", new { telefone = "11987654321" })).StatusCode);

        var usuario = await Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));
        var historico = await Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == id).OrderByDescending(h => h.AlteradoEmUtc).FirstAsync());
        Assert.NotEqual(Guid.Empty, historico.Id);
        Assert.Equal(id, historico.UsuarioId);
        Assert.Equal(usuario.DataCriacao, historico.DataCriacao);
        Assert.Equal((usuario.EnderecoUnidade, usuario.EnderecoEstado, usuario.EnderecoRegiao, usuario.EnderecoIbge, usuario.EnderecoGia),
            (historico.EnderecoUnidade, historico.EnderecoEstado, historico.EnderecoRegiao, historico.EnderecoIbge, historico.EnderecoGia));
        Assert.Equal("01001000", historico.EnderecoCep);
        Assert.Null(historico.Telefone);
    }

    [Fact]
    public async Task LancamentoExcluido_CopiaIdentificacaoDatasMembroEVencimento()
    {
        var membro = (await TestHelpers.AddUsuarioAsync(_factory, "Membro auditado", "DS")).Id;
        var vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var criado = await _admin.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = membro, finalidade = "Mensalidade", descricao = "auditoria", categoria = "Clube", tipoFluxo = "Entrada", valor = 10m,
            vencimento = vencimento.ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var lancamentoId = (await criado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var original = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == lancamentoId));
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{lancamentoId}")
        {
            Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }),
        })).StatusCode);

        var copia = await Db(db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == lancamentoId));
        Assert.NotEqual(Guid.Empty, copia.Id);
        Assert.NotEqual(default, copia.ExcluidoEmUtc);
        Assert.Equal((membro, vencimento, (Guid?)null, original.DataCriacao), (copia.MembroId!.Value, copia.Vencimento, copia.OperacaoId, copia.DataCriacaoOriginal));
        Assert.Equal(Ip, copia.IpResponsavel);
    }

    [Fact]
    public async Task EventoExcluido_GeraHistoricoComIdProprio_EParticipanteNavegaAteOEvento()
    {
        var membro = (await TestHelpers.AddUsuarioAsync(_factory, "Participante auditado", "DS")).Id;
        var evento = await EventosSqlSupport.CriarAsync(_admin, membro);
        var participante = await Db(db => db.EventosMembros.AsNoTracking().Include(m => m.Evento).SingleAsync(m => m.EventoId == evento.Id));
        Assert.Equal(evento.Local, participante.Evento!.Local);

        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(_admin, evento.Id, "passeio cancelado", evento.Versao)).StatusCode);
        var historico = await Db(db => db.HistoricoEventos.AsNoTracking().SingleAsync(h => h.EventoId == evento.Id));
        Assert.NotEqual(Guid.Empty, historico.Id);
    }

    [Fact]
    public async Task Almoxarifado_TomboVemDoSequencial_EHistoricoApontaBemEDesejo()
    {
        var categoria = $"Aud-{Guid.NewGuid():N}"[..20];
        var bem = await _admin.PostAsJsonAsync("/api/Almoxarifado/Bens", new
        {
            nome = "Barraca", descricao = "Barraca iglu", categoria, estadoConservacao = "Bom", possuiValorComercial = false,
            dataAquisicao = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30).ToString("yyyy-MM-dd"), origem = "Doacao", localizacao = "Central",
        });
        Assert.Equal(HttpStatusCode.Created, bem.StatusCode);
        var dto = await bem.Content.ReadFromJsonAsync<JsonElement>();
        var bemId = dto.GetProperty("id").GetGuid();
        var entidade = await Db(db => db.BensPatrimoniais.AsNoTracking().SingleAsync(b => b.Id == bemId));
        Assert.Equal($"PAT-{entidade.NumeroTombo:000000}", dto.GetProperty("tombo").GetString());

        var desejo = await _admin.PostAsJsonAsync("/api/Almoxarifado/Desejos", new
        {
            nome = "Lona", descricao = "Lona 6x4", justificativa = "Chuva", tipoItem = "Patrimonial", quantidadeDesejada = 1, urgencia = "Urgente",
            categoria, dataNecessidade = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd"),
        });
        Assert.Equal(HttpStatusCode.Created, desejo.StatusCode);
        var desejoId = (await desejo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var historico = await Db(db => db.HistoricoAlmoxarifado.AsNoTracking().Where(h => h.BemId == bemId || h.DesejoId == desejoId).ToListAsync());
        Assert.Contains(historico, h => h.BemId == bemId && h.DesejoId is null);
        Assert.Contains(historico, h => h.DesejoId == desejoId && h.BemId is null);
        Assert.All(historico, h => Assert.Equal(Ip, h.IpResponsavel));
    }

    [Fact]
    public async Task RefreshEncadeiaOTokenSucessor_ECargoSemedadoNaoTemAtualizacao()
    {
        var client = _factory.CreateClient();
        await TestHelpers.LoginAsAdminAsync(client);
        await TestHelpers.AddCsrfAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/Auth/refresh", null)).StatusCode);

        var consumido = await Db(db => db.RefreshTokens.AsNoTracking().Include(t => t.ReplacedByToken)
            .Where(t => t.ConsumedAtUtc != null).OrderByDescending(t => t.ConsumedAtUtc).FirstAsync());
        Assert.Equal(consumido.ReplacedByTokenId, consumido.ReplacedByToken!.Id);
        Assert.Null(consumido.ReplacedByToken.ConsumedAtUtc);

        var cargo = await Db(db => db.Cargos.AsNoTracking().SingleAsync(c => c.Role == "DS"));
        Assert.Null(cargo.UltimaAtualizacao);
    }
}
