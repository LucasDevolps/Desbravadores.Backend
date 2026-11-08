using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Tests;

/// <summary>
/// Ordem padrão da listagem de lançamentos (issue #99). Cada teste usa uma factory nova, então o banco
/// só tem o lançamento do seed (Pendente) além dos cenários abaixo; as asserções olham a posição relativa
/// dos lançamentos do cenário e, na ordem sem filtros, a prioridade de status da página inteira.
/// </summary>
public abstract class LancamentosOrdenacaoTestsBase : IAsyncLifetime
{
    // Datas fixas e Ids que diferem só no último byte: o desempate por Id fica igual no InMemory (Guid.CompareTo)
    // e no SQL Server (uniqueidentifier compara os bytes 10-15 primeiro).
    private static readonly DateOnly Base = new(2031, 3, 15);
    private readonly byte[] _prefixoId = Guid.NewGuid().ToByteArray();
    private readonly string _marcador = $"Ordenacao {Guid.NewGuid():N}";
    private AlmiranteApiFactory _factory = null!;
    private HttpClient _client = null!;

    private Guid PagoAntigo => Id(1);
    private Guid PagoRecente => Id(2);
    private Guid AtrasadoAntigo => Id(3);
    private Guid AtrasadoRecente => Id(4);
    private Guid PendenteProximo => Id(5);
    private Guid PendenteDistanteA => Id(6);
    private Guid PendenteDistanteB => Id(7);

    protected abstract AlmiranteApiFactory CriarFactory();

