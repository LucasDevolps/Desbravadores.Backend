using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Almirante.Api.Validation.Formacao;
using FluentValidation;

namespace Almirante.Api.Validation.Almoxarifado;

internal static class RegrasValidacaoAlmoxarifado
{
    public static IRuleBuilderOptions<T, TEnum?> EnumObrigatorio<T, TEnum>(this IRuleBuilder<T, TEnum?> regra, string campo) where TEnum : struct, Enum =>
        regra.NotNull().WithMessage($"{campo} é obrigatório.")
            .IsInEnum().WithMessage($"{campo} deve ser um destes valores: {string.Join(", ", Enum.GetNames<TEnum>())}.");

    public static IRuleBuilderOptions<T, TEnum?> EnumOpcional<T, TEnum>(this IRuleBuilder<T, TEnum?> regra, string campo) where TEnum : struct, Enum =>
        regra.IsInEnum().WithMessage($"{campo} deve ser um destes valores: {string.Join(", ", Enum.GetNames<TEnum>())}.");

    /// <summary>Não negativo, no máximo duas casas decimais e dentro do limite de decimal(18,2).</summary>
    public static IRuleBuilderOptions<T, decimal?> ValorOpcional<T>(this IRuleBuilder<T, decimal?> regra, string campo) =>
        regra.Must(v => v is null || EventoRegras.ValorValido(v.Value))
            .WithMessage($"{campo} deve ser maior ou igual a zero, com até duas casas decimais.");

    public static IRuleBuilderOptions<T, DateOnly?> DataObrigatoria<T>(this IRuleBuilder<T, DateOnly?> regra, string campo) =>
        regra.NotNull().WithMessage($"{campo} é obrigatória (yyyy-MM-dd).");

    public static IRuleBuilderOptions<T, string?> Justificativa<T>(this IRuleBuilder<T, string?> regra, string campo = "justificativa") =>
        regra.TextoObrigatorio(campo, RegrasAlmoxarifado.JustificativaMaximo);

    public static IRuleBuilderOptions<T, string?> Observacoes<T>(this IRuleBuilder<T, string?> regra, string campo = "observacoes") =>
        regra.TextoOpcional(campo, RegrasAlmoxarifado.ObservacoesMaximo, quebraDeLinha: true);

