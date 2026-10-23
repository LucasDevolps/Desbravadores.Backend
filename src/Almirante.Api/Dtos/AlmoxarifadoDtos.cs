using System.Text.Json;
using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

// Patrimônio e lista de desejos do almoxarifado (issue #126). Enums trafegam pelo nome (código) e aceitam o valor numérico na entrada;
// cada resposta traz também a descrição (campo ...Descricao). A lista completa de valores, códigos e descrições está em
// GET /api/Almoxarifado/Enums.

/// <summary>Valor numérico estável, código (nome do enum, aceito nos corpos e filtros) e descrição de exibição.</summary>
public sealed record EnumItemDto(int Valor, string Codigo, string Descricao);

public sealed record EnumsAlmoxarifadoDto(
    IReadOnlyList<EnumItemDto> EstadoConservacao,
    IReadOnlyList<EnumItemDto> SituacaoBem,
    IReadOnlyList<EnumItemDto> OrigemBem,
    IReadOnlyList<EnumItemDto> MotivoBaixa,
    IReadOnlyList<EnumItemDto> EncerramentoEmprestimo,
    IReadOnlyList<EnumItemDto> NivelUrgenciaAlmoxarifado,
    IReadOnlyList<EnumItemDto> SituacaoDesejo,
    IReadOnlyList<EnumItemDto> TipoItemDesejo,
    IReadOnlyList<EnumItemDto> OrigemAtendimento,
    IReadOnlyList<EnumItemDto> TipoMovimentacaoAlmoxarifado);

public sealed record BemReferenciaDto(Guid Id, string Tombo, string Nome);

public sealed record RegistroUsuarioDto(PessoaResumoDto Por, DateTime EmUtc);

public sealed record BaixaDto(DateOnly Data, MotivoBaixa Motivo, string MotivoDescricao, string Justificativa, PessoaResumoDto Por, DateTime EmUtc);

public sealed record ExclusaoDto(string Motivo, PessoaResumoDto Por, DateTime EmUtc);

/// <summary>Atendimento da lista de desejos que trouxe o bem (vínculo ativo).</summary>
public sealed record OrigemDesejoDto(Guid DesejoId, string DesejoNome, Guid AtendimentoId, bool CriadoPeloAtendimento);

public sealed record BemDto
{
    public Guid Id { get; init; }
    public required string Tombo { get; init; }
    public required string Nome { get; init; }
    public required string Descricao { get; init; }
    public string? Categoria { get; init; }
    public EstadoConservacao EstadoConservacao { get; init; }
    public required string EstadoConservacaoDescricao { get; init; }
    public bool PossuiValorComercial { get; init; }

    /// <summary>Estimativa atual; null quando o bem não tem valor comercial (troféu, livro de atas, álbum).</summary>
    public decimal? ValorEstimado { get; init; }
    public DateOnly? DataAquisicao { get; init; }
    public bool DataAquisicaoDesconhecida { get; init; }
    public string? JustificativaDataDesconhecida { get; init; }
    public OrigemBem Origem { get; init; }
    public required string OrigemDescricao { get; init; }
    public string? OrigemDetalhe { get; init; }
    public required string Localizacao { get; init; }
    public PessoaResumoDto? ResponsavelGuarda { get; init; }
    public string? Marca { get; init; }
    public string? Modelo { get; init; }
    public string? NumeroSerie { get; init; }

    /// <summary>Valor pago na aquisição (opcional), distinto da estimativa atual.</summary>
    public decimal? ValorAquisicao { get; init; }
    public string? Observacoes { get; init; }
    public SituacaoBem Situacao { get; init; }
    public required string SituacaoDescricao { get; init; }
    public EmprestimoDto? EmprestimoAberto { get; init; }
    public ManutencaoDto? ManutencaoAberta { get; init; }
    public BaixaDto? Baixa { get; init; }
    public OrigemDesejoDto? OrigemDesejo { get; init; }
    public bool Excluido { get; init; }
    public ExclusaoDto? Exclusao { get; init; }
    public required RegistroUsuarioDto Criacao { get; init; }
    public RegistroUsuarioDto? UltimaAlteracao { get; init; }

