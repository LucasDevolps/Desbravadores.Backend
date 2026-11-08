using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Formacao;

public sealed class ListarClassesHandler(CatalogoFormacaoService service) : IRequestHandler<ListarClassesQuery, ClassesResponse>
{
    public Task<ClassesResponse> Handle(ListarClassesQuery request, CancellationToken ct) => service.ListarClassesAsync(ct);
}

public sealed class ObterClasseHandler(CatalogoFormacaoService service) : IRequestHandler<ObterClasseQuery, ClasseDto?>
{
    public Task<ClasseDto?> Handle(ObterClasseQuery request, CancellationToken ct) => service.ObterClasseAsync(request.Classe, ct);
}

public sealed class ListarRevisoesHandler(CatalogoFormacaoService service) : IRequestHandler<ListarRevisoesQuery, IReadOnlyList<RevisaoResumoDto>?>
{
    public Task<IReadOnlyList<RevisaoResumoDto>?> Handle(ListarRevisoesQuery request, CancellationToken ct) => service.ListarRevisoesAsync(request, ct);
}

public sealed class ListarEspecialidadesHandler(CatalogoFormacaoService service) : IRequestHandler<ListarEspecialidadesQuery, PaginaDto<EspecialidadeDto>>
{
    public Task<PaginaDto<EspecialidadeDto>> Handle(ListarEspecialidadesQuery request, CancellationToken ct) => service.ListarEspecialidadesAsync(request, ct);
}

public sealed class ObterEspecialidadeHandler(CatalogoFormacaoService service) : IRequestHandler<ObterEspecialidadeQuery, EspecialidadeDto?>
{
    public Task<EspecialidadeDto?> Handle(ObterEspecialidadeQuery request, CancellationToken ct) => service.ObterEspecialidadeAsync(request.Id, ct);
}

public sealed class CadastrarEspecialidadeHandler(CatalogoFormacaoService service) : IRequestHandler<CadastrarEspecialidadeRequest, EspecialidadeDto>
{
    public Task<EspecialidadeDto> Handle(CadastrarEspecialidadeRequest request, CancellationToken ct) => service.CadastrarEspecialidadeAsync(request, ct);
}

public sealed class AtualizarEspecialidadeHandler(CatalogoFormacaoService service) : IRequestHandler<AtualizarEspecialidadeRequest, EspecialidadeDto>
{
    public Task<EspecialidadeDto> Handle(AtualizarEspecialidadeRequest request, CancellationToken ct) => service.AtualizarEspecialidadeAsync(request, ct);
}

public sealed class AlterarSituacaoEspecialidadeHandler(CatalogoFormacaoService service)
    : IRequestHandler<AlterarSituacaoEspecialidadeRequest, EspecialidadeDto>
{
    public Task<EspecialidadeDto> Handle(AlterarSituacaoEspecialidadeRequest request, CancellationToken ct) =>
        service.AlterarSituacaoEspecialidadeAsync(request, ct);
}

public sealed class ObterRevisaoHandler(CatalogoFormacaoService service) : IRequestHandler<ObterRevisaoQuery, RevisaoRequisitosDto?>
{
    public Task<RevisaoRequisitosDto?> Handle(ObterRevisaoQuery request, CancellationToken ct) => service.ObterRevisaoAsync(request, ct);
}

public sealed class CriarRevisaoHandler(CatalogoFormacaoService service) : IRequestHandler<CriarRevisaoRequest, RevisaoRequisitosDto>
{
    public Task<RevisaoRequisitosDto> Handle(CriarRevisaoRequest request, CancellationToken ct) => service.CriarRevisaoAsync(request, ct);
}

public sealed class AlterarSituacaoRevisaoHandler(CatalogoFormacaoService service) : IRequestHandler<AlterarSituacaoRevisaoRequest, RevisaoRequisitosDto>
{
    public Task<RevisaoRequisitosDto> Handle(AlterarSituacaoRevisaoRequest request, CancellationToken ct) => service.AlterarSituacaoRevisaoAsync(request, ct);
}

public sealed class ListarVinculosHandler(FormacaoService service) : IRequestHandler<ListarVinculosQuery, PaginaDto<VinculoResumoDto>>
{
    public Task<PaginaDto<VinculoResumoDto>> Handle(ListarVinculosQuery request, CancellationToken ct) => service.ListarAsync(request, ct);
}

