using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Dashboard")]
[Authorize(Policy = Policies.GestaoFinanceira)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class DashboardController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Indicadores consolidados do clube: financeiro realizado, desbravadores ativos e próximos eventos.")]
    [EndpointDescription("Financeiro: soma de valorPago dos lançamentos ativos com pagamento registrado e dataPagamento no período " +
        "(saldo = entradas − saídas); pendentes, atrasados, excluídos e \"Pago\" históricos sem dados de pagamento não entram. " +
        "Sem parâmetros, o período é o mês atual no fuso do clube (Clube:FusoHorario). dataInicial e dataFinal (yyyy-MM-dd, inclusivas) " +
        "são informadas juntas, com no máximo 366 dias. desbravadores e proximosEventos não dependem do período: " +
        "usuários ativos com cargo DS e até 5 passeios ativos (agrupados por eventoGrupoId) com data a partir de hoje, em ordem de data.")]
    [ProducesResponseType<DashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DashboardResponse>> Get(
        [FromQuery] string? dataInicial = null,
        [FromQuery] string? dataFinal = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetDashboardQuery(dataInicial, dataFinal), cancellationToken));
}