    /// <summary>rowversion em base64: exigida em PUT e DELETE.</summary>
    public required string Versao { get; init; }
}

public sealed record EmprestimoDto
{
    public Guid Id { get; init; }
    public required BemReferenciaDto Bem { get; init; }
    public required PessoaResumoDto Membro { get; init; }
    public DateOnly DataRetirada { get; init; }
    public DateOnly PrevisaoDevolucao { get; init; }
    public EstadoConservacao ConservacaoSaida { get; init; }
    public required string ConservacaoSaidaDescricao { get; init; }
    public string? ObservacoesSaida { get; init; }
    public bool Aberto { get; init; }

    /// <summary>Calculado na consulta: aberto e com previsão anterior a hoje (fuso do clube).</summary>
    public bool Atrasado { get; init; }
    public int DiasAtraso { get; init; }
    public EncerramentoEmprestimo? Encerramento { get; init; }
    public string? EncerramentoDescricao { get; init; }
    public DateOnly? DataEncerramento { get; init; }
    public EstadoConservacao? ConservacaoRetorno { get; init; }
    public string? ConservacaoRetornoDescricao { get; init; }
    public string? ObservacoesRetorno { get; init; }
    public required RegistroUsuarioDto Registro { get; init; }
    public RegistroUsuarioDto? RegistroEncerramento { get; init; }
}

public sealed record ManutencaoDto
{
    public Guid Id { get; init; }
    public required BemReferenciaDto Bem { get; init; }
    public DateOnly DataEntrada { get; init; }
    public required string Motivo { get; init; }
    public string? ObservacoesEntrada { get; init; }
    public bool Aberta { get; init; }
    public DateOnly? DataSaida { get; init; }
    public EstadoConservacao? ConservacaoSaida { get; init; }
    public string? ConservacaoSaidaDescricao { get; init; }
    public string? ObservacoesSaida { get; init; }
    public required RegistroUsuarioDto Registro { get; init; }
    public RegistroUsuarioDto? RegistroSaida { get; init; }
}

/// <summary>Uma linha de almoxarifado_historico: estado anterior (null na inclusão) e novo de uma linha, com operação e responsável.</summary>
public sealed record HistoricoAlmoxarifadoDto(Guid Id, string Tabela, Guid RegistroId, TipoMovimentacaoAlmoxarifado? Tipo, string? TipoDescricao,
    string? Justificativa, PessoaResumoDto Responsavel, DateTime OcorridoEmUtc, JsonElement? EstadoAnterior, JsonElement EstadoNovo);

public sealed record TotalPorSituacaoDto(SituacaoBem Situacao, string Descricao, int Quantidade);

/// <summary>
/// Totais do patrimônio. valorEstimadoPatrimonial soma só as estimativas dos bens com valor comercial que não foram baixados: é uma
/// estimativa patrimonial, não um saldo financeiro, e não altera o dashboard nem gera lançamentos.
/// </summary>
public sealed record RelatorioPatrimonioDto(int TotalBens, IReadOnlyList<TotalPorSituacaoDto> PorSituacao, int BensComValorComercial,
    int BensSemValorComercial, decimal ValorEstimadoPatrimonial, string ValorEstimadoPatrimonialRotulo);

public sealed record InventarioResumoDto
{
    public Guid Id { get; init; }
    public DateOnly DataInventario { get; init; }
    public required PessoaResumoDto Responsavel { get; init; }
    public string? Observacoes { get; init; }
    public int TotalBens { get; init; }
    public int TotalBensComValorComercial { get; init; }
    public decimal ValorEstimadoPatrimonial { get; init; }
    public required RegistroUsuarioDto Fechamento { get; init; }

    /// <summary>true só depois que a data da votação e a referência da ata forem registradas por usuário autorizado.</summary>
    public bool Votado { get; init; }
    public VotacaoInventarioDto? Votacao { get; init; }
    public required string Versao { get; init; }
}