    public static bool UrlHttp(string? url) =>
        url is null || (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}

public abstract class BemDadosValidator<T> : AbstractValidator<T> where T : BemDadosRequest
{
    protected BemDadosValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Nome).TextoObrigatorio("nome", RegrasAlmoxarifado.NomeMaximo);
        RuleFor(x => x.Descricao).TextoObrigatorio("descricao", RegrasAlmoxarifado.DescricaoMaximo, quebraDeLinha: true);
        RuleFor(x => x.Categoria).TextoOpcional("categoria", RegrasAlmoxarifado.CategoriaMaximo);
        RuleFor(x => x.EstadoConservacao).EnumObrigatorio("estadoConservacao");
        RuleFor(x => x.PossuiValorComercial).NotNull().WithMessage("possuiValorComercial é obrigatório.");
        RuleFor(x => x.ValorEstimado).NotNull().WithMessage("valorEstimado é obrigatório quando o bem possui valor comercial.")
            .ValorOpcional("valorEstimado").When(x => x.PossuiValorComercial == true);
        RuleFor(x => x.ValorEstimado).Null().WithMessage("valorEstimado deve ser nulo quando o bem não possui valor comercial.")
            .When(x => x.PossuiValorComercial == false);
        RuleFor(x => x.Localizacao).TextoObrigatorio("localizacao", RegrasAlmoxarifado.LocalizacaoMaximo);
        RuleFor(x => x.ResponsavelGuardaId).IdOpcional("responsavelGuardaId");
        RuleFor(x => x.Marca).TextoOpcional("marca", RegrasAlmoxarifado.MarcaModeloSerieMaximo);
        RuleFor(x => x.Modelo).TextoOpcional("modelo", RegrasAlmoxarifado.MarcaModeloSerieMaximo);
        RuleFor(x => x.NumeroSerie).TextoOpcional("numeroSerie", RegrasAlmoxarifado.MarcaModeloSerieMaximo);
        RuleFor(x => x.ValorAquisicao).ValorOpcional("valorAquisicao");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public abstract class BemConteudoValidator<T> : BemDadosValidator<T> where T : BemConteudoRequest
{
    protected BemConteudoValidator()
    {
        RuleFor(x => x.DataAquisicao).DataObrigatoria("dataAquisicao").When(x => x.DataAquisicaoDesconhecida != true);
        RuleFor(x => x.JustificativaDataDesconhecida).Null()
            .WithMessage("justificativaDataDesconhecida só é aceita com dataAquisicaoDesconhecida = true.").When(x => x.DataAquisicaoDesconhecida != true);
        RuleFor(x => x.DataAquisicao).Null()
            .WithMessage("dataAquisicao deve ser nula quando dataAquisicaoDesconhecida = true (a data não é inventada).")
            .When(x => x.DataAquisicaoDesconhecida == true);
        RuleFor(x => x.JustificativaDataDesconhecida).TextoObrigatorio("justificativaDataDesconhecida", RegrasAlmoxarifado.OrigemDetalheMaximo)
            .When(x => x.DataAquisicaoDesconhecida == true);
        RuleFor(x => x.Origem).EnumObrigatorio("origem");
        RuleFor(x => x.OrigemDetalhe).TextoObrigatorio("origemDetalhe", RegrasAlmoxarifado.OrigemDetalheMaximo).When(x => x.Origem == OrigemBem.Outro);
        RuleFor(x => x.OrigemDetalhe).TextoOpcional("origemDetalhe", RegrasAlmoxarifado.OrigemDetalheMaximo).When(x => x.Origem != OrigemBem.Outro);
    }
}

public sealed class CadastrarBemRequestValidator : BemConteudoValidator<CadastrarBemRequest>;

public sealed class AtualizarBemRequestValidator : BemConteudoValidator<AtualizarBemRequest>
{
    public AtualizarBemRequestValidator() => RuleFor(x => x.Versao).VersaoValida();
}

public sealed class NovoBemAtendimentoRequestValidator : BemDadosValidator<NovoBemAtendimentoRequest>;

public sealed class ExcluirBemRequestValidator : AbstractValidator<ExcluirBemRequest>
{
    public ExcluirBemRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Justificativa).Justificativa();
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class EmprestarBemRequestValidator : AbstractValidator<EmprestarBemRequest>
{
    public EmprestarBemRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.MembroId).IdObrigatorio("membroId");
        RuleFor(x => x.DataRetirada).DataObrigatoria("dataRetirada");
        RuleFor(x => x.PrevisaoDevolucao).DataObrigatoria("previsaoDevolucao");
        RuleFor(x => x.ConservacaoSaida).EnumObrigatorio("conservacaoSaida");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class DevolverEmprestimoRequestValidator : AbstractValidator<DevolverEmprestimoRequest>
{
    public DevolverEmprestimoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataDevolucao).DataObrigatoria("dataDevolucao");
        RuleFor(x => x.ConservacaoRetorno).EnumObrigatorio("conservacaoRetorno");
        RuleFor(x => x.Observacoes).Observacoes();
        RuleFor(x => x.MotivoManutencao).TextoObrigatorio("motivoManutencao", RegrasAlmoxarifado.JustificativaMaximo)
            .When(x => x.EncaminharParaManutencao == true);
        RuleFor(x => x.MotivoManutencao).Null().WithMessage("motivoManutencao só é aceito com encaminharParaManutencao = true.")
            .When(x => x.EncaminharParaManutencao != true);
    }
}

public sealed class RegistrarPerdaFurtoRequestValidator : AbstractValidator<RegistrarPerdaFurtoRequest>
{
    public RegistrarPerdaFurtoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Data).DataObrigatoria("data");
        RuleFor(x => x.Motivo).Must(m => m is EncerramentoEmprestimo.Perda or EncerramentoEmprestimo.Furto)
            .WithMessage("motivo deve ser Perda ou Furto.");
        RuleFor(x => x.Justificativa).Justificativa();
    }
}

public sealed class EntradaManutencaoRequestValidator : AbstractValidator<EntradaManutencaoRequest>
{
    public EntradaManutencaoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataEntrada).DataObrigatoria("dataEntrada");
        RuleFor(x => x.Motivo).TextoObrigatorio("motivo", RegrasAlmoxarifado.JustificativaMaximo);
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class SaidaManutencaoRequestValidator : AbstractValidator<SaidaManutencaoRequest>
{
    public SaidaManutencaoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataSaida).DataObrigatoria("dataSaida");
        RuleFor(x => x.ConservacaoSaida).EnumObrigatorio("conservacaoSaida");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class BaixarBemRequestValidator : AbstractValidator<BaixarBemRequest>
{
    public BaixarBemRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataBaixa).DataObrigatoria("dataBaixa");
        RuleFor(x => x.Motivo).EnumObrigatorio("motivo");
        RuleFor(x => x.Justificativa).Justificativa();
    }
}

