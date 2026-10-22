using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using FluentValidation;

namespace Almirante.Api.Validation.Formacao;

internal static class RegrasValidacaoFormacao
{
    public static IRuleBuilderOptions<T, string?> TextoObrigatorio<T>(this IRuleBuilder<T, string?> regra, string campo, int maximo,
        bool quebraDeLinha = false) =>
        regra.Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage($"{campo} é obrigatório.")
            .Must(t => t!.Trim().Length <= maximo).WithMessage($"{campo} deve ter no máximo {maximo} caracteres.")
            .Must(t => TextoVarchar.CabeNaPagina1252(t!.Trim(), quebraDeLinha)).WithMessage(TextoVarchar.ForaDaPagina1252(campo));

    public static IRuleBuilderOptions<T, string?> TextoOpcional<T>(this IRuleBuilder<T, string?> regra, string campo, int maximo,
        bool quebraDeLinha = false) =>
        regra.Must(t => t is null || t.Trim().Length <= maximo).WithMessage($"{campo} deve ter no máximo {maximo} caracteres.")
            .Must(t => t is null || TextoVarchar.CabeNaPagina1252(t.Trim(), quebraDeLinha)).WithMessage(TextoVarchar.ForaDaPagina1252(campo));

    public static IRuleBuilderOptions<T, string?> VersaoValida<T>(this IRuleBuilder<T, string?> regra) =>
        regra.Must(v => EventoRegras.TentarLerVersao(v, out _)).WithMessage("versao é obrigatória e deve ser a versão devolvida pela API.");

    public static IRuleBuilderOptions<T, Guid?> IdObrigatorio<T>(this IRuleBuilder<T, Guid?> regra, string campo) =>
        regra.Must(id => id.HasValue && id.Value != Guid.Empty).WithMessage($"{campo} é obrigatório (GUID não vazio).");

    public static IRuleBuilderOptions<T, Guid?> IdOpcional<T>(this IRuleBuilder<T, Guid?> regra, string campo) =>
        regra.Must(id => id != Guid.Empty).WithMessage($"{campo} não pode ser um GUID vazio.");

    public static bool ClasseValida(ClasseDesbravador? classe) => classe is { } c && CatalogoClasses.Existe(c);

    public static string MensagemClasse(string campo) =>
        $"{campo} deve ser uma das classes do catálogo: {string.Join(", ", CatalogoClasses.Todas.Select(d => d.Classe))}.";

    public static void Paginacao<T>(AbstractValidator<T> validator, Func<T, int> page, Func<T, int> pageSize)
    {
        validator.RuleFor(x => page(x)).GreaterThanOrEqualTo(1).WithName("page").WithMessage("page deve ser maior ou igual a 1.");
        validator.RuleFor(x => pageSize(x)).InclusiveBetween(1, RegrasFormacao.PageSizeMaximo).WithName("pageSize")
            .WithMessage($"pageSize deve estar entre 1 e {RegrasFormacao.PageSizeMaximo}.");
    }
}