public sealed record VotacaoInventarioDto(DateOnly DataVotacao, string ReferenciaAta, PessoaResumoDto RegistradaPor, DateTime RegistradaEmUtc);

public sealed record InventarioItemDto(Guid BemId, string Tombo, string Nome, string Descricao, string? Categoria, EstadoConservacao EstadoConservacao,
    string EstadoConservacaoDescricao, SituacaoBem Situacao, string SituacaoDescricao, string Localizacao, OrigemBem Origem, DateOnly? DataAquisicao,
    bool DataAquisicaoDesconhecida, decimal? ValorAquisicao, bool PossuiValorComercial, decimal? ValorEstimado);

public sealed record InventarioDto(InventarioResumoDto Inventario, IReadOnlyList<InventarioItemDto> Itens);

public sealed record AtendimentoDto
{
    public Guid Id { get; init; }
    public Guid DesejoId { get; init; }
    public int Quantidade { get; init; }
    public DateOnly Data { get; init; }
    public required PessoaResumoDto Responsavel { get; init; }
    public OrigemAtendimento Origem { get; init; }
    public required string OrigemDescricao { get; init; }
    public string? Observacoes { get; init; }

    /// <summary>Bens físicos recebidos neste atendimento (vazio para consumíveis).</summary>
    public required IReadOnlyList<BemAtendimentoDto> Bens { get; init; }
    public required RegistroUsuarioDto Registro { get; init; }
    public bool Estornado { get; init; }
    public string? MotivoEstorno { get; init; }
    public RegistroUsuarioDto? Estorno { get; init; }
}

public sealed record BemAtendimentoDto(Guid BemId, string Tombo, string Nome, bool CriadoPeloAtendimento, bool VinculoAtivo);

public sealed record DesejoDto
{
    public Guid Id { get; init; }
    public required string Nome { get; init; }
    public required string Descricao { get; init; }
    public required string Justificativa { get; init; }
    public TipoItemDesejo TipoItem { get; init; }
    public required string TipoItemDescricao { get; init; }
    public int QuantidadeDesejada { get; init; }
    public int QuantidadeAtendida { get; init; }
    public int QuantidadePendente { get; init; }
    public NivelUrgenciaAlmoxarifado Urgencia { get; init; }
    public required string UrgenciaDescricao { get; init; }
    public string? Categoria { get; init; }

    /// <summary>null = custo desconhecido (não significa custo zero).</summary>
    public decimal? CustoUnitarioEstimado { get; init; }
    public decimal? CustoTotalEstimado { get; init; }
    public decimal? CustoPendenteEstimado { get; init; }
    public DateOnly? DataNecessidade { get; init; }
    public string? LinkReferencia { get; init; }
    public string? Observacoes { get; init; }
    public SituacaoDesejo Situacao { get; init; }
    public required string SituacaoDescricao { get; init; }
    public string? MotivoCancelamento { get; init; }
    public RegistroUsuarioDto? Cancelamento { get; init; }
    public bool Excluido { get; init; }
    public ExclusaoDto? Exclusao { get; init; }
    public required PessoaResumoDto Solicitante { get; init; }
    public DateTime CriadoEmUtc { get; init; }
    public RegistroUsuarioDto? UltimaAlteracao { get; init; }

    /// <summary>Atendimentos (inclusive estornados), do mais antigo ao mais recente. Vazio nas listagens.</summary>
    public required IReadOnlyList<AtendimentoDto> Atendimentos { get; init; }
    public required string Versao { get; init; }
}

public sealed record DesejosPorUrgenciaDto(NivelUrgenciaAlmoxarifado Urgencia, string Descricao, int Necessidades, int QuantidadePendente,
    decimal CustoPendenteEstimado, int NecessidadesSemCustoEstimado);