public sealed class ObterVinculoHandler(FormacaoService service) : IRequestHandler<ObterVinculoQuery, VinculoDto?>
{
    public Task<VinculoDto?> Handle(ObterVinculoQuery request, CancellationToken ct) => service.ObterAsync(request, ct);
}

public sealed class ListarHistoricoVinculoHandler(FormacaoService service)
    : IRequestHandler<ListarHistoricoVinculoQuery, IReadOnlyList<HistoricoFormacaoDto>?>
{
    public Task<IReadOnlyList<HistoricoFormacaoDto>?> Handle(ListarHistoricoVinculoQuery request, CancellationToken ct) =>
        service.ListarHistoricoAsync(request, ct);
}

public sealed class ObterFormacaoMembroHandler(FormacaoService service) : IRequestHandler<ObterFormacaoMembroQuery, FormacaoMembroDto?>
{
    public Task<FormacaoMembroDto?> Handle(ObterFormacaoMembroQuery request, CancellationToken ct) => service.ObterFormacaoMembroAsync(request.MembroId, ct);
}

public sealed class RelatorioParticipantesHandler(FormacaoService service)
    : IRequestHandler<RelatorioParticipantesQuery, IReadOnlyList<ParticipantesPorSituacaoDto>>
{
    public Task<IReadOnlyList<ParticipantesPorSituacaoDto>> Handle(RelatorioParticipantesQuery request, CancellationToken ct) =>
        service.RelatorioParticipantesAsync(request, ct);
}

public sealed class AssociarClasseHandler(FormacaoService service) : IRequestHandler<AssociarClasseRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(AssociarClasseRequest request, CancellationToken ct) => service.AssociarClasseAsync(request, ct);
}

public sealed class AssociarEspecialidadeHandler(FormacaoService service) : IRequestHandler<AssociarEspecialidadeRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(AssociarEspecialidadeRequest request, CancellationToken ct) => service.AssociarEspecialidadeAsync(request, ct);
}

public sealed class RegistrarHistoricoHandler(FormacaoService service) : IRequestHandler<RegistrarHistoricoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(RegistrarHistoricoRequest request, CancellationToken ct) => service.RegistrarHistoricoAsync(request, ct);
}

public sealed class AtualizarVinculoHandler(FormacaoService service) : IRequestHandler<AtualizarVinculoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(AtualizarVinculoRequest request, CancellationToken ct) => service.AtualizarAsync(request, ct);
}

public sealed class AtualizarRequisitoVinculoHandler(FormacaoService service) : IRequestHandler<AtualizarRequisitoVinculoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(AtualizarRequisitoVinculoRequest request, CancellationToken ct) => service.AtualizarRequisitoAsync(request, ct);
}

public sealed class VincularEvidenciaHandler(FormacaoService service) : IRequestHandler<VincularEvidenciaRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(VincularEvidenciaRequest request, CancellationToken ct) => service.VincularEvidenciaAsync(request, ct);
}

public sealed class RemoverEvidenciaHandler(FormacaoService service) : IRequestHandler<RemoverEvidenciaRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(RemoverEvidenciaRequest request, CancellationToken ct) => service.RemoverEvidenciaAsync(request, ct);
}

public sealed class RegistrarValidacaoExternaHandler(FormacaoService service) : IRequestHandler<RegistrarValidacaoExternaRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(RegistrarValidacaoExternaRequest request, CancellationToken ct) => service.RegistrarValidacaoExternaAsync(request, ct);
}

public sealed class ConcluirVinculoHandler(FormacaoService service) : IRequestHandler<ConcluirVinculoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(ConcluirVinculoRequest request, CancellationToken ct) => service.ConcluirAsync(request, ct);
}

public sealed class RegistrarReconhecimentoHandler(FormacaoService service) : IRequestHandler<RegistrarReconhecimentoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(RegistrarReconhecimentoRequest request, CancellationToken ct) => service.RegistrarReconhecimentoAsync(request, ct);
}

public sealed class OperacaoVinculoHandler(FormacaoService service) : IRequestHandler<OperacaoVinculoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(OperacaoVinculoRequest request, CancellationToken ct) => service.ExecutarOperacaoAsync(request, ct);
}

public sealed class MigrarRevisaoHandler(FormacaoService service) : IRequestHandler<MigrarRevisaoRequest, VinculoDto>
{
    public Task<VinculoDto> Handle(MigrarRevisaoRequest request, CancellationToken ct) => service.MigrarRevisaoAsync(request, ct);
}
