using Almirante.Api.Dtos;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Eventos;

// Regras de conteúdo compartilhadas por POST e PUT. O campo "membros" já chega normalizado como lista
// (MembrosJsonConverter), então estas regras valem igualmente para "GUID" e "GUID[]".
public abstract class EventoConteudoValidator<T> : AbstractValidator<T> where T : EventoConteudoRequest
{
    protected EventoConteudoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.DataEvento).NotNull().WithMessage("dataEvento é obrigatório (yyyy-MM-dd).");

        RuleFor(x => x.Local)
            .Must(local => EventoRegras.NormalizarLocal(local).Length > 0).WithMessage("local é obrigatório.")
            .Must(local => EventoRegras.NormalizarLocal(local).Length <= EventoRegras.LocalMaximo)
            .WithMessage($"local deve ter no máximo {EventoRegras.LocalMaximo} caracteres.");

        RuleFor(x => x.Transporte).NotNull().WithMessage("transporte é obrigatório (objeto com ehGratis e valor).");
        RuleFor(x => x.Transporte!.EhGratis).NotNull().WithMessage("transporte.ehGratis é obrigatório.").When(x => x.Transporte is not null);
        RuleFor(x => x.Transporte!.Valor).NotNull().WithMessage("transporte.valor é obrigatório (pode ser zero).").When(x => x.Transporte is not null);
        // Valor ignorado por ehGratis=true não é validado como regra de negócio (é normalizado para zero).
        RuleFor(x => x.Transporte!.Valor!.Value).Must(EventoRegras.ValorValido).WithMessage(MensagemValor("transporte.valor")).OverridePropertyName("Transporte.Valor")
            .When(x => x.Transporte is { EhGratis: false, Valor: not null });

        RuleFor(x => x.Alimentacao).NotNull().WithMessage("alimentacao é obrigatório (objeto com individual e valor).");
        RuleFor(x => x.Alimentacao!.Individual).NotNull().WithMessage("alimentacao.individual é obrigatório.").When(x => x.Alimentacao is not null);
        RuleFor(x => x.Alimentacao!.Valor).NotNull().WithMessage("alimentacao.valor é obrigatório (pode ser zero).").When(x => x.Alimentacao is not null);
        RuleFor(x => x.Alimentacao!.Valor!.Value).Must(EventoRegras.ValorValido).WithMessage(MensagemValor("alimentacao.valor")).OverridePropertyName("Alimentacao.Valor")
            .When(x => x.Alimentacao is { Individual: false, Valor: not null });

        RuleFor(x => x.SeguroObrigatorio).NotNull().WithMessage("seguroObrigatorio é obrigatório (pode ser zero).");
        RuleFor(x => x.SeguroObrigatorio!.Value).Must(EventoRegras.ValorValido).WithMessage(MensagemValor("seguroObrigatorio")).OverridePropertyName("SeguroObrigatorio")
            .When(x => x.SeguroObrigatorio.HasValue);

        RuleFor(x => x.Membros).NotNull().WithMessage("membros é obrigatório (um GUID ou um array de GUIDs).")
            .Must(m => m!.Count > 0).WithMessage("membros não pode ser vazio.")
            .Must(m => m!.Count <= MembrosJsonConverter.MaximoMembros).WithMessage($"membros aceita no máximo {MembrosJsonConverter.MaximoMembros} itens.")
            .Must(m => m!.All(id => id != Guid.Empty)).WithMessage("membros não pode conter GUID vazio.")
            .Must(m => m!.Distinct().Count() == m!.Count)
            .WithMessage(m => "membros não pode conter GUIDs repetidos: " +
                string.Join(", ", m.Membros!.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)) + ".");

        // Overflow do total (valorPorMembro × quantidade) só faz sentido quando os componentes são válidos.
        RuleFor(x => x).Must(TotalCabeNoBanco).WithName("total").WithMessage("O total do evento excede o limite monetário suportado.")
            .When(ConteudoNumericoValido);
    }

    protected static string MensagemValor(string campo) =>
        $"{campo} deve ser maior ou igual a zero, ter no máximo duas casas decimais e não exceder {EventoRegras.ValorMaximo:0.00}.";

    private static bool ConteudoNumericoValido(T x) =>
        x.Transporte is { EhGratis: not null, Valor: not null } && x.Alimentacao is { Individual: not null, Valor: not null }
        && x.SeguroObrigatorio.HasValue && x.Membros is { Count: > 0 } && x.DataEvento.HasValue
        && (x.Transporte.EhGratis.Value || EventoRegras.ValorValido(x.Transporte.Valor.Value))
        && (x.Alimentacao.Individual.Value || EventoRegras.ValorValido(x.Alimentacao.Valor.Value))
        && EventoRegras.ValorValido(x.SeguroObrigatorio.Value);

    private static bool TotalCabeNoBanco(T x)
    {
        var por = (x.Transporte!.EhGratis!.Value ? 0m : x.Transporte.Valor!.Value)
            + (x.Alimentacao!.Individual!.Value ? 0m : x.Alimentacao.Valor!.Value) + x.SeguroObrigatorio!.Value;
        return por <= EventoRegras.ValorMaximo && por * x.Membros!.Count <= EventoRegras.ValorMaximo;
    }
}

