using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

// Patrimônio e lista de desejos do almoxarifado (issue #126). Os valores numéricos dos enums são estáveis e gravados no banco:
// nunca renumere nem reaproveite um valor. Description é o rótulo exibido (GET /api/Almoxarifado/Enums).

[JsonConverter(typeof(JsonStringEnumConverter<EstadoConservacao>))]
public enum EstadoConservacao
{
    [Description("Novo")]
    Novo = 1,

    [Description("Bom")]
    Bom = 2,

    [Description("Regular")]
    Regular = 3,

    [Description("Ruim")]
    Ruim = 4,

    /// <summary>Sem condição de uso: não é emprestado.</summary>
    [Description("Inservível")]
    Inservivel = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter<SituacaoBem>))]
public enum SituacaoBem
{
    [Description("Disponível")]
    Disponivel = 1,

    [Description("Emprestado")]
    Emprestado = 2,

    [Description("Em manutenção")]
    EmManutencao = 3,

    [Description("Baixado")]
    Baixado = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<OrigemBem>))]
public enum OrigemBem
{
    [Description("Compra")]
    Compra = 1,

    [Description("Doação")]
    Doacao = 2,

    /// <summary>Exige origemDetalhe.</summary>
    [Description("Outro")]
    Outro = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<MotivoBaixa>))]
public enum MotivoBaixa
{
    [Description("Perda")]
    Perda = 1,

    [Description("Furto")]
    Furto = 2,

    [Description("Descarte")]
    Descarte = 3,

    [Description("Doação")]
    Doacao = 4,

    [Description("Outro")]
    Outro = 5,
}

/// <summary>Como um empréstimo foi encerrado. Perda e furto encerram o empréstimo e baixam o bem na mesma operação.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EncerramentoEmprestimo>))]
public enum EncerramentoEmprestimo
{
    [Description("Devolvido")]
    Devolvido = 1,

    [Description("Perda")]
    Perda = 2,

    [Description("Furto")]
    Furto = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<NivelUrgenciaAlmoxarifado>))]
public enum NivelUrgenciaAlmoxarifado
{
    /// <summary>Necessidade real que pode ser planejada.</summary>
    [Description("Pouco urgente, mas necessário")]
    PoucoUrgenteMasNecessario = 1,

    /// <summary>Falta que compromete uma atividade próxima.</summary>
    [Description("Urgente")]
    Urgente = 2,

    /// <summary>Falta que exige prioridade imediata.</summary>
    [Description("Muito urgente")]
    MuitoUrgente = 3,
}

/// <summary>Pendente, parcialmente atendida e atendida decorrem das quantidades; cancelada exige motivo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SituacaoDesejo>))]
public enum SituacaoDesejo
{
    [Description("Pendente")]
    Pendente = 1,

    [Description("Parcialmente atendida")]
    ParcialmenteAtendida = 2,

    [Description("Atendida")]
    Atendida = 3,

    [Description("Cancelada")]
    Cancelada = 4,
}

/// <summary>Bem patrimonial: o recebimento cria ou vincula bens físicos. Consumível: o recebimento não gera patrimônio nem estoque.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TipoItemDesejo>))]
public enum TipoItemDesejo
{
    [Description("Bem patrimonial")]
    Patrimonial = 1,

    [Description("Material consumível")]
    Consumivel = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<OrigemAtendimento>))]
public enum OrigemAtendimento
{
    [Description("Compra")]
    Compra = 1,

    [Description("Doação")]
    Doacao = 2,
}

/// <summary>Operação gravada em almoxarifado_historico (coluna Acao, pelo nome); filtro tipo dos históricos.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TipoMovimentacaoAlmoxarifado>))]
public enum TipoMovimentacaoAlmoxarifado
{
    [Description("Cadastro do bem")]
    Cadastro = 1,

    [Description("Alteração do cadastro do bem")]
    Alteracao = 2,

    [Description("Exclusão lógica do bem")]
    Exclusao = 3,

    [Description("Empréstimo")]
    Emprestimo = 4,

    [Description("Devolução")]
    Devolucao = 5,

    [Description("Perda ou furto em empréstimo")]
    PerdaOuFurto = 6,

    [Description("Entrada em manutenção")]
    EntradaManutencao = 7,

    [Description("Saída de manutenção")]
    SaidaManutencao = 8,

    [Description("Baixa")]
    Baixa = 9,

    [Description("Fechamento de inventário")]
    FechamentoInventario = 10,

    [Description("Registro da votação do inventário")]
    VotacaoInventario = 11,

    [Description("Cadastro da necessidade")]
    CadastroDesejo = 12,

    [Description("Alteração da necessidade")]
    AlteracaoDesejo = 13,

    [Description("Cancelamento da necessidade")]
    CancelamentoDesejo = 14,

    [Description("Exclusão lógica da necessidade")]
    ExclusaoDesejo = 15,

    [Description("Atendimento da necessidade")]
    Atendimento = 16,

    [Description("Estorno de atendimento")]
    EstornoAtendimento = 17,

    [Description("Designação de responsável pelo almoxarifado")]
    Designacao = 18,

