using Almirante.Api.Dtos;

namespace Almirante.Api.Services;

/// <summary>Consulta de cargos para o cadastro de usuários; a administração está em <see cref="AutorizacaoAdminService"/>.</summary>
public class CargosService(AutorizacaoAdminService admin)
{
    public Task<IReadOnlyList<CargoDto>> ListAsync(CancellationToken cancellationToken) => admin.ListarCargosAsync(cancellationToken);
}
