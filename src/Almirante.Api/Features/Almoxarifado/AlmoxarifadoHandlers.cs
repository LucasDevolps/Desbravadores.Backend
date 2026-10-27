using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Almoxarifado;

public sealed class CadastrarBemHandler(PatrimonioService service) : IRequestHandler<CadastrarBemRequest, BemDto>
{
    public Task<BemDto> Handle(CadastrarBemRequest request, CancellationToken ct) => service.CadastrarAsync(request, ct);
}

public sealed class AtualizarBemHandler(PatrimonioService service) : IRequestHandler<AtualizarBemRequest, BemDto>
{
    public Task<BemDto> Handle(AtualizarBemRequest request, CancellationToken ct) => service.AtualizarAsync(request, ct);
}

public sealed class ExcluirBemHandler(PatrimonioService service) : IRequestHandler<ExcluirBemRequest, BemDto>
{
    public Task<BemDto> Handle(ExcluirBemRequest request, CancellationToken ct) => service.ExcluirAsync(request, ct);
}

public sealed class BaixarBemHandler(PatrimonioService service) : IRequestHandler<BaixarBemRequest, BemDto>
{
    public Task<BemDto> Handle(BaixarBemRequest request, CancellationToken ct) => service.BaixarAsync(request, ct);
}

public sealed class EmprestarBemHandler(PatrimonioService service) : IRequestHandler<EmprestarBemRequest, EmprestimoDto>
{
    public Task<EmprestimoDto> Handle(EmprestarBemRequest request, CancellationToken ct) => service.EmprestarAsync(request, ct);
}

public sealed class DevolverEmprestimoHandler(PatrimonioService service) : IRequestHandler<DevolverEmprestimoRequest, EmprestimoDto>
{
    public Task<EmprestimoDto> Handle(DevolverEmprestimoRequest request, CancellationToken ct) => service.DevolverAsync(request, ct);
}

public sealed class RegistrarPerdaFurtoHandler(PatrimonioService service) : IRequestHandler<RegistrarPerdaFurtoRequest, EmprestimoDto>
{
    public Task<EmprestimoDto> Handle(RegistrarPerdaFurtoRequest request, CancellationToken ct) => service.RegistrarPerdaFurtoAsync(request, ct);
}

public sealed class EntradaManutencaoHandler(PatrimonioService service) : IRequestHandler<EntradaManutencaoRequest, ManutencaoDto>
{
    public Task<ManutencaoDto> Handle(EntradaManutencaoRequest request, CancellationToken ct) => service.RegistrarEntradaManutencaoAsync(request, ct);
}

public sealed class SaidaManutencaoHandler(PatrimonioService service) : IRequestHandler<SaidaManutencaoRequest, ManutencaoDto>
{
    public Task<ManutencaoDto> Handle(SaidaManutencaoRequest request, CancellationToken ct) => service.RegistrarSaidaManutencaoAsync(request, ct);
}

public sealed class ObterBemHandler(PatrimonioService service) : IRequestHandler<ObterBemQuery, BemDto?>
{
    public Task<BemDto?> Handle(ObterBemQuery request, CancellationToken ct) => service.ObterAsync(request.Id, ct);
}

public sealed class ListarBensHandler(PatrimonioService service) : IRequestHandler<ListarBensQuery, PaginaDto<BemDto>>
{
    public Task<PaginaDto<BemDto>> Handle(ListarBensQuery request, CancellationToken ct) => service.ListarAsync(request, ct);
}

public sealed class HistoricoHandler(PatrimonioService service) : IRequestHandler<HistoricoQuery, IReadOnlyList<HistoricoAlmoxarifadoDto>?>
{
    public Task<IReadOnlyList<HistoricoAlmoxarifadoDto>?> Handle(HistoricoQuery request, CancellationToken ct) => service.HistoricoAsync(request, ct);
}

public sealed class ObterEmprestimoHandler(PatrimonioService service) : IRequestHandler<ObterEmprestimoQuery, EmprestimoDto?>
{
    public Task<EmprestimoDto?> Handle(ObterEmprestimoQuery request, CancellationToken ct) => service.ObterEmprestimoAsync(request.Id, ct);
}

public sealed class ListarEmprestimosHandler(PatrimonioService service) : IRequestHandler<ListarEmprestimosQuery, PaginaDto<EmprestimoDto>>
{
    public Task<PaginaDto<EmprestimoDto>> Handle(ListarEmprestimosQuery request, CancellationToken ct) => service.ListarEmprestimosAsync(request, ct);
}

public sealed class ListarManutencoesHandler(PatrimonioService service) : IRequestHandler<ListarManutencoesQuery, IReadOnlyList<ManutencaoDto>?>
{
    public Task<IReadOnlyList<ManutencaoDto>?> Handle(ListarManutencoesQuery request, CancellationToken ct) =>
        service.ListarManutencoesAsync(request.BemId, ct);
}

public sealed class RelatorioPatrimonioHandler(PatrimonioService service) : IRequestHandler<RelatorioPatrimonioQuery, RelatorioPatrimonioDto>
{
    public Task<RelatorioPatrimonioDto> Handle(RelatorioPatrimonioQuery request, CancellationToken ct) => service.RelatorioAsync(request.Filtros, ct);
}

public sealed class ExportarPatrimonioHandler(PatrimonioService service) : IRequestHandler<ExportarPatrimonioQuery, ArquivoCsv>
{
    public Task<ArquivoCsv> Handle(ExportarPatrimonioQuery request, CancellationToken ct) => service.ExportarAsync(request.Filtros, ct);
}