    [Description("Revogação de responsável pelo almoxarifado")]
    RevogacaoDesignacao = 19,
}

/// <summary>Um bem físico identificável: cinco barracas são cinco registros. Nomes podem repetir; o tombo é único e gerado pelo banco.</summary>
public class BemPatrimonial
{
    public Guid Id { get; set; }

    /// <summary>Sequencial (IDENTITY) que origina o tombo; lacunas são possíveis (operação desfeita), nunca reaproveitado.</summary>
    public int NumeroTombo { get; set; }

    /// <summary>Coluna calculada e persistida a partir de NumeroTombo (PAT-000001).</summary>
    public string Tombo { get; set; } = string.Empty;

    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public string? Categoria { get; set; }
    public EstadoConservacao EstadoConservacao { get; set; }
    public bool PossuiValorComercial { get; set; }
    public decimal? ValorEstimado { get; set; }
    public DateOnly? DataAquisicao { get; set; }

    /// <summary>Acervo antigo sem data conhecida: DataAquisicao fica null (nunca inventada) e a justificativa é obrigatória.</summary>
    public bool DataAquisicaoDesconhecida { get; set; }
    public string? JustificativaDataDesconhecida { get; set; }
    public OrigemBem Origem { get; set; }
    public string? OrigemDetalhe { get; set; }
    public required string Localizacao { get; set; }
    public Guid? ResponsavelGuardaId { get; set; }
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? NumeroSerie { get; set; }
    public decimal? ValorAquisicao { get; set; }
    public string? Observacoes { get; set; }
    public SituacaoBem Situacao { get; set; } = SituacaoBem.Disponivel;

    public DateOnly? DataBaixa { get; set; }
    public MotivoBaixa? MotivoBaixa { get; set; }
    public string? JustificativaBaixa { get; set; }
    public Guid? BaixadoPorUsuarioId { get; set; }
    public DateTime? BaixadoEmUtc { get; set; }

    public bool Excluido { get; set; }
    public string? MotivoExclusao { get; set; }
    public Guid? ExcluidoPorUsuarioId { get; set; }
    public DateTime? ExcluidoEmUtc { get; set; }

    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];
}

/// <summary>Retirada de um bem por um membro. Aberto enquanto Encerramento é null; no máximo um aberto por bem (índice único filtrado).</summary>
public class EmprestimoBem
{
    public Guid Id { get; set; }
    public Guid BemId { get; set; }
    public Guid MembroId { get; set; }
    public DateOnly DataRetirada { get; set; }
    public DateOnly PrevisaoDevolucao { get; set; }
    public EstadoConservacao ConservacaoSaida { get; set; }
    public string? ObservacoesSaida { get; set; }
    public EncerramentoEmprestimo? Encerramento { get; set; }
    public DateOnly? DataEncerramento { get; set; }
    public EstadoConservacao? ConservacaoRetorno { get; set; }
    public string? ObservacoesRetorno { get; set; }
    public Guid RegistradoPorUsuarioId { get; set; }
    public DateTime RegistradoEmUtc { get; set; }
    public Guid? EncerramentoRegistradoPorUsuarioId { get; set; }
    public DateTime? EncerramentoRegistradoEmUtc { get; set; }

    public bool Aberto => Encerramento is null;
}

/// <summary>Passagem de um bem pela manutenção. Aberta enquanto DataSaida é null; no máximo uma aberta por bem.</summary>
public class ManutencaoBem
{
    public Guid Id { get; set; }
    public Guid BemId { get; set; }
    public DateOnly DataEntrada { get; set; }
    public required string Motivo { get; set; }
    public string? ObservacoesEntrada { get; set; }
    public DateOnly? DataSaida { get; set; }
    public EstadoConservacao? ConservacaoSaida { get; set; }
    public string? ObservacoesSaida { get; set; }
    public Guid RegistradaPorUsuarioId { get; set; }
    public DateTime RegistradaEmUtc { get; set; }
    public Guid? SaidaRegistradaPorUsuarioId { get; set; }
    public DateTime? SaidaRegistradaEmUtc { get; set; }

    public bool Aberta => DataSaida is null;
}

/// <summary>
/// Conferência fechada do patrimônio, com a fotografia dos bens (itens imutáveis). Começa sem votação: só é apresentado como votado depois
/// que um usuário autorizado registra a data da votação e a referência da ata da Comissão da Igreja. O sistema não substitui a votação.
/// </summary>
public class InventarioPatrimonio
{
    public Guid Id { get; set; }
    public DateOnly DataInventario { get; set; }
    public Guid ResponsavelId { get; set; }
    public string? Observacoes { get; set; }
    public int TotalBens { get; set; }
    public int TotalBensComValorComercial { get; set; }
    public decimal ValorEstimadoPatrimonial { get; set; }
    public Guid FechadoPorUsuarioId { get; set; }
    public DateTime FechadoEmUtc { get; set; }
    public DateOnly? DataVotacao { get; set; }
    public string? ReferenciaAta { get; set; }
    public Guid? VotacaoRegistradaPorUsuarioId { get; set; }
    public DateTime? VotacaoRegistradaEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];
}

