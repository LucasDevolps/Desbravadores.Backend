namespace Almirante.Api.Entities;

// Auditoria de exclusão lógica de lançamentos (tabela lancamentos_deletados). Nenhum código da
// aplicação insere linhas aqui: são geradas exclusivamente pelo trigger
// TR_Lancamentos_AuditoriaExclusaoLogica, disparado ao transicionar Lancamentos.Ativo de 1 para 0
// (ver migration AddLancamentoGeral e LancamentoExclusaoService.DeleteAsync, que alimenta o
// contexto via SESSION_CONTEXT). Este DbSet existe só para leitura/consulta (ex.: testes).
public sealed class LancamentoDeletado
{
    public Guid Id { get; set; }
    public Guid LancamentoId { get; set; }

    // Usuário autenticado responsável pela exclusão (claims validadas), nunca o usuário dono do
    // lançamento (MembroId) nem o login da conexão SQL.
    public Guid UsuarioResponsavelId { get; set; }

    // Suporta IPv4 e IPv6 (ex.: "::ffff:255.255.255.255", 45 caracteres no máximo).
    public required string IpResponsavel { get; set; }

    // Gerada pelo banco (SYSUTCDATETIME() no trigger), nunca pela aplicação.
    public DateTime ExcluidoEmUtc { get; set; }

    public required string Motivo { get; set; }

    // Snapshot do lançamento imediatamente antes da exclusão (valores "deleted" do trigger),
    // preservado mesmo que o lançamento original venha a ser alterado depois.
    public Guid? MembroId { get; set; }
    public string? Finalidade { get; set; }
    public required CategoriaLancamento Categoria { get; set; }
    public required TipoFluxoLancamento TipoFluxo { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public required StatusLancamento Status { get; set; }
    public Guid? OperacaoId { get; set; }
    public DateTime DataCriacaoOriginal { get; set; }
    public Guid? EventoId { get; set; }
}