public sealed class RelatorioEmprestimosHandler(PatrimonioService service) : IRequestHandler<RelatorioEmprestimosQuery, IReadOnlyList<EmprestimoDto>>
{
    public Task<IReadOnlyList<EmprestimoDto>> Handle(RelatorioEmprestimosQuery request, CancellationToken ct) =>
        service.RelatorioEmprestimosAsync(request.SomenteAtrasados, ct);
}

public sealed class ExportarEmprestimosHandler(PatrimonioService service) : IRequestHandler<ExportarEmprestimosQuery, ArquivoCsv>
{
    public Task<ArquivoCsv> Handle(ExportarEmprestimosQuery request, CancellationToken ct) =>
        service.ExportarEmprestimosAsync(request.SomenteAtrasados, ct);
}

public sealed class FecharInventarioHandler(InventarioService service) : IRequestHandler<FecharInventarioRequest, InventarioDto>
{
    public Task<InventarioDto> Handle(FecharInventarioRequest request, CancellationToken ct) => service.FecharAsync(request, ct);
}

public sealed class RegistrarVotacaoInventarioHandler(InventarioService service) : IRequestHandler<RegistrarVotacaoInventarioRequest, InventarioDto>
{
    public Task<InventarioDto> Handle(RegistrarVotacaoInventarioRequest request, CancellationToken ct) => service.RegistrarVotacaoAsync(request, ct);
}

public sealed class ListarInventariosHandler(InventarioService service) : IRequestHandler<ListarInventariosQuery, PaginaDto<InventarioResumoDto>>
{
    public Task<PaginaDto<InventarioResumoDto>> Handle(ListarInventariosQuery request, CancellationToken ct) => service.ListarAsync(request, ct);
}

public sealed class ObterInventarioHandler(InventarioService service) : IRequestHandler<ObterInventarioQuery, InventarioDto?>
{
    public Task<InventarioDto?> Handle(ObterInventarioQuery request, CancellationToken ct) => service.ObterAsync(request.Id, ct);
}

public sealed class CadastrarDesejoHandler(DesejosAlmoxarifadoService service) : IRequestHandler<CadastrarDesejoRequest, DesejoDto>
{
    public Task<DesejoDto> Handle(CadastrarDesejoRequest request, CancellationToken ct) => service.CadastrarAsync(request, ct);
}

public sealed class AtualizarDesejoHandler(DesejosAlmoxarifadoService service) : IRequestHandler<AtualizarDesejoRequest, DesejoDto>
{
    public Task<DesejoDto> Handle(AtualizarDesejoRequest request, CancellationToken ct) => service.AtualizarAsync(request, ct);
}

public sealed class EncerrarDesejoHandler(DesejosAlmoxarifadoService service) : IRequestHandler<EncerrarDesejoRequest, DesejoDto>
{
    public Task<DesejoDto> Handle(EncerrarDesejoRequest request, CancellationToken ct) => service.EncerrarAsync(request, ct);
}

public sealed class RegistrarAtendimentoHandler(DesejosAlmoxarifadoService service)
    : IRequestHandler<RegistrarAtendimentoRequest, RegistrarAtendimentoResultado>
{
    public Task<RegistrarAtendimentoResultado> Handle(RegistrarAtendimentoRequest request, CancellationToken ct) =>
        service.RegistrarAtendimentoAsync(request, ct);
}

public sealed class EstornarAtendimentoHandler(DesejosAlmoxarifadoService service) : IRequestHandler<EstornarAtendimentoRequest, DesejoDto>
{
    public Task<DesejoDto> Handle(EstornarAtendimentoRequest request, CancellationToken ct) => service.EstornarAtendimentoAsync(request, ct);
}

public sealed class ObterDesejoHandler(DesejosAlmoxarifadoService service) : IRequestHandler<ObterDesejoQuery, DesejoDto?>
{
    public Task<DesejoDto?> Handle(ObterDesejoQuery request, CancellationToken ct) => service.ObterAsync(request.Id, ct);
}

public sealed class ListarDesejosHandler(DesejosAlmoxarifadoService service) : IRequestHandler<ListarDesejosQuery, PaginaDto<DesejoDto>>
{
    public Task<PaginaDto<DesejoDto>> Handle(ListarDesejosQuery request, CancellationToken ct) => service.ListarAsync(request, ct);
}

public sealed class RelatorioDesejosHandler(DesejosAlmoxarifadoService service)
    : IRequestHandler<RelatorioDesejosQuery, IReadOnlyList<DesejosPorUrgenciaDto>>
{
    public Task<IReadOnlyList<DesejosPorUrgenciaDto>> Handle(RelatorioDesejosQuery request, CancellationToken ct) => service.RelatorioAsync(ct);
}

public sealed class DesignarResponsavelHandler(ResponsaveisAlmoxarifadoService service)
    : IRequestHandler<DesignarResponsavelRequest, ResponsavelAlmoxarifadoDto>
{
    public Task<ResponsavelAlmoxarifadoDto> Handle(DesignarResponsavelRequest request, CancellationToken ct) => service.DesignarAsync(request, ct);
}

public sealed class RevogarResponsavelHandler(ResponsaveisAlmoxarifadoService service)
    : IRequestHandler<RevogarResponsavelRequest, ResponsavelAlmoxarifadoDto>
{
    public Task<ResponsavelAlmoxarifadoDto> Handle(RevogarResponsavelRequest request, CancellationToken ct) => service.RevogarAsync(request, ct);
}

public sealed class ListarResponsaveisHandler(ResponsaveisAlmoxarifadoService service)
    : IRequestHandler<ListarResponsaveisQuery, IReadOnlyList<ResponsavelAlmoxarifadoDto>>
{
    public Task<IReadOnlyList<ResponsavelAlmoxarifadoDto>> Handle(ListarResponsaveisQuery request, CancellationToken ct) =>
        service.ListarAsync(request.IncluirRevogados, ct);
}
