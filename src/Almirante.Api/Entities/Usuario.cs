namespace Almirante.Api.Entities;

public class Usuario
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalizado { get; set; }

    // Dados pessoais opcionais (NULL em registros legados). Cpf é gravado já normalizado (sem espaços externos,
    // pontos e hífen; ver UsuariosService.NormalizarCpf) e é único entre TODOS os usuários, inclusive inativos
    // (índice UX_Usuarios_Cpf, filtrado por Cpf IS NOT NULL). Texto, nunca número: preserva zeros à esquerda.
    public string? Cpf { get; set; }
    public DateOnly? DataNascimento { get; set; }
    public string? Telefone { get; set; }

    // Responsável legal (NULL em registros legados). Nome e e-mail são exigidos só para menor de 18 anos (ver
    // RegraResponsavel); CPF não é único (irmãos têm o mesmo responsável). Todos varchar: o nome é recusado se tiver
    // caractere fora da página 1252 (TextoVarchar), os demais são ASCII como os do próprio usuário.
    public string? ResponsavelNome { get; set; }
    public string? ResponsavelEmail { get; set; }
    public string? ResponsavelCpf { get; set; }
    public string? ResponsavelTelefone { get; set; }

    // Dados de identificação e saúde, todos opcionais (NULL em registros legados e quando não informados; nada é
    // inventado). Rg: texto livre do documento (formato varia por estado), não é único. TipoSanguineo: um dos oito ABO/Rh
    // (CHECK CK_Usuarios_TipoSanguineo). DoencasAlergias: UM campo de texto livre com doenças, alergias e cuidados de
    // saúde, de propósito sem estrutura própria. Todos varchar (página 1252, ver TextoVarchar).
    public string? Rg { get; set; }
    public string? TipoSanguineo { get; set; }
    public string? DoencasAlergias { get; set; }

    // Foto de perfil: só o caminho RELATIVO à pasta Media:Storage:Path, no formato usuarios/{Id:N}/{aleatório}.webp
    // (CHECK CK_Usuarios_FotoCaminho garante o formato e que a foto é deste usuário). O binário nunca vai para o banco.
    public string? FotoCaminho { get; set; }

    public required string SenhaHash { get; set; }
    public Guid CargoId { get; set; }
    public Cargo? Cargo { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;

    // Exclusão lógica (DELETE /api/Usuarios/{id}): o registro nunca é apagado fisicamente. Toda mudança
    // cadastral (inclusive esta) é copiada para _usuarios_hist pelo trigger TR_Usuarios_Historico.
    public bool Ativo { get; set; } = true;
    public long SecurityVersion { get; set; }

    // Bloqueio temporário por conta contra força bruta distribuída (ver Services/LoginLockout.cs).
    // Persistido no SQL Server, portanto compartilhado entre instâncias da API.
    public int FalhasLoginConsecutivas { get; set; }
    public DateTime? UltimaFalhaLoginUtc { get; set; }
    public DateTime? LoginBloqueadoAteUtc { get; set; }
    public ICollection<Lancamento> Lancamentos { get; set; } = [];
}
