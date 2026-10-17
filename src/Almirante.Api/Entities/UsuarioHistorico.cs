namespace Almirante.Api.Entities;

public sealed class UsuarioHistorico
{
    public const string OperacaoUpdate = "UPDATE";
    public const string OperacaoDelete = "DELETE";

    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }

    public required string Nome { get; set; }
    public required string Email { get; set; }

    public string? Cpf { get; set; }
    public DateOnly? DataNascimento { get; set; }
    public string? Telefone { get; set; }

    public string? ResponsavelNome { get; set; }
    public string? ResponsavelEmail { get; set; }
    public string? ResponsavelCpf { get; set; }
    public string? ResponsavelTelefone { get; set; }
    public string? FotoCaminho { get; set; }

    public string? Rg { get; set; }
    public string? TipoSanguineo { get; set; }
    public string? DoencasAlergias { get; set; }

    /// <summary>null nas linhas gravadas antes de o campo existir.</summary>
    public bool? EhCin { get; set; }
    public DateOnly? DataExpiracao { get; set; }

    public string? EnderecoCep { get; set; }
    public string? EnderecoLogradouro { get; set; }
    public string? EnderecoNumero { get; set; }
    public string? EnderecoComplemento { get; set; }
    public string? EnderecoComplementoCep { get; set; }
    public string? EnderecoUnidade { get; set; }
    public string? EnderecoBairro { get; set; }
    public string? EnderecoLocalidade { get; set; }
    public string? EnderecoUf { get; set; }
    public string? EnderecoEstado { get; set; }
    public string? EnderecoRegiao { get; set; }
    public string? EnderecoIbge { get; set; }
    public string? EnderecoGia { get; set; }
    public string? EnderecoDdd { get; set; }
    public string? EnderecoSiafi { get; set; }

    public Guid CargoId { get; set; }
    public required string CargoNome { get; set; }
    public bool Ativo { get; set; }
    public DateTime DataCriacao { get; set; }

    public required string TipoOperacao { get; set; }

    public Guid UsuarioResponsavelId { get; set; }
    public required string UsuarioResponsavelLogin { get; set; }
    public required string IpResponsavel { get; set; }

    public DateTime AlteradoEmUtc { get; set; }
}