public sealed record ResponsavelAlmoxarifadoDto(Guid Id, PessoaResumoDto Membro, string? CargoMembro, DateOnly DataInicio, string? Observacoes,
    RegistroUsuarioDto Designacao, bool Vigente, string? MotivoRevogacao, RegistroUsuarioDto? Revogacao, string Versao);

/// <summary>Resultado de um atendimento: Criado=false quando é o reenvio idempotente (mesma chave e mesmos dados).</summary>
public sealed record RegistrarAtendimentoResultado(DesejoDto Desejo, AtendimentoDto Atendimento, bool Criado);

/// <summary>Arquivo CSV gerado por um relatório.</summary>
public sealed record ArquivoCsv(string Nome, byte[] Conteudo);

/// <summary>Responsável e origem da operação, preenchidos pelo controller (nunca pelo corpo).</summary>
public abstract class ComandoAlmoxarifado
{
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

/// <summary>Dados físicos e de guarda de um bem, comuns ao cadastro direto e ao recebimento num atendimento.</summary>
public abstract class BemDadosRequest : ComandoAlmoxarifado
{
    public string? Nome { get; set; }
    public string? Descricao { get; set; }
    public string? Categoria { get; set; }
    public EstadoConservacao? EstadoConservacao { get; set; }
    public bool? PossuiValorComercial { get; set; }

    /// <summary>Obrigatório (≥ 0) com valor comercial; proibido sem valor comercial.</summary>
    public decimal? ValorEstimado { get; set; }
    public string? Localizacao { get; set; }

    /// <summary>Membro ativo responsável pela guarda (opcional).</summary>
    public Guid? ResponsavelGuardaId { get; set; }
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? NumeroSerie { get; set; }
    public decimal? ValorAquisicao { get; set; }
    public string? Observacoes { get; set; }
}

/// <summary>Cadastro completo de um bem: dados físicos, aquisição e origem.</summary>
public abstract class BemConteudoRequest : BemDadosRequest
{
    /// <summary>Obrigatória e não futura, salvo dataAquisicaoDesconhecida = true (acervo antigo).</summary>
    public DateOnly? DataAquisicao { get; set; }
    public bool? DataAquisicaoDesconhecida { get; set; }

    /// <summary>Obrigatória quando dataAquisicaoDesconhecida = true (ex.: "acervo recebido da gestão anterior, sem nota").</summary>
    public string? JustificativaDataDesconhecida { get; set; }
    public OrigemBem? Origem { get; set; }