public sealed class FecharInventarioRequestValidator : AbstractValidator<FecharInventarioRequest>
{
    public FecharInventarioRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataInventario).DataObrigatoria("dataInventario");
        RuleFor(x => x.ResponsavelId).IdObrigatorio("responsavelId");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class RegistrarVotacaoInventarioRequestValidator : AbstractValidator<RegistrarVotacaoInventarioRequest>
{
    public RegistrarVotacaoInventarioRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.DataVotacao).DataObrigatoria("dataVotacao");
        RuleFor(x => x.ReferenciaAta).TextoObrigatorio("referenciaAta", RegrasAlmoxarifado.ReferenciaAtaMaximo);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public abstract class DesejoConteudoValidator<T> : AbstractValidator<T> where T : DesejoConteudoRequest
{
    protected DesejoConteudoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Nome).TextoObrigatorio("nome", RegrasAlmoxarifado.NomeMaximo);
        RuleFor(x => x.Descricao).TextoObrigatorio("descricao", RegrasAlmoxarifado.DescricaoMaximo, quebraDeLinha: true);
        RuleFor(x => x.Justificativa).TextoObrigatorio("justificativa", RegrasAlmoxarifado.JustificativaDesejoMaximo, quebraDeLinha: true);
        RuleFor(x => x.TipoItem).EnumObrigatorio("tipoItem");
        RuleFor(x => x.QuantidadeDesejada).NotNull().WithMessage("quantidadeDesejada é obrigatória.")
            .InclusiveBetween(1, RegrasAlmoxarifado.QuantidadeMaxima)
            .WithMessage($"quantidadeDesejada deve ser um inteiro entre 1 e {RegrasAlmoxarifado.QuantidadeMaxima}.");
        RuleFor(x => x.Urgencia).EnumObrigatorio("urgencia");
        RuleFor(x => x.Categoria).TextoOpcional("categoria", RegrasAlmoxarifado.CategoriaMaximo);
        RuleFor(x => x.CustoUnitarioEstimado).ValorOpcional("custoUnitarioEstimado");
        RuleFor(x => x.LinkReferencia).TextoOpcional("linkReferencia", RegrasAlmoxarifado.LinkMaximo)
            .Must(RegrasValidacaoAlmoxarifado.UrlHttp).WithMessage("linkReferencia deve ser uma URL http ou https absoluta.");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class CadastrarDesejoRequestValidator : DesejoConteudoValidator<CadastrarDesejoRequest>;

public sealed class AtualizarDesejoRequestValidator : DesejoConteudoValidator<AtualizarDesejoRequest>
{
    public AtualizarDesejoRequestValidator() => RuleFor(x => x.Versao).VersaoValida();
}

public sealed class EncerrarDesejoRequestValidator : AbstractValidator<EncerrarDesejoRequest>
{
    public EncerrarDesejoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Justificativa).Justificativa();
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class RegistrarAtendimentoRequestValidator : AbstractValidator<RegistrarAtendimentoRequest>
{
    public RegistrarAtendimentoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.IdempotencyKey).Must(k => EventoRegras.NormalizarIdempotencyKey(k) is not null)
            .WithName("Idempotency-Key").WithMessage("O header Idempotency-Key é obrigatório e deve ser um UUID.");
        RuleFor(x => x.Quantidade).NotNull().WithMessage("quantidade é obrigatória.")
            .InclusiveBetween(1, RegrasAlmoxarifado.QuantidadeMaxima)
            .WithMessage($"quantidade deve ser um inteiro entre 1 e {RegrasAlmoxarifado.QuantidadeMaxima}.");
        RuleFor(x => x.Data).DataObrigatoria("data");
        RuleFor(x => x.ResponsavelId).IdObrigatorio("responsavelId");
        RuleFor(x => x.Origem).EnumObrigatorio("origem");
        RuleFor(x => x.Observacoes).Observacoes();
        RuleFor(x => x.BensNovos!.Count + (x.BensExistentes == null ? 0 : x.BensExistentes.Count))
            .LessThanOrEqualTo(RegrasAlmoxarifado.BensPorAtendimentoMaximo).WithName("bens")
            .WithMessage($"Um atendimento aceita no máximo {RegrasAlmoxarifado.BensPorAtendimentoMaximo} bens.")
            .When(x => x.BensNovos is not null);
        RuleForEach(x => x.BensNovos).NotNull().WithMessage("bensNovos não pode conter itens nulos.")
            .SetValidator(new NovoBemAtendimentoRequestValidator());
        RuleFor(x => x.BensExistentes)
            .Must(b => b!.Count <= RegrasAlmoxarifado.BensPorAtendimentoMaximo)
            .WithMessage($"bensExistentes aceita no máximo {RegrasAlmoxarifado.BensPorAtendimentoMaximo} itens.")
            .Must(b => b!.All(id => id != Guid.Empty)).WithMessage("bensExistentes não pode conter GUID vazio.")
            .Must(b => b!.Distinct().Count() == b!.Count).WithMessage("bensExistentes não pode conter GUIDs repetidos.")
            .When(x => x.BensExistentes is not null);
    }
}