/// <summary>Fotografia de um bem no fechamento do inventário: copiada, nunca recalculada (trigger impede alteração).</summary>
public class InventarioItem
{
    public Guid Id { get; set; }
    public Guid InventarioId { get; set; }
    public Guid BemId { get; set; }
    public required string Tombo { get; set; }
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public string? Categoria { get; set; }
    public EstadoConservacao EstadoConservacao { get; set; }
    public SituacaoBem Situacao { get; set; }
    public required string Localizacao { get; set; }
    public OrigemBem Origem { get; set; }
    public DateOnly? DataAquisicao { get; set; }
    public bool DataAquisicaoDesconhecida { get; set; }
    public decimal? ValorAquisicao { get; set; }
    public bool PossuiValorComercial { get; set; }
    public decimal? ValorEstimado { get; set; }
}

/// <summary>Necessidade de aquisição da lista de desejos. Cadastrar não cria patrimônio, despesa, pagamento nem reserva de dinheiro.</summary>
public class DesejoAlmoxarifado
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public required string Justificativa { get; set; }
    public TipoItemDesejo TipoItem { get; set; }
    public int QuantidadeDesejada { get; set; }

    /// <summary>Soma dos atendimentos não estornados (mantida na mesma transação de cada atendimento/estorno).</summary>
    public int QuantidadeAtendida { get; set; }
    public NivelUrgenciaAlmoxarifado Urgencia { get; set; }
    public string? Categoria { get; set; }
    public decimal? CustoUnitarioEstimado { get; set; }
    public DateOnly? DataNecessidade { get; set; }
    public string? LinkReferencia { get; set; }
    public string? Observacoes { get; set; }
    public SituacaoDesejo Situacao { get; set; } = SituacaoDesejo.Pendente;
    public string? MotivoCancelamento { get; set; }
    public Guid? CanceladoPorUsuarioId { get; set; }
    public DateTime? CanceladoEmUtc { get; set; }
    public bool Excluido { get; set; }
    public string? MotivoExclusao { get; set; }
    public Guid? ExcluidoPorUsuarioId { get; set; }
    public DateTime? ExcluidoEmUtc { get; set; }

    /// <summary>Solicitante: o usuário autenticado que cadastrou.</summary>
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];
}

/// <summary>Recebimento (compra ou doação) de parte ou de toda a quantidade de uma necessidade. Idempotente por usuário + Idempotency-Key.</summary>
public class AtendimentoDesejo
{
    public Guid Id { get; set; }
    public Guid DesejoId { get; set; }
    public int Quantidade { get; set; }
    public DateOnly Data { get; set; }
    public Guid ResponsavelId { get; set; }
    public OrigemAtendimento Origem { get; set; }
    public string? Observacoes { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public Guid RegistradoPorUsuarioId { get; set; }
    public DateTime RegistradoEmUtc { get; set; }
    public string? MotivoEstorno { get; set; }
    public Guid? EstornadoPorUsuarioId { get; set; }
    public DateTime? EstornadoEmUtc { get; set; }

    public bool Estornado => EstornadoEmUtc.HasValue;
}

/// <summary>Bem físico recebido num atendimento (criado nele ou já existente). Um bem tem no máximo um vínculo ativo.</summary>
public class AtendimentoBem
{
    public Guid Id { get; set; }
    public Guid AtendimentoId { get; set; }
    public Guid BemId { get; set; }
    public bool CriadoPeloAtendimento { get; set; }
    public bool Ativo { get; set; } = true;
    public Guid? DesvinculadoPorUsuarioId { get; set; }
    public DateTime? DesvinculadoEmUtc { get; set; }
}

/// <summary>
/// Designação de um membro como responsável pelo almoxarifado do clube, independente do cargo (o membro mantém o seu). Concede a gestão do
/// almoxarifado enquanto não revogada; a revogação vale na requisição seguinte.
/// </summary>
public class ResponsavelAlmoxarifado
{
    public Guid Id { get; set; }
    public Guid MembroId { get; set; }
    public DateOnly DataInicio { get; set; }
    public string? Observacoes { get; set; }
    public Guid DesignadoPorUsuarioId { get; set; }
    public DateTime DesignadoEmUtc { get; set; }
    public string? MotivoRevogacao { get; set; }
    public Guid? RevogadoPorUsuarioId { get; set; }
    public DateTime? RevogadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];

    public bool Vigente => RevogadoEmUtc is null;
}

/// <summary>Auditoria por trigger das tabelas do almoxarifado: estado anterior e novo, responsável, IP, operação e justificativa.</summary>
public class HistoricoAlmoxarifado
{
    public Guid Id { get; set; }
    public required string Tabela { get; set; }
    public Guid RegistroId { get; set; }
    public Guid? BemId { get; set; }
    public Guid? DesejoId { get; set; }
    public string? Acao { get; set; }
    public string? Motivo { get; set; }
    public string? EstadoAnteriorJson { get; set; }
    public required string EstadoNovoJson { get; set; }
    public Guid UsuarioResponsavelId { get; set; }
    public required string IpResponsavel { get; set; }
    public DateTime OcorridoEmUtc { get; set; }
}
