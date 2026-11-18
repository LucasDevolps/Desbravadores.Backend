using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Dashboard")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class DashboardController(ISender mediator, PermissoesService permissoes) : ControllerBase
{
    [HttpGet]
    [RequerPermissao(Permissoes.DashboardConsultar)]
    [EndpointSummary("Indicadores consolidados do clube: financeiro realizado, desbravadores ativos, próximos eventos e atividades.")]
    [EndpointDescription("Financeiro: soma de valorPago dos lançamentos ativos com pagamento registrado e dataPagamento no período " +
        "(saldo = entradas − saídas); pendentes, atrasados, excluídos e \"Pago\" históricos sem dados de pagamento não entram. " +
        "Sem parâmetros, o período é o mês atual no fuso do clube (Clube:FusoHorario). dataInicial e dataFinal (yyyy-MM-dd, inclusivas) " +
        "são informadas juntas, com no máximo 366 dias. desbravadores, proximosEventos e atividades não dependem do período: " +
        "usuários ativos com cargo DS e até 5 passeios ativos (agrupados por eventoGrupoId) com data a partir de hoje, em ordem de data. " +
        "atividadesPlanejadas conta os passeios ativos (cada eventoGrupoId uma vez, sem limite) de hoje até hoje + 29 dias; " +
        "atividadesRecentes traz até 5 passeios ativos dos 30 dias anteriores a hoje, da data mais recente para a mais antiga. " +
        "Atividade é o passeio (evento) cadastrado; não há situação operacional nem taxa de conclusão. " +
        "Exige dashboard.consultar. financeiro só para quem também tem dashboard.financeiro.consultar; para os demais " +
        "(Secretário) vem null e os valores nem são calculados.")]
    [ProducesResponseType<DashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DashboardResponse>> Get(
        [FromQuery] string? dataInicial = null,
        [FromQuery] string? dataFinal = null,
        CancellationToken cancellationToken = default)
    {
        var incluirFinanceiro = await permissoes.TemAsync(User, Permissoes.DashboardFinanceiroConsultar, cancellationToken);
        return Ok(await mediator.Send(new GetDashboardQuery(dataInicial, dataFinal, incluirFinanceiro), cancellationToken));
    }
}