public sealed class RegistrarEventoRequestValidator : EventoConteudoValidator<RegistrarEventoRequest>
{
    public RegistrarEventoRequestValidator(TimeProvider clock)
    {
        // A partir do PRIMEIRO DIA do mês atual (UTC), não de "hoje": datas passadas dentro do mês são válidas.
        RuleFor(x => x.DataEvento!.Value)
            .Must(data => data >= EventoRegras.PrimeiroDiaDoMes(clock.GetUtcNow()))
            .WithMessage(_ => $"dataEvento deve ser a partir de {EventoRegras.PrimeiroDiaDoMes(clock.GetUtcNow()):yyyy-MM-dd} (primeiro dia do mês atual, UTC).")
            .When(x => x.DataEvento.HasValue).OverridePropertyName("DataEvento");

        RuleFor(x => x.EventoReferenciaId).NotEqual(Guid.Empty).WithMessage("eventoReferenciaId não pode ser um GUID vazio.")
            .When(x => x.EventoReferenciaId.HasValue);

        RuleFor(x => x.IdempotencyKey)
            .Must(key => !string.IsNullOrWhiteSpace(key)).WithMessage("Informe o header Idempotency-Key (UUID).")
            .Must(key => EventoRegras.NormalizarIdempotencyKey(key) is not null).WithMessage("Idempotency-Key deve ser um UUID válido.")
            .OverridePropertyName("Idempotency-Key");
    }
}

public sealed class UpdateEventoRequestValidator : EventoConteudoValidator<UpdateEventoRequest>
{
    public UpdateEventoRequestValidator()
    {
        // O limite de data do PUT só se aplica a uma data ALTERADA (a data original de um evento histórico é
        // preservada); isso depende do estado atual e é verificado em EventosService.UpdateAsync.
        RuleFor(x => x.Versao)
            .Must(v => EventoRegras.TentarLerVersao(v, out _)).WithMessage("versao é obrigatória e deve ser a versão devolvida pela API.");
        RuleFor(x => x.Motivo)
            .Must(m => m!.Trim().Length is >= 1 and <= EventoRegras.MotivoMaximo)
            .WithMessage($"motivo deve ter de 1 a {EventoRegras.MotivoMaximo} caracteres.")
            .When(x => x.Motivo is not null);
    }
}

public sealed class DeleteEventoRequestValidator : AbstractValidator<DeleteEventoRequest>
{
    public DeleteEventoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m)).WithMessage("motivo é obrigatório.")
            .Must(m => m!.Trim().Length <= EventoRegras.MotivoMaximo).WithMessage($"motivo deve ter no máximo {EventoRegras.MotivoMaximo} caracteres.");
        RuleFor(x => x.Versao)
            .Must(v => EventoRegras.TentarLerVersao(v, out _)).WithMessage("versao é obrigatória e deve ser a versão devolvida pela API.");
    }
}

public sealed class ListEventosQueryValidator : AbstractValidator<ListEventosQuery>
{
    public ListEventosQueryValidator()
    {
        RuleFor(x => x.DataFinal).NotNull().WithMessage("Informe dataFinal junto com dataInicial (yyyy-MM-dd).").When(x => x.DataInicial.HasValue);
        RuleFor(x => x.DataInicial).NotNull().WithMessage("Informe dataInicial junto com dataFinal (yyyy-MM-dd).").When(x => x.DataFinal.HasValue);
        RuleFor(x => x).Must(x => x.DataInicial!.Value <= x.DataFinal!.Value)
            .WithName("dataInicial").WithMessage("dataInicial não pode ser posterior a dataFinal.")
            .When(x => x.DataInicial.HasValue && x.DataFinal.HasValue);

        // A listagem não é paginada: o período limita o volume de eventos, participantes e lançamentos devolvidos.
        RuleFor(x => x).Must(x => x.DataFinal!.Value.DayNumber - x.DataInicial!.Value.DayNumber < PeriodoMaximoDias)
            .WithName("dataFinal").WithMessage($"O período consultado não pode ultrapassar {PeriodoMaximoDias} dias.")
            .When(x => x.DataInicial.HasValue && x.DataFinal.HasValue && x.DataInicial.Value <= x.DataFinal.Value);
    }

    public const int PeriodoMaximoDias = 366;
}