    /// <summary>Obrigatório quando origem = Outro.</summary>
    public string? OrigemDetalhe { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CadastrarBemRequest : BemConteudoRequest, IRequest<BemDto>;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarBemRequest : BemConteudoRequest, IRequest<BemDto>
{
    public string? Versao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ExcluirBemRequest : ComandoAlmoxarifado, IRequest<BemDto>
{
    /// <summary>Obrigatória (cadastro indevido).</summary>
    public string? Justificativa { get; set; }
    public string? Versao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EmprestarBemRequest : ComandoAlmoxarifado, IRequest<EmprestimoDto>
{
    /// <summary>Membro ativo responsável pela retirada (destinatário externo não integra esta versão).</summary>
    public Guid? MembroId { get; set; }
    public DateOnly? DataRetirada { get; set; }
    public DateOnly? PrevisaoDevolucao { get; set; }

    /// <summary>Estado na saída (passa a ser o estado do bem); Inservivel é recusado.</summary>
    public EstadoConservacao? ConservacaoSaida { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DevolverEmprestimoRequest : ComandoAlmoxarifado, IRequest<EmprestimoDto>
{
    public DateOnly? DataDevolucao { get; set; }
    public EstadoConservacao? ConservacaoRetorno { get; set; }
    public string? Observacoes { get; set; }

    /// <summary>true: o bem segue para manutenção (motivoManutencao obrigatório); false/omitido: volta a Disponível.</summary>
    public bool? EncaminharParaManutencao { get; set; }
    public string? MotivoManutencao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarPerdaFurtoRequest : ComandoAlmoxarifado, IRequest<EmprestimoDto>
{
    public DateOnly? Data { get; set; }

    /// <summary>Perda ou Furto: encerra o empréstimo e baixa o bem com o mesmo motivo.</summary>
    public EncerramentoEmprestimo? Motivo { get; set; }
    public string? Justificativa { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EntradaManutencaoRequest : ComandoAlmoxarifado, IRequest<ManutencaoDto>
{
    public DateOnly? DataEntrada { get; set; }
    public string? Motivo { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SaidaManutencaoRequest : ComandoAlmoxarifado, IRequest<ManutencaoDto>
{
    public DateOnly? DataSaida { get; set; }

    /// <summary>Estado após a manutenção (passa a ser o estado do bem).</summary>
    public EstadoConservacao? ConservacaoSaida { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BaixarBemRequest : ComandoAlmoxarifado, IRequest<BemDto>
{
    public DateOnly? DataBaixa { get; set; }
    public MotivoBaixa? Motivo { get; set; }
    public string? Justificativa { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class FecharInventarioRequest : ComandoAlmoxarifado, IRequest<InventarioDto>
{
    public DateOnly? DataInventario { get; set; }

    /// <summary>Membro ativo responsável pela conferência.</summary>
    public Guid? ResponsavelId { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarVotacaoInventarioRequest : ComandoAlmoxarifado, IRequest<InventarioDto>
{
    public DateOnly? DataVotacao { get; set; }

    /// <summary>Referência da ata da Comissão da Igreja (ex.: "Ata 12/2026, item 4").</summary>
    public string? ReferenciaAta { get; set; }
    public string? Versao { get; set; }
}

public abstract class DesejoConteudoRequest : ComandoAlmoxarifado
{
    public string? Nome { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Justificativa / uso previsto (obrigatória).</summary>
    public string? Justificativa { get; set; }
    public TipoItemDesejo? TipoItem { get; set; }
    public int? QuantidadeDesejada { get; set; }
    public NivelUrgenciaAlmoxarifado? Urgencia { get; set; }
    public string? Categoria { get; set; }
    public decimal? CustoUnitarioEstimado { get; set; }
    public DateOnly? DataNecessidade { get; set; }

    /// <summary>URL http(s) absoluta.</summary>
    public string? LinkReferencia { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CadastrarDesejoRequest : DesejoConteudoRequest, IRequest<DesejoDto>;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarDesejoRequest : DesejoConteudoRequest, IRequest<DesejoDto>
{
    public string? Versao { get; set; }
}

/// <summary>Cancelamento (motivo) ou exclusão lógica (justificativa) de uma necessidade; os atendimentos são preservados.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EncerrarDesejoRequest : ComandoAlmoxarifado, IRequest<DesejoDto>
{
    public string? Justificativa { get; set; }
    public string? Versao { get; set; }
    [JsonIgnore] public bool Excluir { get; set; }
}

/// <summary>Bem físico criado no recebimento: origem e data de aquisição vêm do atendimento.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NovoBemAtendimentoRequest : BemDadosRequest;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarAtendimentoRequest : ComandoAlmoxarifado, IRequest<RegistrarAtendimentoResultado>
{
    public int? Quantidade { get; set; }
    public DateOnly? Data { get; set; }

    /// <summary>Membro ativo responsável pelo recebimento.</summary>
    public Guid? ResponsavelId { get; set; }
    public OrigemAtendimento? Origem { get; set; }
    public string? Observacoes { get; set; }

    /// <summary>Item patrimonial: bens a criar (um por unidade), somados aos existentes, devem totalizar a quantidade.</summary>
    public List<NovoBemAtendimentoRequest>? BensNovos { get; set; }

    /// <summary>Item patrimonial: ids de bens já cadastrados (não excluídos, não baixados e sem outro vínculo ativo).</summary>
    public List<Guid>? BensExistentes { get; set; }

    [JsonIgnore] public string? IdempotencyKey { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EstornarAtendimentoRequest : ComandoAlmoxarifado, IRequest<DesejoDto>
{
    public string? Justificativa { get; set; }
    [JsonIgnore] public Guid AtendimentoId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DesignarResponsavelRequest : ComandoAlmoxarifado, IRequest<ResponsavelAlmoxarifadoDto>
{
    public Guid? MembroId { get; set; }
    public DateOnly? DataInicio { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RevogarResponsavelRequest : ComandoAlmoxarifado, IRequest<ResponsavelAlmoxarifadoDto>
{
    public string? Motivo { get; set; }
    public string? Versao { get; set; }
}

public sealed record ListarBensQuery : IRequest<PaginaDto<BemDto>>
{
    /// <summary>Parte do nome ou do tombo.</summary>
    public string? Busca { get; init; }
    public string? Categoria { get; init; }
    public EstadoConservacao? Conservacao { get; init; }
    public SituacaoBem? Situacao { get; init; }

    /// <summary>Parte da localização.</summary>
    public string? Localizacao { get; init; }
    public bool? PossuiValorComercial { get; init; }
    public bool IncluirExcluidos { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Services.RegrasAlmoxarifado.PageSizePadrao;
}

public sealed record ObterBemQuery(Guid Id) : IRequest<BemDto?>;

public sealed record HistoricoQuery(Guid Id, bool DoDesejo, DateOnly? De, DateOnly? Ate, TipoMovimentacaoAlmoxarifado? Tipo)
    : IRequest<IReadOnlyList<HistoricoAlmoxarifadoDto>?>;

public sealed record ListarEmprestimosQuery : IRequest<PaginaDto<EmprestimoDto>>
{
    public Guid? BemId { get; init; }
    public Guid? MembroId { get; init; }

    /// <summary>true: só abertos; false: só encerrados.</summary>
    public bool? Abertos { get; init; }

    /// <summary>true: só abertos com previsão vencida.</summary>
    public bool? Atrasados { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Services.RegrasAlmoxarifado.PageSizePadrao;
}

public sealed record ListarManutencoesQuery(Guid BemId) : IRequest<IReadOnlyList<ManutencaoDto>?>;

public sealed record RelatorioPatrimonioQuery(ListarBensQuery Filtros) : IRequest<RelatorioPatrimonioDto>;

public sealed record ExportarPatrimonioQuery(ListarBensQuery Filtros) : IRequest<ArquivoCsv>;

public sealed record RelatorioEmprestimosQuery(bool SomenteAtrasados) : IRequest<IReadOnlyList<EmprestimoDto>>;

public sealed record ExportarEmprestimosQuery(bool SomenteAtrasados) : IRequest<ArquivoCsv>;

public sealed record ListarInventariosQuery(int Page, int PageSize) : IRequest<PaginaDto<InventarioResumoDto>>;

public sealed record ObterInventarioQuery(Guid Id) : IRequest<InventarioDto?>;

public sealed record ListarDesejosQuery : IRequest<PaginaDto<DesejoDto>>
{
    /// <summary>Parte do nome.</summary>
    public string? Busca { get; init; }
    public NivelUrgenciaAlmoxarifado? Urgencia { get; init; }
    public SituacaoDesejo? Situacao { get; init; }
    public string? Categoria { get; init; }
    public TipoItemDesejo? TipoItem { get; init; }

    /// <summary>true: só Pendente e ParcialmenteAtendida.</summary>
    public bool SomenteAbertas { get; init; }
    public bool IncluirExcluidos { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Services.RegrasAlmoxarifado.PageSizePadrao;
}

public sealed record ObterDesejoQuery(Guid Id) : IRequest<DesejoDto?>;

public sealed record RelatorioDesejosQuery : IRequest<IReadOnlyList<DesejosPorUrgenciaDto>>;

public sealed record ListarResponsaveisQuery(bool IncluirRevogados) : IRequest<IReadOnlyList<ResponsavelAlmoxarifadoDto>>;

public sealed record ObterEmprestimoQuery(Guid Id) : IRequest<EmprestimoDto?>;
