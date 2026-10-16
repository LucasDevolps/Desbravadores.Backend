namespace Almirante.Api.Entities;

// Histórico de alterações cadastrais de usuários (tabela _usuarios_hist). Nenhum código da aplicação insere
// linhas aqui: são geradas exclusivamente pelo trigger TR_Usuarios_Historico, ANTES de a alteração valer (valores
// "deleted" do trigger), sempre que Nome, Email, EmailNormalizado, Cpf, DataNascimento, Telefone, CargoId ou Ativo
// mudam, com o responsável e o IP
// fornecidos via SESSION_CONTEXT (ver UsuariosService). Login, bloqueio e reset de senha não geram linhas.
// Somente leitura para a aplicação (DENY de escrita, ver DbPrivilegeAuditor.AuditTables).
public sealed class UsuarioHistorico
{
    public const string OperacaoUpdate = "UPDATE";
    public const string OperacaoDelete = "DELETE";

    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }

    // Snapshot do usuário imediatamente antes da alteração. CargoNome preserva a função exibida na época,
    // mesmo que o cargo venha a ser renomeado. A senha (hash) nunca é copiada.
    public required string Nome { get; set; }
    public required string Email { get; set; }

    // Dados pessoais no estado anterior. NULL tanto quando o usuário não os tinha quanto em linhas gravadas antes
    // da migration AddUsuariosDadosPessoais (o valor da época não existia e nunca é preenchido retroativamente).
    public string? Cpf { get; set; }
    public DateOnly? DataNascimento { get; set; }
    public string? Telefone { get; set; }

    public Guid CargoId { get; set; }
    public required string CargoNome { get; set; }
    public bool Ativo { get; set; }
    public DateTime DataCriacao { get; set; }

    // UPDATE ou DELETE (exclusão lógica: transição Ativo 1 -> 0), derivado pelo trigger a partir dos dados.
    public required string TipoOperacao { get; set; }

    // Usuário autenticado (claims validadas) e o e-mail de login dele no instante da operação.
    public Guid UsuarioResponsavelId { get; set; }
    public required string UsuarioResponsavelLogin { get; set; }
    public required string IpResponsavel { get; set; }

    // Gerada pelo banco (SYSUTCDATETIME() no trigger).
    public DateTime AlteradoEmUtc { get; set; }
}
