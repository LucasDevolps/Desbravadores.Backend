using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Tesouraria")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class TesourariaController(ISender mediator) : ControllerBase
{
    [HttpGet("Resumo")]
    [RequerPermissao(Permissoes.TesourariaConsultar)]
    [EndpointSummary("Resumo da Tesouraria: série mensal de receitas e despesas realizadas.")]
    [EndpointDescription("receitasDespesas: seis competências (ano, mes) em ordem cronológica, dos cinco meses anteriores ao mês atual " +
        "no fuso do clube (Clube:FusoHorario), inclusive na virada do ano; mês sem movimento vem com entradas e saidas zero. " +
        "Cada competência soma o valorPago dos lançamentos ativos com pagamento registrado e dataPagamento no mês, separado por tipoFluxo " +
        "(entradas = Entrada, saidas = Saida): a mesma regra do financeiro de GET /api/Dashboard, então o mês atual coincide com o " +
        "Dashboard sem parâmetros. Pendentes, atrasados, excluídos e \"Pago\" históricos sem dados de pagamento não entram. " +
        "Exige financeiro.tesouraria.consultar.")]
    [ProducesResponseType<TesourariaResumoResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TesourariaResumoResponse>> ObterResumoAsync(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetTesourariaResumoQuery(), cancellationToken));
}