    public async Task InitializeAsync()
    {
        _factory = CriarFactory();
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        var membro = await TestHelpers.AddUsuarioAsync(_factory, "Membro ordenação", "DS");

        await TestHelpers.WithDbAsync(_factory, db =>
        {
            db.Lancamentos.AddRange(
                Novo(PagoAntigo, StatusLancamento.Pago, Base.AddDays(-10)),
                Novo(PagoRecente, StatusLancamento.Pago, Base.AddDays(60)),
                Novo(AtrasadoAntigo, StatusLancamento.Atrasado, Base.AddDays(-30)),
                Novo(AtrasadoRecente, StatusLancamento.Atrasado, Base.AddDays(-10)),
                Novo(PendenteProximo, StatusLancamento.Pendente, Base.AddDays(10)),
                Novo(PendenteDistanteA, StatusLancamento.Pendente, Base.AddDays(20)),
                Novo(PendenteDistanteB, StatusLancamento.Pendente, Base.AddDays(20)));
            return db.SaveChangesAsync();
        });

        Lancamento Novo(Guid id, StatusLancamento status, DateOnly vencimento) => new()
        {
            Id = id, MembroId = membro.Id, Finalidade = LancamentoFinalidades.Mensalidade, Descricao = _marcador,
            Categoria = CategoriaLancamento.Clube, TipoFluxo = TipoFluxoLancamento.Entrada, Valor = 25m,
            Vencimento = vencimento, Status = status,
        };
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Guid Id(byte ultimoByte)
    {
        var bytes = (byte[])_prefixoId.Clone();
        bytes[15] = ultimoByte;
        return new Guid(bytes);
    }

    private Guid[] OrdemPorPrioridade =>
        [AtrasadoRecente, AtrasadoAntigo, PendenteDistanteA, PendenteDistanteB, PendenteProximo, PagoRecente, PagoAntigo];

    private Guid[] OrdemPorVencimento =>
        [PagoRecente, PendenteDistanteA, PendenteDistanteB, PendenteProximo, PagoAntigo, AtrasadoRecente, AtrasadoAntigo];

    private async Task<LancamentosResponse> ListarAsync(string query)
    {
        var response = await _client.GetAsync($"/api/Lancamentos?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LancamentosResponse>())!;
    }

    private Guid[] DoCenario(IEnumerable<LancamentoDto> itens)
    {
        var cenario = OrdemPorPrioridade.ToHashSet();
        return itens.Select(i => i.Id).Where(cenario.Contains).ToArray();
    }

    private static int Prioridade(StatusLancamento status) => status switch
    {
        StatusLancamento.Atrasado => 0,
        StatusLancamento.Pendente => 1,
        _ => 2,
    };

    [Theory]
    [InlineData("pageSize=100")]
    [InlineData("pageSize=100&status=Todos&finalidade=Todos")]
    [InlineData("pageSize=100&search=%20&status=%20&finalidade=%20")]
    public async Task SemFiltros_OrdenaAtrasadoPendentePago_ComVencimentoDescEIdComoDesempate(string query)
    {
        var lista = await ListarAsync(query);

        Assert.Equal(OrdemPorPrioridade, DoCenario(lista.Items));
        var prioridades = lista.Items.Select(i => Prioridade(i.Status)).ToArray();
        Assert.Equal(prioridades.Order(), prioridades);
        Assert.Equal(lista.Total, lista.Items.Count);
    }

    [Fact]
    public async Task SemFiltros_OrdemConsistenteEntreAsPaginas()
    {
        var completa = await ListarAsync("pageSize=100");
        var primeira = await ListarAsync("page=1&pageSize=2");
        var paginas = new List<LancamentoDto>(primeira.Items);
        for (var page = 2; page <= primeira.TotalPages; page++)
        {
            var pagina = await ListarAsync($"page={page}&pageSize=2");
            Assert.Equal(primeira.Total, pagina.Total);
            Assert.True(pagina.Items.Count <= 2);
            paginas.AddRange(pagina.Items);
        }

        Assert.Equal((int)Math.Ceiling(completa.Total / 2d), primeira.TotalPages);
        Assert.Equal(completa.Items.Select(i => i.Id), paginas.Select(i => i.Id));
        Assert.Equal(OrdemPorPrioridade, DoCenario(paginas));
    }

    [Fact]
    public async Task ComFiltroDeBusca_MantemOrdemPorVencimentoDesc()
    {
        var lista = await ListarAsync($"pageSize=100&search={Uri.EscapeDataString(_marcador)}");

        Assert.Equal(OrdemPorVencimento, lista.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ComFiltroDeFinalidade_MantemOrdemPorVencimentoDesc()
    {
        var lista = await ListarAsync($"pageSize=100&finalidade={LancamentoFinalidades.Mensalidade}");

        Assert.Equal(OrdemPorVencimento, DoCenario(lista.Items));
    }

    [Fact]
    public async Task ComFiltroDeStatus_MantemOrdemPorVencimentoDesc_ESoOStatusFiltrado()
    {
        var lista = await ListarAsync("pageSize=100&status=Pendente");

        Assert.All(lista.Items, i => Assert.Equal(StatusLancamento.Pendente, i.Status));
        Assert.Equal([PendenteDistanteA, PendenteDistanteB, PendenteProximo], DoCenario(lista.Items));
        Assert.Equal(lista.Items.OrderByDescending(i => i.Vencimento).Select(i => i.Vencimento), lista.Items.Select(i => i.Vencimento));
    }

    [Fact]
    public async Task ComFiltroDeVencimento_NaoAplicaPrioridadeDeStatus()
    {
        var lista = await ListarAsync($"pageSize=100&vencimento={Base.AddDays(-10):yyyy-MM-dd}");

        // Mesmo vencimento: só o Id desempata, então o Pago (Id menor) vem antes do Atrasado.
        Assert.Equal([PagoAntigo, AtrasadoRecente], lista.Items.Select(i => i.Id));
    }
}

public sealed class LancamentosOrdenacaoTests : LancamentosOrdenacaoTestsBase
{
    protected override AlmiranteApiFactory CriarFactory() => new();
}

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosOrdenacaoSqlServerTests : LancamentosOrdenacaoTestsBase
{
    protected override AlmiranteApiFactory CriarFactory() => new SqlServerApiFactory();
}
