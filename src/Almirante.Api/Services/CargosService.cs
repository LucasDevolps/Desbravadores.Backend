using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public class CargosService(AlmiranteDbContext db)
{
    public async Task<IReadOnlyList<CargoDto>> ListAsync(CancellationToken cancellationToken)
    {
        return await db.Cargos
            .OrderBy(c => c.Nome)
            .Select(c => new CargoDto
            {
                Id = c.Id,
                Nome = c.Nome,
                Descricao = c.Descricao,
                Ativo = c.Ativo,
                CriadoPor = c.CriadoPor,
                CriadoEm = c.CriadoEm,
                UltimaAtualizacao = c.UltimaAtualizacao,
                Role = c.Role,
            })
            .ToListAsync(cancellationToken);
    }
}
