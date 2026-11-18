using System.Text.Json.Serialization;
using MediatR;

namespace Almirante.Api.Dtos;

public abstract class DadosPessoaisRequest
{
    private string? _cpf;
    private DateOnly? _dataNascimento;
    private string? _telefone;

    public string? Cpf { get => _cpf; set { _cpf = value; CpfInformado = true; } }

    public DateOnly? DataNascimento { get => _dataNascimento; set { _dataNascimento = value; DataNascimentoInformada = true; } }

    public string? Telefone { get => _telefone; set { _telefone = value; TelefoneInformado = true; } }

    private string? _responsavelNome;
    private string? _responsavelEmail;
    private string? _responsavelCpf;
    private string? _responsavelTelefone;

    public string? ResponsavelNome { get => _responsavelNome; set { _responsavelNome = value; ResponsavelNomeInformado = true; } }

    public string? ResponsavelEmail { get => _responsavelEmail; set { _responsavelEmail = value; ResponsavelEmailInformado = true; } }

    public string? ResponsavelCpf { get => _responsavelCpf; set { _responsavelCpf = value; ResponsavelCpfInformado = true; } }

    public string? ResponsavelTelefone { get => _responsavelTelefone; set { _responsavelTelefone = value; ResponsavelTelefoneInformado = true; } }

    private string? _rg;
    private string? _tipoSanguineo;
    private string? _doencasAlergias;

    public string? Rg { get => _rg; set { _rg = value; RgInformado = true; } }

    public string? TipoSanguineo { get => _tipoSanguineo; set { _tipoSanguineo = value; TipoSanguineoInformado = true; } }

    public string? DoencasAlergias { get => _doencasAlergias; set { _doencasAlergias = value; DoencasAlergiasInformado = true; } }

    private bool? _ehCin;
    private DateOnly? _dataExpiracao;
    private EnderecoRequest? _endereco;

    public bool? EhCin { get => _ehCin; set { _ehCin = value; EhCinInformado = true; } }

    public DateOnly? DataExpiracao { get => _dataExpiracao; set { _dataExpiracao = value; DataExpiracaoInformada = true; } }

    public EnderecoRequest? Endereco { get => _endereco; set { _endereco = value; EnderecoInformado = true; } }

    [JsonIgnore] public bool EhCinInformado { get; private set; }
    [JsonIgnore] public bool DataExpiracaoInformada { get; private set; }
    [JsonIgnore] public bool EnderecoInformado { get; private set; }
    [JsonIgnore] public bool CpfInformado { get; private set; }
    [JsonIgnore] public bool RgInformado { get; private set; }
    [JsonIgnore] public bool TipoSanguineoInformado { get; private set; }
    [JsonIgnore] public bool DoencasAlergiasInformado { get; private set; }
    [JsonIgnore] public bool DataNascimentoInformada { get; private set; }
    [JsonIgnore] public bool TelefoneInformado { get; private set; }
    [JsonIgnore] public bool ResponsavelNomeInformado { get; private set; }
    [JsonIgnore] public bool ResponsavelEmailInformado { get; private set; }
    [JsonIgnore] public bool ResponsavelCpfInformado { get; private set; }
    [JsonIgnore] public bool ResponsavelTelefoneInformado { get; private set; }

    [JsonIgnore]
    public bool AlgumDadoPessoalInformado =>
        CpfInformado || DataNascimentoInformada || TelefoneInformado || ResponsavelNomeInformado || ResponsavelEmailInformado
        || ResponsavelCpfInformado || ResponsavelTelefoneInformado || RgInformado || TipoSanguineoInformado || DoencasAlergiasInformado
        || EhCinInformado || DataExpiracaoInformada || EnderecoInformado;
}

public abstract class UsuarioConteudoRequest : DadosPessoaisRequest
{
    private string? _nome;
    private string? _email;
    private Guid? _cargoId;

    public string? Nome { get => _nome; set { _nome = value; NomeInformado = true; } }
    public string? Email { get => _email; set { _email = value; EmailInformado = true; } }
    public Guid? CargoId { get => _cargoId; set { _cargoId = value; CargoIdInformado = true; } }

    [JsonIgnore] public string SolicitanteRole { get; set; } = string.Empty;

    [JsonIgnore] public bool NomeInformado { get; private set; }
    [JsonIgnore] public bool EmailInformado { get; private set; }
    [JsonIgnore] public bool CargoIdInformado { get; private set; }
}

public sealed class EnderecoRequest
{
    private string? _cep;
    private string? _numero;
    private string? _complemento;

    public string? Cep { get => _cep; set { _cep = value; CepInformado = true; } }

    public string? Numero { get => _numero; set { _numero = value; NumeroInformado = true; } }

    public string? Complemento { get => _complemento; set { _complemento = value; ComplementoInformado = true; } }

    [JsonIgnore] public bool CepInformado { get; private set; }
    [JsonIgnore] public bool NumeroInformado { get; private set; }
    [JsonIgnore] public bool ComplementoInformado { get; private set; }
}

public sealed class EnderecoDto
{
    public required string Cep { get; set; }
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? ComplementoCep { get; set; }
    public string? Unidade { get; set; }
    public string? Bairro { get; set; }
    public required string Localidade { get; set; }
    public required string Uf { get; set; }
    public string? Estado { get; set; }
    public string? Regiao { get; set; }
    public string? Ibge { get; set; }
    public string? Gia { get; set; }
    public string? Ddd { get; set; }
    public string? Siafi { get; set; }
}

public sealed class RegistrarUsuarioRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    public string? Senha { get; set; }
}

public abstract class EdicaoAdministrativaRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

public sealed class UpdateUsuarioRequest : EdicaoAdministrativaRequest;

// PATCH /api/Usuarios/{id}: só os campos presentes no corpo mudam; os omitidos (inclusive nome, email e cargoId) mantêm o valor lido da
// linha travada na transação, de modo que uma alteração concorrente de outro campo não é desfeita. null remove apenas os campos opcionais.
public sealed class PatchUsuarioRequest : EdicaoAdministrativaRequest
{
    [JsonIgnore] public bool AlgumCampoInformado => NomeInformado || EmailInformado || CargoIdInformado || AlgumDadoPessoalInformado;
}

public sealed record DeleteUsuarioRequest(Guid Id, Guid UsuarioResponsavelId, string IpResponsavel, string SolicitanteRole) : IRequest<Unit>;

public sealed record EventoBloqueadorDto(Guid Id, string Nome, DateOnly Data);
