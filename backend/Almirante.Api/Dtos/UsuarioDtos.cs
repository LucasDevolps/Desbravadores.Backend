using System.Text.Json.Serialization;
using MediatR;

namespace Almirante.Api.Dtos;

// Campos editáveis de um usuário, comuns a POST e PUT /api/Usuarios.
public abstract class UsuarioConteudoRequest
{
    /// <summary>Obrigatório, até 200 caracteres.</summary>
    public string? Nome { get; set; }

    /// <summary>E-mail de login: obrigatório, formato válido, único (sem diferenciar maiúsculas/minúsculas).</summary>
    public string? Email { get; set; }

    /// <summary>Id de um cargo ativo (ver GET /api/Cargos).</summary>
    public Guid? CargoId { get; set; }

    // Preenchidos pelo controller a partir das claims validadas; nunca lidos do corpo.
    [JsonIgnore] public bool SolicitanteEhAdmin { get; set; }
}

/// <summary>POST /api/Usuarios.</summary>
public sealed class RegistrarUsuarioRequest : UsuarioConteudoRequest, IRequest<UsuarioListItemDto>
{
    /// <summary>Senha inicial; segue a política de senha da aplicação.</summary>
    public string? Senha { get; set; }
}

/// <summary>PUT /api/Usuarios/{id}. Não altera senha nem situação (ativo/inativo).</summary>
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
