using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Operações comuns aos vínculos membro–classe e membro–especialidade (issue #125), só para GestaoFormacao (ADM, DIR, DIRA, SEC).
/// O membro consulta a própria formação em /api/Me/Formacao; tesoureiro, instrutor e demais cargos não acessam os dados de outros membros.
/// Toda alteração exige o vínculo existente, é transacional e grava o estado anterior em formacao_historico.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.GestaoFormacao)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public abstract class FormacaoVinculosControllerBase(ISender mediator) : ControllerBase
{
    protected ISender Mediator { get; } = mediator;

    protected abstract TipoVinculo Tipo { get; }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Consulta um vínculo com requisitos, progresso, pré-requisitos, validação final e investidura/entrega.")]
    [EndpointDescription("requisitos traz a árvore da revisão usada pelo vínculo, com situação, datas, validador, evidências e satisfeito " +
        "(resultado das regras). progressoDetalhado: unidades obrigatórias, percentual (critério em criterio), pendências, pré-requisitos, " +
        "podeConcluir e bloqueios. O percentual não substitui a conclusão registrada. Registro histórico: sem requisitos nem progresso.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VinculoDto>> Obter(Guid id, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(new ObterVinculoQuery(Tipo, id), cancellationToken);
        return dto is null ? NaoEncontrado() : Ok(dto);
    }

    [HttpGet("{id:guid}/Historico")]
    [EndpointSummary("Histórico de alterações do vínculo, dos requisitos, evidências e validações (estado anterior e novo, responsável, ação, justificativa).")]
    [ProducesResponseType<IReadOnlyList<HistoricoFormacaoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<HistoricoFormacaoDto>>> Historico(Guid id, CancellationToken cancellationToken)
    {
        var historico = await Mediator.Send(new ListarHistoricoVinculoQuery(Tipo, id), cancellationToken);
        return historico is null ? NaoEncontrado() : Ok(historico);
    }

    [HttpPost("Historicos")]
    [EndpointSummary("Registra uma conquista anterior à implantação (registro histórico, já concluído).")]
    [EndpointDescription("Sem revisão nem validação individual de requisitos (nada é fabricado). origemHistorica obrigatória; o usuário autenticado " +
        "fica como responsável pelo lançamento. dataInicio, dataConclusao e dataInvestiduraOuEntrega opcionais (null = desconhecida), não futuras " +
        "e em ordem cronológica. investiduraOuEntregaRealizada obrigatório. Regras de início não se aplicam a fatos passados. 409 com vínculo " +
        "não cancelado da mesma classe/especialidade.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegistrarHistorico([FromBody] RegistrarHistoricoRequest request, CancellationToken cancellationToken)
    {
        Preencher(request, Guid.Empty);
        var dto = await Mediator.Send(request, cancellationToken);
        return Created($"{Request.Path.Value![..Request.Path.Value!.LastIndexOf('/')]}/{dto.Id}", dto);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Atualiza instrutor/responsável e observações do vínculo.")]
    [EndpointDescription("Substitui instrutorId (usuário ativo; null remove) e observacoes. Exige versao. Ser instrutor não concede permissão de escrita.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> Atualizar(Guid id, [FromBody] AtualizarVinculoRequest request, CancellationToken cancellationToken) =>
        EnviarAsync(request, id, cancellationToken);

    [HttpPut("{id:guid}/Requisitos/{requisitoId:guid}")]
    [EndpointSummary("Atualiza a situação de um requisito do participante (Pendente, EmAndamento ou Validado).")]
    [EndpointDescription("Só em vínculo em andamento e em item da revisão do vínculo (grupos são calculados pelos subitens: 409). Validado registra " +
        "o usuário e a data/hora da validação; item que exige especialidades só é validado com evidências válidas suficientes (409). " +
        "dataCumprimento opcional, entre o início e hoje. Retirar uma validação exige justificativa. Vínculo concluído: reabra antes (409).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> AtualizarRequisito(Guid id, Guid requisitoId, [FromBody] AtualizarRequisitoVinculoRequest request,
        CancellationToken cancellationToken)
    {
        request.RequisitoId = requisitoId;
        return EnviarAsync(request, id, cancellationToken);
    }

    [HttpPost("{id:guid}/Concluir")]
    [EndpointSummary("Registra a conclusão (validação final) pelo fluxo normal.")]
    [EndpointDescription("Corpo: dataConclusao e versao. 409 enquanto houver requisito obrigatório pendente (requisitosPendentes) ou pré-requisito " +
        "não atendido (preRequisitos). dataConclusao entre o início e hoje e não anterior ao cumprimento de um requisito validado. Concluir não " +
        "registra investidura/entrega. Nas classes de liderança, a conclusão local não é aprovação oficial.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> Concluir(Guid id, [FromBody] ConcluirVinculoRequest request, CancellationToken cancellationToken) =>
        EnviarAsync(request, id, cancellationToken);

    [HttpPost("{id:guid}/Cancelar")]
    [EndpointSummary("Cancela o vínculo (justificativa obrigatória; progresso e histórico preservados).")]
    [EndpointDescription("Fluxo normal: só em andamento (concluído: reabra antes). Registro histórico: cancela mesmo concluído, desde que nenhuma " +
        "outra conclusão dependa dele. Corpo: justificativa e versao.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> Cancelar(Guid id, [FromBody] OperacaoVinculoRequest request, CancellationToken cancellationToken) =>
        Operacao(id, request, OperacaoVinculo.Cancelar, cancellationToken);

    [HttpPost("{id:guid}/Retomar")]
    [EndpointSummary("Retoma um vínculo cancelado do fluxo normal, com o progresso preservado.")]
    [EndpointDescription("409 se já houver outro vínculo não cancelado da mesma classe/especialidade (nunca duplica conquistas).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> Retomar(Guid id, [FromBody] OperacaoVinculoRequest request, CancellationToken cancellationToken) =>
        Operacao(id, request, OperacaoVinculo.Retomar, cancellationToken);

    [HttpPost("{id:guid}/Reabrir")]
    [EndpointSummary("Reabre um vínculo concluído para correção (volta a em andamento).")]
    [EndpointDescription("Justificativa obrigatória. Conclusão e investidura/entrega saem do vínculo e ficam preservadas no histórico (estado anterior) " +
        "para análise. 409 se outra conclusão depender desta (classe pré-requisito, especialidade usada como evidência ou pré-requisito): " +
        "a lista vem em dependentes.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> Reabrir(Guid id, [FromBody] OperacaoVinculoRequest request, CancellationToken cancellationToken) =>
        Operacao(id, request, OperacaoVinculo.Reabrir, cancellationToken);

    [HttpPost("{id:guid}/MigrarRevisao")]
    [EndpointSummary("Migra um vínculo em andamento para a revisão vigente dos requisitos (explícito, justificado e auditado).")]
    [EndpointDescription("Corpo: revisaoId (a publicada da mesma classe/especialidade), justificativa e versao. O progresso da revisão anterior é " +
        "preservado e nenhuma validação é copiada: os requisitos da nova revisão começam pendentes. Evidências vinculadas são desativadas.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> MigrarRevisao(Guid id, [FromBody] MigrarRevisaoRequest request, CancellationToken cancellationToken) =>
        EnviarAsync(request, id, cancellationToken);

    protected Task<ActionResult<VinculoDto>> Operacao(Guid id, OperacaoVinculoRequest request, OperacaoVinculo operacao, CancellationToken cancellationToken)
    {
        request.Operacao = operacao;
        return EnviarAsync(request, id, cancellationToken);
    }

    protected async Task<ActionResult<VinculoDto>> EnviarAsync(ComandoVinculoRequest request, Guid id, CancellationToken cancellationToken)
    {
        Preencher(request, id);
        return Ok(await Mediator.Send(request, cancellationToken));
    }

    protected async Task<IActionResult> AssociarAsync(ComandoVinculoRequest request, CancellationToken cancellationToken)
    {
        Preencher(request, Guid.Empty);
        var dto = await Mediator.Send(request, cancellationToken);
        return Created($"{Request.Path.Value!.TrimEnd('/')}/{dto.Id}", dto);
    }

    protected async Task<ActionResult<PaginaDto<VinculoResumoDto>>> ListarAsync(ListarVinculosQuery query, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(query with { Tipo = Tipo }, cancellationToken));

    private void Preencher(ComandoVinculoRequest request, Guid id)
    {
        request.Tipo = Tipo;
        request.Id = id;
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
    }

    private NotFoundObjectResult NaoEncontrado() =>
        NotFound(new ProblemDetails { Title = "Vínculo não encontrado.", Status = StatusCodes.Status404NotFound });
}

/// <summary>Acompanhamento membro–classe (regulares, avançadas e liderança).</summary>
[Route("api/Formacao/Classes")]
public sealed class FormacaoClassesController(ISender mediator) : FormacaoVinculosControllerBase(mediator)
{
    protected override TipoVinculo Tipo => TipoVinculo.Classe;

    [HttpGet]
    [EndpointSummary("Lista os vínculos membro–classe, paginados, com o resumo do progresso (relatórios por participante e por situação).")]
    [EndpointDescription("Filtros: membroId, classe, grupo (Regular, Avancada, Lideranca), situacao (EmAndamento, Concluida, Cancelada), historico, " +
        "aguardandoInvestiduraOuEntrega (concluídos sem investidura) e período: periodo = Inicio | Conclusao | InvestiduraOuEntrega define qual " +
        "data de/ate (yyyy-MM-dd, inclusivas) filtram. ordenarPor = Membro (padrão) | Inicio | Conclusao | InvestiduraOuEntrega, sempre com " +
        "desempate estável (nome do membro, classe, id). Cada item traz requisitosCumpridos e requisitosPendentes (unidades da raiz).")]
    [ProducesResponseType<PaginaDto<VinculoResumoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginaDto<VinculoResumoDto>>> Listar(
        [FromQuery] Guid? membroId = null,
        [FromQuery] ClasseDesbravador? classe = null,
        [FromQuery] GrupoClasse? grupo = null,
        [FromQuery] SituacaoVinculo? situacao = null,
        [FromQuery] bool? historico = null,
        [FromQuery] bool? aguardandoInvestiduraOuEntrega = null,
        [FromQuery] PeriodoFormacao? periodo = null,
        [FromQuery] DateOnly? de = null,
        [FromQuery] DateOnly? ate = null,
        [FromQuery] OrdenacaoVinculos ordenarPor = OrdenacaoVinculos.Membro,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Services.RegrasFormacao.PageSizePadrao,
        CancellationToken cancellationToken = default) =>
        ListarAsync(new ListarVinculosQuery
        {
            MembroId = membroId, Classe = classe, Grupo = grupo, Situacao = situacao, Historico = historico,
            AguardandoInvestiduraOuEntrega = aguardandoInvestiduraOuEntrega, Periodo = periodo, De = de, Ate = ate, OrdenarPor = ordenarPor,
            Page = page, PageSize = pageSize,
        }, cancellationToken);

    [HttpPost]
    [EndpointSummary("Associa uma classe a um membro existente (inclusive diretoria e adultos, e classes de liderança).")]
    [EndpointDescription("membroId (usuário ativo), classe, dataInicio (não futura) obrigatórios; modalidade Padrao (padrão) ou Agrupada " +
        "(classes agrupadas, só regulares/avançadas: cada classe mantém o próprio vínculo); instrutorId e observacoes opcionais. Usa a revisão " +
        "publicada dos requisitos (409 sem revisão). 409 se já houver vínculo não cancelado da mesma classe. Liderança: aplica as regras de " +
        "início da referência oficial (idade mínima, classes anteriores, experiência como líder investido); pendências em preRequisitos (409).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Associar([FromBody] AssociarClasseRequest request, CancellationToken cancellationToken) =>
        AssociarAsync(request, cancellationToken);

    [HttpPost("{id:guid}/Investidura")]
    [EndpointSummary("Registra a investidura de uma classe concluída (ato separado da conclusão).")]
    [EndpointDescription("Corpo: data (entre a conclusão e hoje) e versao. Liderança: exige as validações externas confirmadas (inclusive " +
        "APROVACAO_OFICIAL) e, em Líder, 18 anos completos na data. 409 se não concluída, já investida ou com pendências (preRequisitos).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> RegistrarInvestidura(Guid id, [FromBody] RegistrarReconhecimentoRequest request, CancellationToken cancellationToken) =>
        EnviarAsync(request, id, cancellationToken);

    [HttpDelete("{id:guid}/Investidura")]
    [EndpointSummary("Desfaz a investidura registrada (correção com justificativa; o registro anterior fica no histórico).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> DesfazerInvestidura(Guid id, [FromBody] OperacaoVinculoRequest request, CancellationToken cancellationToken) =>
        Operacao(id, request, OperacaoVinculo.DesfazerReconhecimento, cancellationToken);

    [HttpPost("{id:guid}/Requisitos/{requisitoId:guid}/Evidencias")]
    [EndpointSummary("Vincula uma especialidade concluída do membro como evidência de um requisito da classe.")]
    [EndpointDescription("Corpo: membroEspecialidadeId. O requisito precisa exigir especialidades; a conquista precisa ser do mesmo membro, estar " +
        "concluída e atender área/especialidade exigidas e, com naoRealizadaAnteriormente, ter sido concluída a partir do início da classe e não " +
        "ser evidência de outra classe. Uma conquista conta uma única vez por classe (409).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> VincularEvidencia(Guid id, Guid requisitoId, [FromBody] VincularEvidenciaRequest request,
        CancellationToken cancellationToken)
    {
        request.RequisitoId = requisitoId;
        return EnviarAsync(request, id, cancellationToken);
    }

    [HttpDelete("{id:guid}/Requisitos/{requisitoId:guid}/Evidencias/{membroEspecialidadeId:guid}")]
    [EndpointSummary("Remove (logicamente) uma evidência de especialidade, com justificativa.")]
    [EndpointDescription("409 se a remoção deixaria um requisito validado sem evidências suficientes: retire a validação antes.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> RemoverEvidencia(Guid id, Guid requisitoId, Guid membroEspecialidadeId,
        [FromBody] RemoverEvidenciaRequest request, CancellationToken cancellationToken)
    {
        request.RequisitoId = requisitoId;
        request.MembroEspecialidadeId = membroEspecialidadeId;
        return EnviarAsync(request, id, cancellationToken);
    }

    [HttpPut("{id:guid}/ValidacoesExternas/{codigo}")]
    [EndpointSummary("Registra uma validação externa exigida pela referência oficial (classes de liderança).")]
    [EndpointDescription("codigo: um dos requisitosExternos da classe (ex.: RECOMENDACAO_COMISSAO_IGREJA, CADASTRO_SGC, APROVACAO_OFICIAL). " +
        "Corpo: confirmada, data (obrigatória ao confirmar), referencia (documento/protocolo), observacao. Desfazer uma confirmação exige " +
        "justificativa e, com investidura registrada, desfazer a investidura antes. O acompanhamento local não substitui SGC nem aprovações externas.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> RegistrarValidacaoExterna(Guid id, string codigo, [FromBody] RegistrarValidacaoExternaRequest request,
        CancellationToken cancellationToken)
    {
        request.Codigo = codigo;
        return EnviarAsync(request, id, cancellationToken);
    }
}

/// <summary>Acompanhamento membro–especialidade.</summary>
[Route("api/Formacao/Especialidades")]
public sealed class FormacaoEspecialidadesController(ISender mediator) : FormacaoVinculosControllerBase(mediator)
{
    protected override TipoVinculo Tipo => TipoVinculo.Especialidade;

    [HttpGet]
    [EndpointSummary("Lista os vínculos membro–especialidade, paginados, com o resumo do progresso.")]
    [EndpointDescription("Filtros: membroId, especialidadeId, area, situacao, historico, aguardandoInvestiduraOuEntrega (concluídas sem entrega do " +
        "distintivo) e periodo = Inicio | Conclusao | InvestiduraOuEntrega (entrega) com de/ate. ordenarPor como em /api/Formacao/Classes, com " +
        "desempate estável (nome do membro, especialidade, id).")]
    [ProducesResponseType<PaginaDto<VinculoResumoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<PaginaDto<VinculoResumoDto>>> Listar(
        [FromQuery] Guid? membroId = null,
        [FromQuery] Guid? especialidadeId = null,
        [FromQuery] string? area = null,
        [FromQuery] SituacaoVinculo? situacao = null,
        [FromQuery] bool? historico = null,
        [FromQuery] bool? aguardandoInvestiduraOuEntrega = null,
        [FromQuery] PeriodoFormacao? periodo = null,
        [FromQuery] DateOnly? de = null,
        [FromQuery] DateOnly? ate = null,
        [FromQuery] OrdenacaoVinculos ordenarPor = OrdenacaoVinculos.Membro,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Services.RegrasFormacao.PageSizePadrao,
        CancellationToken cancellationToken = default) =>
        ListarAsync(new ListarVinculosQuery
        {
            MembroId = membroId, EspecialidadeId = especialidadeId, Area = area, Situacao = situacao, Historico = historico,
            AguardandoInvestiduraOuEntrega = aguardandoInvestiduraOuEntrega, Periodo = periodo, De = de, Ate = ate, OrdenarPor = ordenarPor,
            Page = page, PageSize = pageSize,
        }, cancellationToken);

    [HttpPost]
    [EndpointSummary("Associa uma especialidade ativa a um membro existente.")]
    [EndpointDescription("membroId (usuário ativo), especialidadeId (ativa; inativa: 409) e dataInicio obrigatórios. Usa a revisão publicada dos " +
        "requisitos (409 sem revisão). 409 se já houver vínculo não cancelado da mesma especialidade. Pré-requisitos (outras especialidades) " +
        "são exigidos na conclusão.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Associar([FromBody] AssociarEspecialidadeRequest request, CancellationToken cancellationToken) =>
        AssociarAsync(request, cancellationToken);

    [HttpPost("{id:guid}/Entrega")]
    [EndpointSummary("Registra a entrega do distintivo de uma especialidade concluída (ato separado da conclusão).")]
    [EndpointDescription("Corpo: data (entre a conclusão e hoje) e versao. 409 se não concluída ou já entregue.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> RegistrarEntrega(Guid id, [FromBody] RegistrarReconhecimentoRequest request, CancellationToken cancellationToken) =>
        EnviarAsync(request, id, cancellationToken);

    [HttpDelete("{id:guid}/Entrega")]
    [EndpointSummary("Desfaz a entrega registrada (correção com justificativa; o registro anterior fica no histórico).")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<VinculoDto>> DesfazerEntrega(Guid id, [FromBody] OperacaoVinculoRequest request, CancellationToken cancellationToken) =>
        Operacao(id, request, OperacaoVinculo.DesfazerReconhecimento, cancellationToken);
}
