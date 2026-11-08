using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Dtos;

/// <summary>
/// PUT /api/Usuarios/{id}/foto. Conteúdo e formato vêm do multipart já lido com limites (LeitorUploadFoto); responsável,
/// IP e papel vêm das claims e da conexão, nunca do corpo.
/// </summary>
public sealed record EnviarFotoUsuarioRequest(Guid Id, byte[] Conteudo, FormatoFoto Formato, Guid UsuarioResponsavelId,
    string IpResponsavel, string SolicitanteRole) : IRequest<UsuarioListItemDto>;

/// <summary>GET /api/Usuarios/{id}/foto: null quando o usuário não existe, não tem foto ou o arquivo não está disponível.</summary>
public sealed record ObterFotoUsuarioQuery(Guid Id) : IRequest<FotoUsuarioArquivo?>;

/// <summary>DELETE /api/Usuarios/{id}/foto. Sem corpo: tudo vem da rota e do contexto autenticado.</summary>
public sealed record ExcluirFotoUsuarioRequest(Guid Id, Guid UsuarioResponsavelId, string IpResponsavel, string SolicitanteRole) : IRequest<Unit>;

/// <summary>Arquivo WebP da foto (fechado pelo FileStreamResult) e o ETag derivado da parte aleatória do nome.</summary>
public sealed record FotoUsuarioArquivo(Stream Conteudo, string ETag);