public abstract class EspecialidadeConteudoValidator<T> : AbstractValidator<T> where T : EspecialidadeConteudoRequest
{
    protected EspecialidadeConteudoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Nome).TextoObrigatorio("nome", RegrasFormacao.NomeEspecialidadeMaximo);
        RuleFor(x => x.CodigoOficial).TextoOpcional("codigoOficial", RegrasFormacao.CodigoOficialMaximo);
        RuleFor(x => x.Area).TextoObrigatorio("area", RegrasFormacao.AreaMaximo);
        RuleFor(x => x.Descricao).TextoOpcional("descricao", RegrasFormacao.DescricaoMaximo, quebraDeLinha: true);
        RuleFor(x => x.NivelDificuldade).InclusiveBetween(RegrasFormacao.NivelDificuldadeMinimo, RegrasFormacao.NivelDificuldadeMaximo)
            .WithMessage($"nivelDificuldade deve estar entre {RegrasFormacao.NivelDificuldadeMinimo} e {RegrasFormacao.NivelDificuldadeMaximo}.")
            .When(x => x.NivelDificuldade.HasValue);
        RuleFor(x => x.Modalidade).IsInEnum().WithMessage("modalidade deve ser Basica ou Avancada.");
        RuleFor(x => x.PreRequisitos)
            .Must(p => p!.Count <= RegrasFormacao.PreRequisitosMaximo).WithMessage($"preRequisitos aceita no máximo {RegrasFormacao.PreRequisitosMaximo} itens.")
            .Must(p => p!.All(id => id != Guid.Empty)).WithMessage("preRequisitos não pode conter GUID vazio.")
            .Must(p => p!.Distinct().Count() == p!.Count).WithMessage("preRequisitos não pode conter GUIDs repetidos.")
            .When(x => x.PreRequisitos is not null);
        RuleFor(x => x.PreRequisitosTexto).TextoOpcional("preRequisitosTexto", RegrasFormacao.PreRequisitosTextoMaximo, quebraDeLinha: true);
        RuleFor(x => x.ReferenciaDistintivo).TextoOpcional("referenciaDistintivo", RegrasFormacao.ReferenciaMaximo)
            .Must(r => r is null || (Uri.TryCreate(r.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("referenciaDistintivo deve ser uma URL https absoluta.");
        RuleFor(x => x.FonteOficial).TextoObrigatorio("fonteOficial", RegrasFormacao.FonteMaximo);
        RuleFor(x => x.EdicaoRevisao).TextoOpcional("edicaoRevisao", RegrasFormacao.EdicaoMaximo);
    }
}

public sealed class CadastrarEspecialidadeRequestValidator : EspecialidadeConteudoValidator<CadastrarEspecialidadeRequest>;

public sealed class AtualizarEspecialidadeRequestValidator : EspecialidadeConteudoValidator<AtualizarEspecialidadeRequest>
{
    public AtualizarEspecialidadeRequestValidator() => RuleFor(x => x.Versao).VersaoValida();
}

public sealed class AlterarSituacaoEspecialidadeRequestValidator : AbstractValidator<AlterarSituacaoEspecialidadeRequest>
{
    public AlterarSituacaoEspecialidadeRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Motivo).TextoObrigatorio("motivo", RegrasFormacao.JustificativaMaximo).When(x => !x.Ativar);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class ListarEspecialidadesQueryValidator : AbstractValidator<ListarEspecialidadesQuery>
{
    public ListarEspecialidadesQueryValidator()
    {
        RuleFor(x => x.Busca).TextoOpcional("busca", RegrasFormacao.NomeEspecialidadeMaximo);
        RuleFor(x => x.Area).TextoOpcional("area", RegrasFormacao.AreaMaximo);
        RuleFor(x => x.Modalidade).IsInEnum().WithMessage("modalidade deve ser Basica ou Avancada.");
        RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
    }
}

public sealed class ListarRevisoesQueryValidator : AbstractValidator<ListarRevisoesQuery>
{
    public ListarRevisoesQueryValidator() =>
        RuleFor(x => x).Must(x => x.Classe.HasValue != x.EspecialidadeId.HasValue).WithName("classe")
            .WithMessage("Informe classe ou especialidadeId.");
}

public sealed class CriarRevisaoRequestValidator : AbstractValidator<CriarRevisaoRequest>
{
    public CriarRevisaoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x).Must(x => x.Classe.HasValue != x.EspecialidadeId.HasValue).WithName("classe")
            .WithMessage("Informe classe OU especialidadeId (exatamente um).");
        RuleFor(x => x.Classe).Must(RegrasValidacaoFormacao.ClasseValida).WithMessage(RegrasValidacaoFormacao.MensagemClasse("classe"))
            .When(x => x.Classe.HasValue);
        RuleFor(x => x.EspecialidadeId).IdOpcional("especialidadeId");
        RuleFor(x => x.Edicao).TextoObrigatorio("edicao", RegrasFormacao.EdicaoMaximo);
        RuleFor(x => x.FonteOficial).TextoObrigatorio("fonteOficial", RegrasFormacao.FonteMaximo);
        RuleFor(x => x.Observacoes).TextoOpcional("observacoes", RegrasFormacao.ObservacoesRevisaoMaximo, quebraDeLinha: true);
        RuleFor(x => x.Requisitos).Custom(ValidarArvore);
    }

    private static void ValidarArvore(IReadOnlyList<RequisitoRequest>? raiz, ValidationContext<CriarRevisaoRequest> contexto)
    {
        if (raiz is null || raiz.Count == 0)
        {
            contexto.AddFailure("requisitos", "requisitos é obrigatório e não pode ser vazio.");
            return;
        }

        var codigos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        void Falha(string campo, string mensagem) => contexto.AddFailure(campo, mensagem);

        void Texto(string campo, string? valor, int maximo, bool obrigatorio, bool quebraDeLinha = false)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                if (obrigatorio) Falha(campo, $"{campo} é obrigatório.");
                return;
            }

            if (valor.Trim().Length > maximo) Falha(campo, $"{campo} deve ter no máximo {maximo} caracteres.");
            else if (!TextoVarchar.CabeNaPagina1252(valor.Trim(), quebraDeLinha)) Falha(campo, TextoVarchar.ForaDaPagina1252(campo));
        }

        void Validar(IReadOnlyList<RequisitoRequest> itens, string caminho, int nivel, RegraGrupo regraDoPai)
        {
            if (nivel > RegrasFormacao.ProfundidadeMaxima)
            {
                Falha(caminho, $"Os requisitos aceitam no máximo {RegrasFormacao.ProfundidadeMaxima} níveis de subitens.");
                return;
            }

            for (var i = 0; i < itens.Count; i++)
            {
                var item = itens[i];
                var campo = $"{caminho}[{i}]";
                if (item is null)
                {
                    Falha(campo, "Requisito nulo.");
                    continue;
                }

                total++;
                Texto($"{campo}.codigo", item.Codigo, RegrasFormacao.CodigoRequisitoMaximo, obrigatorio: true);
                if (!string.IsNullOrWhiteSpace(item.Codigo) && !codigos.Add(item.Codigo.Trim()))
                {
                    Falha($"{campo}.codigo", $"codigo {item.Codigo.Trim()} repetido na revisão.");
                }

                Texto($"{campo}.descricao", item.Descricao, RegrasFormacao.DescricaoMaximo, obrigatorio: true, quebraDeLinha: true);
                Texto($"{campo}.secao", item.Secao, RegrasFormacao.SecaoMaximo, obrigatorio: false);
                Texto($"{campo}.referenciaOficial", item.ReferenciaOficial, RegrasFormacao.ReferenciaMaximo, obrigatorio: false);
                if (regraDoPai == RegraGrupo.Alternativas && item.Obrigatorio == true)
                {
                    Falha($"{campo}.obrigatorio", "Opções de um grupo de alternativas não são obrigatórias (omita ou use false).");
                }

                var subitens = item.Subitens ?? [];
                if (subitens.Count > 0)
                {
                    if (item.Regra is not { } regra || !Enum.IsDefined(regra))
                    {
                        Falha($"{campo}.regra", "regra é obrigatória quando há subitens (Todos ou Alternativas).");
                        continue;
                    }

                    if (regra == RegraGrupo.Alternativas && (item.QuantidadeMinima is not { } minimo || minimo < 1 || minimo > subitens.Count))
                    {
                        Falha($"{campo}.quantidadeMinima", $"quantidadeMinima é obrigatória com regra Alternativas e deve estar entre 1 e {subitens.Count}.");
                    }

                    if (regra == RegraGrupo.Todos && item.QuantidadeMinima.HasValue)
                    {
                        Falha($"{campo}.quantidadeMinima", "quantidadeMinima só se aplica à regra Alternativas.");
                    }

                    if (regra == RegraGrupo.Todos && !subitens.Any(s => s is not null && (s.Obrigatorio ?? true)))
                    {
                        Falha($"{campo}.subitens", "Um grupo Todos precisa de ao menos um subitem obrigatório.");
                    }

                    if (item.ExigenciaEspecialidade is not null)
                    {
                        Falha($"{campo}.exigenciaEspecialidade", "exigenciaEspecialidade só se aplica a itens sem subitens.");
                    }

                    Validar(subitens, $"{campo}.subitens", nivel + 1, regra);
                    continue;
                }

                if (item.Regra.HasValue || item.QuantidadeMinima.HasValue)
                {
                    Falha($"{campo}.regra", "regra e quantidadeMinima só se aplicam a requisitos com subitens.");
                }

                if (item.ExigenciaEspecialidade is { } exigencia)
                {
                    if (exigencia.Quantidade is not { } quantidade || quantidade < 1 || quantidade > RegrasFormacao.EspecialidadesExigidasMaximo)
                    {
                        Falha($"{campo}.exigenciaEspecialidade.quantidade",
                            $"quantidade é obrigatória e deve estar entre 1 e {RegrasFormacao.EspecialidadesExigidasMaximo}.");
                    }

                    if (exigencia.EspecialidadeId == Guid.Empty)
                    {
                        Falha($"{campo}.exigenciaEspecialidade.especialidadeId", "especialidadeId não pode ser um GUID vazio.");
                    }

                    Texto($"{campo}.exigenciaEspecialidade.area", exigencia.Area, RegrasFormacao.AreaMaximo, obrigatorio: false);
                }
            }
        }

        Validar(raiz, "requisitos", 1, RegraGrupo.Todos);
        if (total > RegrasFormacao.RequisitosPorRevisaoMaximo)
        {
            Falha("requisitos", $"Uma revisão aceita no máximo {RegrasFormacao.RequisitosPorRevisaoMaximo} requisitos e subitens.");
        }

        if (!raiz.Any(r => r is not null && (r.Obrigatorio ?? true)))
        {
            Falha("requisitos", "A revisão precisa de ao menos um requisito obrigatório na raiz.");
        }
    }
}

