using System.Text.Json.Serialization;
using MediatR;

namespace Almirante.Api.Dtos;

// Campos editáveis de um usuário, comuns a POST e PUT /api/Usuarios.
public abstract class UsuarioConteudoRequest
{
    /// <summary>Obrigatório, até 200 caracteres.</summary>
    public string? Nome { get; set; }

    /// <summary>E-mail de login: obrigatório, formato válido, até 100 caracteres ASCII, único (sem diferenciar maiúsculas/minúsculas).</summary>
    public string? Email { get; set; }

    /// <summary>Id de um cargo ativo (ver GET /api/Cargos).</summary>
    public Guid? CargoId { get; set; }

    // Dados pessoais opcionais. Os setters só rodam quando a propriedade está no JSON (inclusive com null), o que
    // distingue OMISSÃO (PUT preserva o valor atual) de LIMPEZA explícita (null ou "" -> NULL no banco).
    private string? _cpf;
    private DateOnly? _dataNascimento;
    private string? _telefone;

    /// <summary>
    /// Opcional; único entre todos os usuários (inclusive inativos). Espaços externos, pontos e hífen são removidos antes de
    /// salvar e comparar ("123.456.789-01" = "12345678901"); até 20 caracteres ASCII após a normalização. No PUT: omitido
    /// mantém o valor atual; null ou "" remove.
    /// </summary>
    public string? Cpf { get => _cpf; set { _cpf = value; CpfInformado = true; } }

    /// <summary>Opcional, formato yyyy-MM-dd, sem horário; não pode ser futura. No PUT: omitida mantém; null remove.</summary>
    public DateOnly? DataNascimento { get => _dataNascimento; set { _dataNascimento = value; DataNascimentoInformada = true; } }

    /// <summary>Opcional, até 20 caracteres ASCII (espaços externos removidos). No PUT: omitido mantém; null ou "" remove.</summary>
    public string? Telefone { get => _telefone; set { _telefone = value; TelefoneInformado = true; } }

    [JsonIgnore] public bool CpfInformado { get; private set; }
    [JsonIgnore] public bool DataNascimentoInformada { get; private set; }
    [JsonIgnore] public bool TelefoneInformado { get; private set; }

    // Preenchidos pelo controller a partir das claims validadas; nunca lidos do corpo.
    [JsonIgnore] public bool SolicitanteEhAdmin { get; set; }
}

/// <summary>POST /api/Usuarios.</summary>
public sealed class RegistrarUsuarioRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    /// <summary>Senha inicial; segue a política de senha da aplicação.</summary>
    public string? Senha { get; set; }
}

/// <summary>
/// PUT /api/Usuarios/{id}. nome, email e cargoId são sempre substituídos (obrigatórios); cpf, dataNascimento e telefone
/// omitidos preservam o valor atual. Não altera senha nem situação (ativo/inativo).
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
