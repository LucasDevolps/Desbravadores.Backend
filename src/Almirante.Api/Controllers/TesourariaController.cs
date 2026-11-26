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
    [EndpointSummary("Resumo da Tesouraria: saldo em caixa, receita e despesas do mês, mensalidades pendentes e série mensal de receitas e despesas realizadas.")]
    [EndpointDescription("saldoEmCaixa: saldo acumulado de todo o histórico até hoje no fuso do clube, o valorPago das entradas menos o das " +
        "saídas pagas, com a mesma regra de pagamentos registrados; pode ser negativo e é zero sem movimentação. Ainda não há saldo de " +
        "abertura: o saldo parte de zero no primeiro pagamento registrado no sistema. " +
        "receitaMes: total das entradas pagas no mês civil atual do clube (do dia 1 até o fim do mês), igual a entradas da " +
        "última competência de receitasDespesas e a financeiro.entradas de GET /api/Dashboard sem parâmetros; zero em mês sem receitas. " +
        "despesasMes: total das saídas pagas no mês civil atual do clube (do dia 1 até o fim do mês), igual a saidas da " +
        "última competência de receitasDespesas e a financeiro.saidas de GET /api/Dashboard sem parâmetros; zero em mês sem despesas. " +
        "mensalidadesPendentes: quantidade de lançamentos ativos de Entrada com finalidade Mensalidade e status Pendente ou Atrasado, " +
        "de qualquer vencimento; cada lançamento conta uma vez, então duas mensalidades em aberto do mesmo membro contam duas; zero sem " +
        "mensalidades em aberto. Pago (inclusive parcial), excluídos, saídas e outras finalidades não entram. " +
        "receitasDespesas: seis competências (ano, mes) em ordem cronológica, dos cinco meses anteriores ao mês atual " +
        "no fuso do clube (Clube:FusoHorario), inclusive na virada do ano; mês sem movimento vem com entradas e saidas zero. " +
        "Cada competência soma o valorPago dos lançamentos ativos com pagamento registrado e dataPagamento no mês, separado por tipoFluxo " +
        "(entradas = Entrada, saidas = Saida): a mesma regra do financeiro de GET /api/Dashboard, então o mês atual coincide com o " +
        "Dashboard sem parâmetros. Pendentes, atrasados, excluídos e \"Pago\" históricos sem dados de pagamento não entram. " +
        "Exige financeiro.tesouraria.consultar.")]
    [ProducesResponseType<TesourariaResumoResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TesourariaResumoResponse>> ObterResumoAsync(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetTesourariaResumoQuery(), cancellationToken));
}