public sealed class AlterarSituacaoRevisaoRequestValidator : AbstractValidator<AlterarSituacaoRevisaoRequest>
{
    public AlterarSituacaoRevisaoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Motivo).TextoObrigatorio("motivo", RegrasFormacao.JustificativaMaximo).When(x => !x.Publicar);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class AssociarClasseRequestValidator : AbstractValidator<AssociarClasseRequest>
{
    public AssociarClasseRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.MembroId).IdObrigatorio("membroId");
        RuleFor(x => x.Classe).Must(RegrasValidacaoFormacao.ClasseValida).WithMessage(RegrasValidacaoFormacao.MensagemClasse("classe"));
        RuleFor(x => x.Modalidade).IsInEnum().WithMessage("modalidade deve ser Padrao ou Agrupada.");
        RuleFor(x => x.DataInicio).NotNull().WithMessage("dataInicio é obrigatória (yyyy-MM-dd).");
        RuleFor(x => x.InstrutorId).IdOpcional("instrutorId");
        RuleFor(x => x.Observacoes).TextoOpcional("observacoes", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
    }
}

public sealed class AssociarEspecialidadeRequestValidator : AbstractValidator<AssociarEspecialidadeRequest>
{
    public AssociarEspecialidadeRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.MembroId).IdObrigatorio("membroId");
        RuleFor(x => x.EspecialidadeId).IdObrigatorio("especialidadeId");
        RuleFor(x => x.DataInicio).NotNull().WithMessage("dataInicio é obrigatória (yyyy-MM-dd).");
        RuleFor(x => x.InstrutorId).IdOpcional("instrutorId");
        RuleFor(x => x.Observacoes).TextoOpcional("observacoes", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
    }
}