public sealed class EstornarAtendimentoRequestValidator : AbstractValidator<EstornarAtendimentoRequest>
{
    public EstornarAtendimentoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Justificativa).Justificativa();
    }
}

public sealed class DesignarResponsavelRequestValidator : AbstractValidator<DesignarResponsavelRequest>
{
    public DesignarResponsavelRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.MembroId).IdObrigatorio("membroId");
        RuleFor(x => x.DataInicio).DataObrigatoria("dataInicio");
        RuleFor(x => x.Observacoes).Observacoes();
    }
}

public sealed class RevogarResponsavelRequestValidator : AbstractValidator<RevogarResponsavelRequest>
{
    public RevogarResponsavelRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Motivo).Justificativa("motivo");
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class ListarBensQueryValidator : AbstractValidator<ListarBensQuery>
{
    public ListarBensQueryValidator()
    {
        RuleFor(x => x.Busca).TextoOpcional("busca", RegrasAlmoxarifado.BuscaMaximo);
        RuleFor(x => x.Categoria).TextoOpcional("categoria", RegrasAlmoxarifado.CategoriaMaximo);
        RuleFor(x => x.Localizacao).TextoOpcional("localizacao", RegrasAlmoxarifado.LocalizacaoMaximo);
        RuleFor(x => x.Conservacao).EnumOpcional("conservacao");
        RuleFor(x => x.Situacao).EnumOpcional("situacao");
        RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
    }
}

public sealed class RelatorioPatrimonioQueryValidator : AbstractValidator<RelatorioPatrimonioQuery>
{
    public RelatorioPatrimonioQueryValidator() => RuleFor(x => x.Filtros).SetValidator(new ListarBensQueryValidator());
}

public sealed class ExportarPatrimonioQueryValidator : AbstractValidator<ExportarPatrimonioQuery>
{
    public ExportarPatrimonioQueryValidator() => RuleFor(x => x.Filtros).SetValidator(new ListarBensQueryValidator());
}

public sealed class ListarEmprestimosQueryValidator : AbstractValidator<ListarEmprestimosQuery>
{
    public ListarEmprestimosQueryValidator()
    {
        RuleFor(x => x.BemId).IdOpcional("bemId");
        RuleFor(x => x.MembroId).IdOpcional("membroId");
        RuleFor(x => x.Abertos).NotEqual(false).WithMessage("atrasados = true já implica empréstimos abertos; não combine com abertos = false.")
            .When(x => x.Atrasados == true);
        RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
    }
}

public sealed class ListarInventariosQueryValidator : AbstractValidator<ListarInventariosQuery>
{
    public ListarInventariosQueryValidator() => RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
}

public sealed class ListarDesejosQueryValidator : AbstractValidator<ListarDesejosQuery>
{
    public ListarDesejosQueryValidator()
    {
        RuleFor(x => x.Busca).TextoOpcional("busca", RegrasAlmoxarifado.BuscaMaximo);
        RuleFor(x => x.Categoria).TextoOpcional("categoria", RegrasAlmoxarifado.CategoriaMaximo);
        RuleFor(x => x.Urgencia).EnumOpcional("urgencia");
        RuleFor(x => x.Situacao).EnumOpcional("situacao");
        RuleFor(x => x.TipoItem).EnumOpcional("tipoItem");
        RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
    }
}

public sealed class HistoricoQueryValidator : AbstractValidator<HistoricoQuery>
{
    public HistoricoQueryValidator()
    {
        RuleFor(x => x.Tipo).EnumOpcional("tipo");
        RuleFor(x => x.Ate).GreaterThanOrEqualTo(x => x.De).WithName("ate").WithMessage("ate não pode ser anterior a de.")
            .When(x => x.De.HasValue && x.Ate.HasValue);
    }
}
