using Almirante.Api.Dtos;

namespace Almirante.Api.Tests.Builders;

public sealed class RegistrarEventoRequestBuilder
{
    private decimal _transporte = 100m;
    private decimal _alimentacao;
    private decimal _seguro;
    private readonly List<Guid> _membros = [Guid.Parse("11111111-1111-1111-1111-111111111111")];
    private List<EventoItemAdicionalRequest>? _itens;

    public RegistrarEventoRequestBuilder ComBase(decimal transporte, decimal alimentacao = 0m, decimal seguro = 0m)
    {
        _transporte = transporte;
        _alimentacao = alimentacao;
        _seguro = seguro;
        return this;
    }

    public RegistrarEventoRequestBuilder ComItem(string? titulo, decimal? valor)
    {
        (_itens ??= []).Add(new EventoItemAdicionalRequest { Titulo = titulo, Valor = valor });
        return this;
    }

    public RegistrarEventoRequestBuilder ComItensVazios()
    {
        _itens = [];
        return this;
    }

    public RegistrarEventoRequestBuilder ComMembros(int quantidade)
    {
        _membros.Clear();
        _membros.AddRange(Enumerable.Range(1, quantidade).Select(i => new Guid(i, 0, 0, new byte[8])));
        return this;
    }

    public RegistrarEventoRequest Build() => new()
    {
        Titulo = "Passeio no parque",
        DataEvento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10),
        Local = "Parque Ibirapuera",
        Transporte = new TransporteRequest { Valor = _transporte, EhGratis = false },
        Alimentacao = new AlimentacaoRequest { Individual = false, Valor = _alimentacao },
        SeguroObrigatorio = _seguro,
        Membros = _membros.ToList(),
        ItensAdicionais = _itens?.ToList(),
        IdempotencyKey = Guid.NewGuid().ToString(),
    };
}