public sealed class RegistrarHistoricoRequestValidator : AbstractValidator<RegistrarHistoricoRequest>
{
    public RegistrarHistoricoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.MembroId).IdObrigatorio("membroId");
        RuleFor(x => x.Classe).Must(RegrasValidacaoFormacao.ClasseValida).WithMessage(RegrasValidacaoFormacao.MensagemClasse("classe"))
            .When(x => x.Tipo == TipoVinculo.Classe);
        RuleFor(x => x.Modalidade).IsInEnum().WithMessage("modalidade deve ser Padrao ou Agrupada.").When(x => x.Tipo == TipoVinculo.Classe);
        RuleFor(x => x.EspecialidadeId).Null().WithMessage("especialidadeId não se aplica a classes.").When(x => x.Tipo == TipoVinculo.Classe);
        RuleFor(x => x.EspecialidadeId).IdObrigatorio("especialidadeId").When(x => x.Tipo == TipoVinculo.Especialidade);
        RuleFor(x => x.Classe).Null().WithMessage("classe não se aplica a especialidades.").When(x => x.Tipo == TipoVinculo.Especialidade);
        RuleFor(x => x.Modalidade).Null().WithMessage("modalidade não se aplica a especialidades.").When(x => x.Tipo == TipoVinculo.Especialidade);
        RuleFor(x => x.OrigemHistorica).TextoObrigatorio("origemHistorica", RegrasFormacao.OrigemHistoricaMaximo);
        RuleFor(x => x.Observacoes).TextoOpcional("observacoes", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
        RuleFor(x => x.InvestiduraOuEntregaRealizada).NotNull().WithMessage("investiduraOuEntregaRealizada é obrigatório (true ou false).");
        RuleFor(x => x.DataInvestiduraOuEntrega).Null().WithMessage("dataInvestiduraOuEntrega só se aplica com investiduraOuEntregaRealizada true.")
            .When(x => x.InvestiduraOuEntregaRealizada == false);
        RuleFor(x => x.DataConclusao).GreaterThanOrEqualTo(x => x.DataInicio!.Value)
            .WithMessage("dataConclusao não pode ser anterior a dataInicio.").When(x => x.DataInicio.HasValue && x.DataConclusao.HasValue);
        RuleFor(x => x.DataInvestiduraOuEntrega)
            .Must((x, data) => (x.DataConclusao is null || data >= x.DataConclusao) && (x.DataInicio is null || data >= x.DataInicio))
            .WithMessage("dataInvestiduraOuEntrega não pode ser anterior à conclusão nem ao início.")
            .When(x => x.DataInvestiduraOuEntrega.HasValue);
    }
}

