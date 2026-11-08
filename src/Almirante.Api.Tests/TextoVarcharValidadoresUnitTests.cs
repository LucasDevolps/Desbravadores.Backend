using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Validation.Eventos;
using Almirante.Api.Validation.Lancamentos;
using FluentValidation;

namespace Almirante.Api.Tests;

/// <summary>
/// Testes unitários das travas de texto de lançamentos e eventos: descrição, local e motivos são gravados em colunas varchar
/// (página 1252), então emoji, ideograma ou caractere de controle é recusado com 400 em vez de virar "?" no banco.
/// </summary>
public sealed class TextoVarcharValidadoresUnitTests
{
    public static TheoryData<string> Invalidos => ["😀", "中", "\u0007"];

    private const string Acentuado = "Doação de outubro — São João";

    private static bool Recusa<T>(IValidator<T> validator, T request, string propriedade) =>
        validator.Validate(request).Errors.Any(e => e.PropertyName.Equals(propriedade, StringComparison.OrdinalIgnoreCase));

    private static RegistrarLancamentoRequest Lancamento(string? descricao, string? chave = null) => new()
    {
        MembroId = Guid.NewGuid(), Finalidade = LancamentoFinalidades.Mensalidade, Descricao = descricao, Categoria = CategoriaLancamento.Clube,
        TipoFluxo = TipoFluxoLancamento.Entrada, Valor = 10m, Vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), IdempotencyKey = chave,
    };

    private static RegistrarEventoRequest Evento(string local) => new()
    {
        DataEvento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), Local = local,
        Transporte = new TransporteRequest { Valor = 10m, EhGratis = false }, Alimentacao = new AlimentacaoRequest { Individual = false, Valor = 5m },
        SeguroObrigatorio = 1m, Membros = [Guid.NewGuid()], IdempotencyKey = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void TextosAcentuadosDaPagina1252_SaoAceitos()
    {
        Assert.True(new RegistrarLancamentoRequestValidator(TimeProvider.System).Validate(Lancamento(Acentuado)).IsValid);
        Assert.False(Recusa(new UpdateLancamentoRequestValidator(TimeProvider.System), new UpdateLancamentoRequest { Descricao = Acentuado }, "Descricao"));
        Assert.True(new DeleteLancamentoRequestValidator().Validate(new DeleteLancamentoRequest { Motivo = Acentuado }).IsValid);
        Assert.True(new RegistrarEventoRequestValidator().Validate(Evento(Acentuado)).IsValid);
        Assert.False(Recusa(new UpdateEventoRequestValidator(), new UpdateEventoRequest { Local = Acentuado, Motivo = Acentuado }, "Motivo"));
        Assert.False(Recusa(new DeleteEventoRequestValidator(), new DeleteEventoRequest { Motivo = Acentuado }, "Motivo"));
    }

    [Theory]
    [MemberData(nameof(Invalidos))]
    public void ForaDaPagina1252_EhRecusado_EmTodosOsTextosLivres(string invalido)
    {
        var texto = $"Texto {invalido}";
        Assert.True(Recusa(new RegistrarLancamentoRequestValidator(TimeProvider.System), Lancamento(texto), "Descricao"));
        Assert.True(Recusa(new UpdateLancamentoRequestValidator(TimeProvider.System), new UpdateLancamentoRequest { Descricao = texto }, "Descricao"));
        Assert.True(Recusa(new DeleteLancamentoRequestValidator(), new DeleteLancamentoRequest { Motivo = texto }, "Motivo"));
        Assert.True(Recusa(new RegistrarEventoRequestValidator(), Evento(texto), "Local"));
        Assert.True(Recusa(new UpdateEventoRequestValidator(), new UpdateEventoRequest { Local = texto }, "Local"));
        Assert.True(Recusa(new UpdateEventoRequestValidator(), new UpdateEventoRequest { Local = "Parque", Motivo = texto }, "Motivo"));
        Assert.True(Recusa(new DeleteEventoRequestValidator(), new DeleteEventoRequest { Motivo = texto }, "Motivo"));
    }

    [Fact]
    public void DescricaoDoLancamento_AcimaDe500Caracteres_EhRecusada()
    {
        Assert.True(Recusa(new RegistrarLancamentoRequestValidator(TimeProvider.System), Lancamento(new string('a', 501)), "Descricao"));
        Assert.False(Recusa(new RegistrarLancamentoRequestValidator(TimeProvider.System), Lancamento(new string('a', 500)), "Descricao"));
    }

    [Fact]
    public void IdempotencyKeyDoLancamento_SoAscii()
    {
        Assert.True(Recusa(new RegistrarLancamentoRequestValidator(TimeProvider.System), Lancamento("x", "chave-ação"), "IdempotencyKey"));
        Assert.False(Recusa(new RegistrarLancamentoRequestValidator(TimeProvider.System), Lancamento("x", Guid.NewGuid().ToString()), "IdempotencyKey"));
    }
}
