using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public class UsuariosService(AlmiranteDbContext db)
{
    public async Task<IReadOnlyList<UsuarioListItemDto>> ListAsync(CancellationToken cancellationToken)
    {
        return await db.Usuarios
            .OrderBy(u => u.Nome)
            .Select(u => new UsuarioListItemDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                DataCriacao = u.DataCriacao,
                Cargo = new CargoDto
                {
                    Id = u.Cargo!.Id,
                    Nome = u.Cargo.Nome,
                    Descricao = u.Cargo.Descricao,
                    Ativo = u.Cargo.Ativo,
                    CriadoPor = u.Cargo.CriadoPor,
                    CriadoEm = u.Cargo.CriadoEm,
                    UltimaAtualizacao = u.Cargo.UltimaAtualizacao,
                    Role = u.Cargo.Role,
                },
            })
            .ToListAsync(cancellationToken);
    }
}