public sealed class AtualizarVinculoRequestValidator : AbstractValidator<AtualizarVinculoRequest>
{
    public AtualizarVinculoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.InstrutorId).IdOpcional("instrutorId");
        RuleFor(x => x.Observacoes).TextoOpcional("observacoes", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class AtualizarRequisitoVinculoRequestValidator : AbstractValidator<AtualizarRequisitoVinculoRequest>
{
    public AtualizarRequisitoVinculoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Situacao).NotNull().WithMessage("situacao é obrigatória (Pendente, EmAndamento ou Validado).")
            .IsInEnum().WithMessage("situacao deve ser Pendente, EmAndamento ou Validado.");
        RuleFor(x => x.Observacao).TextoOpcional("observacao", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
        RuleFor(x => x.ReferenciaEvidencia).TextoOpcional("referenciaEvidencia", RegrasFormacao.ReferenciaMaximo);
        RuleFor(x => x.Justificativa).TextoOpcional("justificativa", RegrasFormacao.JustificativaMaximo);
    }
}

public sealed class VincularEvidenciaRequestValidator : AbstractValidator<VincularEvidenciaRequest>
{
    public VincularEvidenciaRequestValidator() => RuleFor(x => x.MembroEspecialidadeId).IdObrigatorio("membroEspecialidadeId");
}

public sealed class RemoverEvidenciaRequestValidator : AbstractValidator<RemoverEvidenciaRequest>
{
    public RemoverEvidenciaRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Justificativa).TextoObrigatorio("justificativa", RegrasFormacao.JustificativaMaximo);
    }
}

public sealed class RegistrarValidacaoExternaRequestValidator : AbstractValidator<RegistrarValidacaoExternaRequest>
{
    public RegistrarValidacaoExternaRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Confirmada).NotNull().WithMessage("confirmada é obrigatório (true ou false).");
        RuleFor(x => x.Data).NotNull().WithMessage("data é obrigatória ao confirmar (yyyy-MM-dd).").When(x => x.Confirmada == true);
        RuleFor(x => x.Referencia).TextoOpcional("referencia", RegrasFormacao.ReferenciaMaximo);
        RuleFor(x => x.Observacao).TextoOpcional("observacao", RegrasFormacao.ObservacoesMaximo, quebraDeLinha: true);
        RuleFor(x => x.Justificativa).TextoOpcional("justificativa", RegrasFormacao.JustificativaMaximo);
    }
}

