using System.Text.Json.Serialization;
using MediatR;

namespace Almirante.Api.Dtos;

public abstract class UsuarioConteudoRequest
{
    /// <summary>Obrigatório, até 200 caracteres.</summary>
    public string? Nome { get; set; }

    /// <summary>E-mail de login: obrigatório, formato válido, até 100 caracteres ASCII, único (sem diferenciar maiúsculas/minúsculas).</summary>
    public string? Email { get; set; }

    /// <summary>Id de um cargo ativo (ver GET /api/Cargos).</summary>
    public Guid? CargoId { get; set; }

    private string? _cpf;
    private DateOnly? _dataNascimento;
    private string? _telefone;

    /// <summary>
    /// Obrigatório no cadastro (POST); único entre todos os usuários (inclusive inativos). Espaços externos, pontos e hífen são
    /// removidos antes de salvar e comparar ("123.456.789-09" = "12345678909"); depois disso precisa ter 11 dígitos com dígitos
    /// verificadores válidos. No PUT: omitido mantém o valor atual; null ou "" só é aceito para usuário antigo que ainda não tem CPF
    /// (um CPF gravado não pode ser removido).
    /// </summary>
    public string? Cpf { get => _cpf; set { _cpf = value; CpfInformado = true; } }

    /// <summary>Opcional, formato yyyy-MM-dd, sem horário; não pode ser futura. No PUT: omitida mantém; null remove.</summary>
    public DateOnly? DataNascimento { get => _dataNascimento; set { _dataNascimento = value; DataNascimentoInformada = true; } }

    /// <summary>
    /// Opcional; telefone brasileiro com DDD, gravado só com dígitos: fixo "(11) 3456-7890" (10 dígitos) ou celular
    /// "(11) 98765-4321" (11 dígitos), com ou sem a máscara. No PUT: omitido mantém; null ou "" remove.
    /// </summary>
    public string? Telefone { get => _telefone; set { _telefone = value; TelefoneInformado = true; } }

    private string? _responsavelNome;
    private string? _responsavelEmail;
    private string? _responsavelCpf;
    private string? _responsavelTelefone;

    /// <summary>Até 200 caracteres (espaços externos removidos), sem caracteres fora do Latin1. No PUT: omitido mantém; null ou "" remove.</summary>
    public string? ResponsavelNome { get => _responsavelNome; set { _responsavelNome = value; ResponsavelNomeInformado = true; } }

    /// <summary>Formato válido, até 100 caracteres ASCII; não precisa ser único. No PUT: omitido mantém; null ou "" remove.</summary>
    public string? ResponsavelEmail { get => _responsavelEmail; set { _responsavelEmail = value; ResponsavelEmailInformado = true; } }

    /// <summary>Mesmas regras de cpf; não é único e não pode ser igual ao cpf do próprio usuário. No PUT: omitido mantém; null ou "" remove.</summary>
    public string? ResponsavelCpf { get => _responsavelCpf; set { _responsavelCpf = value; ResponsavelCpfInformado = true; } }

    /// <summary>Mesmas regras de telefone. No PUT: omitido mantém; null ou "" remove.</summary>
    public string? ResponsavelTelefone { get => _responsavelTelefone; set { _responsavelTelefone = value; ResponsavelTelefoneInformado = true; } }

    private string? _rg;
    private string? _tipoSanguineo;
    private string? _doencasAlergias;

    /// <summary>
    /// Opcional, até 20 caracteres: letras sem acento, dígitos, ponto, hífen, barra e espaço, com ao menos um dígito; gravado como
    /// informado (sem espaços externos), não é único. No PUT: omitido mantém; null ou "" remove.
    /// </summary>
    public string? Rg { get => _rg; set { _rg = value; RgInformado = true; } }

    /// <summary>Opcional: A+, A-, B+, B-, AB+, AB-, O+ ou O- (maiúsculas ou minúsculas; gravado em maiúsculas). No PUT: omitido mantém; null ou "" remove.</summary>
    public string? TipoSanguineo { get => _tipoSanguineo; set { _tipoSanguineo = value; TipoSanguineoInformado = true; } }

    /// <summary>
    /// Doenças/alergias: um único texto livre (várias linhas) com doenças, alergias e cuidados de saúde. Opcional, até 2000 caracteres
    /// (espaços externos removidos), sem emojis nem caracteres fora do Latin1; acima do limite é recusado, nunca truncado. No PUT: omitido
    /// mantém; null ou "" remove.
    /// </summary>
    public string? DoencasAlergias { get => _doencasAlergias; set { _doencasAlergias = value; DoencasAlergiasInformado = true; } }

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

    [JsonIgnore] public bool SolicitanteEhAdmin { get; set; }
}

/// <summary>POST /api/Usuarios.</summary>
public sealed class RegistrarUsuarioRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    /// <summary>Senha inicial; segue a política de senha da aplicação.</summary>
    public string? Senha { get; set; }
}

/// <summary>
/// PUT /api/Usuarios/{id}. nome, email e cargoId são sempre substituídos (obrigatórios); cpf, dataNascimento, telefone, rg,
/// tipoSanguineo, doencasAlergias e os campos do responsável omitidos preservam o valor atual. Não altera senha, foto nem situação (ativo/inativo).
/// </summary>
public sealed class UpdateUsuarioRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

/// <summary>DELETE /api/Usuarios/{id} (exclusão lógica). Sem corpo: tudo vem da rota e do contexto autenticado.</summary>
public sealed record DeleteUsuarioRequest(Guid Id, Guid UsuarioResponsavelId, string IpResponsavel, bool SolicitanteEhAdmin) : IRequest<Unit>;

/// <summary>Evento futuro que impede a exclusão de um usuário (extensão "eventos" do 409).</summary>
public sealed record EventoBloqueadorDto(Guid Id, string Nome, DateOnly Data);