public sealed class ConcluirVinculoRequestValidator : AbstractValidator<ConcluirVinculoRequest>
{
    public ConcluirVinculoRequestValidator()
    {
        RuleFor(x => x.DataConclusao).NotNull().WithMessage("dataConclusao é obrigatória (yyyy-MM-dd).");
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class RegistrarReconhecimentoRequestValidator : AbstractValidator<RegistrarReconhecimentoRequest>
{
    public RegistrarReconhecimentoRequestValidator()
    {
        RuleFor(x => x.Data).NotNull().WithMessage("data é obrigatória (yyyy-MM-dd).");
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class OperacaoVinculoRequestValidator : AbstractValidator<OperacaoVinculoRequest>
{
    public OperacaoVinculoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Justificativa).TextoObrigatorio("justificativa", RegrasFormacao.JustificativaMaximo);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class MigrarRevisaoRequestValidator : AbstractValidator<MigrarRevisaoRequest>
{
    public MigrarRevisaoRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.RevisaoId).IdObrigatorio("revisaoId");
        RuleFor(x => x.Justificativa).TextoObrigatorio("justificativa", RegrasFormacao.JustificativaMaximo);
        RuleFor(x => x.Versao).VersaoValida();
    }
}

public sealed class ListarVinculosQueryValidator : AbstractValidator<ListarVinculosQuery>
{
    public ListarVinculosQueryValidator()
    {
        RuleFor(x => x.Classe).Must(RegrasValidacaoFormacao.ClasseValida).WithMessage(RegrasValidacaoFormacao.MensagemClasse("classe"))
            .When(x => x.Classe.HasValue);
        RuleFor(x => x.Grupo).IsInEnum().WithMessage("grupo deve ser Regular, Avancada ou Lideranca.");
        RuleFor(x => x.Situacao).IsInEnum().WithMessage("situacao deve ser EmAndamento, Concluida ou Cancelada.");
        RuleFor(x => x.Periodo).IsInEnum().WithMessage("periodo deve ser Inicio, Conclusao ou InvestiduraOuEntrega.");
        RuleFor(x => x.OrdenarPor).IsInEnum().WithMessage("ordenarPor deve ser Membro, Inicio, Conclusao ou InvestiduraOuEntrega.");
        RuleFor(x => x.Area).TextoOpcional("area", RegrasFormacao.AreaMaximo);
        RuleFor(x => x).Must(x => x.Classe is null && x.Grupo is null).WithName("classe")
            .WithMessage("classe e grupo filtram só vínculos de classe.").When(x => x.Tipo == TipoVinculo.Especialidade);
        RuleFor(x => x).Must(x => x.EspecialidadeId is null && x.Area is null).WithName("especialidadeId")
            .WithMessage("especialidadeId e area filtram só vínculos de especialidade.").When(x => x.Tipo == TipoVinculo.Classe);
        RuleFor(x => x).Must(x => x.De.HasValue || x.Ate.HasValue).WithName("periodo")
            .WithMessage("Com periodo, informe de e/ou ate (yyyy-MM-dd).").When(x => x.Periodo.HasValue);
        RuleFor(x => x).Must(x => x.Periodo.HasValue).WithName("periodo")
            .WithMessage("Informe periodo (Inicio, Conclusao ou InvestiduraOuEntrega) para filtrar por de/ate.").When(x => x.De.HasValue || x.Ate.HasValue);
        RuleFor(x => x).Must(x => x.De <= x.Ate).WithName("de").WithMessage("de não pode ser posterior a ate.")
            .When(x => x.De.HasValue && x.Ate.HasValue);
        RegrasValidacaoFormacao.Paginacao(this, x => x.Page, x => x.PageSize);
    }
}

public sealed class RelatorioParticipantesQueryValidator : AbstractValidator<RelatorioParticipantesQuery>
{
    public RelatorioParticipantesQueryValidator()
    {
        RuleFor(x => x.Tipo).IsInEnum().WithMessage("tipo deve ser Classe ou Especialidade.");
        RuleFor(x => x.Classe).Must(RegrasValidacaoFormacao.ClasseValida).WithMessage(RegrasValidacaoFormacao.MensagemClasse("classe"))
            .When(x => x.Classe.HasValue);
        RuleFor(x => x.Grupo).IsInEnum().WithMessage("grupo deve ser Regular, Avancada ou Lideranca.");
        RuleFor(x => x.Area).TextoOpcional("area", RegrasFormacao.AreaMaximo);
        RuleFor(x => x).Must(x => x.Classe is null && x.Grupo is null).WithName("classe")
            .WithMessage("classe e grupo se aplicam só a tipo=Classe.").When(x => x.Tipo == TipoVinculo.Especialidade);
        RuleFor(x => x).Must(x => x.EspecialidadeId is null && x.Area is null).WithName("especialidadeId")
            .WithMessage("especialidadeId e area se aplicam só a tipo=Especialidade.").When(x => x.Tipo == TipoVinculo.Classe);
    }
}
