using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Almoxarifado do clube (issue #126). Consulta: qualquer cargo ativo exceto DS (ConsultaAlmoxarifado). Escrita: ADM, DIR, DIRA, SEC, ALM e o
/// responsável designado (GestaoAlmoxarifado). Sem autenticação válida: 401; sem permissão: 403. Nada aqui gera lançamento financeiro.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.ConsultaAlmoxarifado)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public abstract class AlmoxarifadoControllerBase(ISender mediator) : ControllerBase
{
    protected ISender Mediator { get; } = mediator;

    protected T Preencher<T>(T request, Guid id = default) where T : ComandoAlmoxarifado
    {
        request.Id = id;
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        return request;
    }

    protected static NotFoundObjectResult NaoEncontrado(string titulo) =>
        new(new ProblemDetails { Title = titulo, Status = StatusCodes.Status404NotFound });

    protected FileContentResult Csv(ArquivoCsv arquivo) => File(arquivo.Conteudo, CsvAlmoxarifado.ContentType, arquivo.Nome);
}

/// <summary>Cadastro, consulta, histórico e movimentações dos bens patrimoniais.</summary>
[Route("api/Almoxarifado/Bens")]
public sealed class AlmoxarifadoBensController(ISender mediator) : AlmoxarifadoControllerBase(mediator)
{
    [HttpGet]
    [EndpointSummary("Lista os bens patrimoniais, paginados (por padrão sem os excluídos logicamente).")]
    [EndpointDescription("busca: parte do nome ou do tombo. categoria: exata (sem diferenciar maiúsculas). conservacao (Novo, Bom, Regular, Ruim, " +
        "Inservivel), situacao (Disponivel, Emprestado, EmManutencao, Baixado), localizacao (parte do texto), possuiValorComercial, " +
        "incluirExcluidos. Ordem: nome, tombo. page a partir de 1; pageSize de 1 a 100 (padrão 20). Bens de mesmo nome são registros distintos " +
        "(tombos diferentes).")]
    [ProducesResponseType<PaginaDto<BemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginaDto<BemDto>>> Listar(
        [FromQuery] string? busca = null,
        [FromQuery] string? categoria = null,
        [FromQuery] EstadoConservacao? conservacao = null,
        [FromQuery] SituacaoBem? situacao = null,
        [FromQuery] string? localizacao = null,
        [FromQuery] bool? possuiValorComercial = null,
        [FromQuery] bool incluirExcluidos = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = RegrasAlmoxarifado.PageSizePadrao,
        CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarBensQuery
        {
            Busca = busca, Categoria = categoria, Conservacao = conservacao, Situacao = situacao, Localizacao = localizacao,
            PossuiValorComercial = possuiValorComercial, IncluirExcluidos = incluirExcluidos, Page = page, PageSize = pageSize,
        }, cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(ObterBem))]
    [EndpointSummary("Consulta um bem (inclusive baixado ou excluído), com empréstimo e manutenção abertos e a origem na lista de desejos.")]
    [ProducesResponseType<BemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BemDto>> ObterBem(Guid id, CancellationToken cancellationToken) =>
        await Mediator.Send(new ObterBemQuery(id), cancellationToken) is { } dto ? Ok(dto) : NaoEncontrado("Bem não encontrado.");

    [HttpPost]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Cadastra um bem físico identificável (o tombo é gerado pelo sistema).")]
    [EndpointDescription("nome, descricao, estadoConservacao, possuiValorComercial, origem e localizacao obrigatórios. Com possuiValorComercial = true, " +
        "valorEstimado é obrigatório e >= 0; com false (troféu, livro de atas, álbum), valorEstimado deve ser null. dataAquisicao obrigatória e não " +
        "futura, salvo acervo antigo: dataAquisicaoDesconhecida = true, dataAquisicao null e justificativaDataDesconhecida obrigatória (a data nunca é " +
        "inventada). origem = Outro exige origemDetalhe. valorAquisicao opcional e >= 0 (distinto da estimativa). responsavelGuardaId: membro ativo. " +
        "Cinco barracas são cinco cadastros. O bem nasce Disponivel. 400: validação.")]
    [ProducesResponseType<BemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Cadastrar([FromBody] CadastrarBemRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request), cancellationToken);
        return CreatedAtRoute(nameof(ObterBem), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Atualiza o cadastro de um bem (estado anterior auditado).")]
    [EndpointDescription("Mesmas regras do cadastro; substitui todos os campos (opcional omitido fica vazio). Exige versao. A situação muda só pelas " +
        "movimentações. 409: versão desatualizada, bem baixado ou excluído.")]
    [ProducesResponseType<BemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BemDto>> Atualizar(Guid id, [FromBody] AtualizarBemRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Exclui logicamente um cadastro indevido (justificativa obrigatória).")]
    [EndpointDescription("Corpo: justificativa (1–255) e versao. 409 se o bem tem empréstimo aberto, já teve movimentações (empréstimo, manutenção, " +
        "baixa: use a baixa), foi recebido num atendimento (estorne o atendimento) ou já está excluído. O registro e o histórico ficam.")]
    [ProducesResponseType<BemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BemDto>> Excluir(Guid id, [FromBody] ExcluirBemRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    [HttpGet("{id:guid}/Historico")]
    [EndpointSummary("Histórico de alterações e movimentações do bem (estado anterior e novo, responsável, operação, justificativa).")]
    [EndpointDescription("Filtros: de/ate (yyyy-MM-dd, inclusivas, data UTC da operação) e tipo (TipoMovimentacaoAlmoxarifado, ex.: Emprestimo, " +
        "Devolucao, Baixa). Ordem cronológica. Inclui empréstimos, manutenções, inventários e atendimentos que envolveram o bem.")]
    [ProducesResponseType<IReadOnlyList<HistoricoAlmoxarifadoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<HistoricoAlmoxarifadoDto>>> Historico(Guid id, [FromQuery] DateOnly? de = null,
        [FromQuery] DateOnly? ate = null, [FromQuery] TipoMovimentacaoAlmoxarifado? tipo = null, CancellationToken cancellationToken = default) =>
        await Mediator.Send(new HistoricoQuery(id, false, de, ate, tipo), cancellationToken) is { } historico
            ? Ok(historico) : NaoEncontrado("Bem não encontrado.");

    [HttpPost("{id:guid}/Emprestimos")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Empresta um bem disponível e utilizável a um membro.")]
    [EndpointDescription("membroId (membro ativo), dataRetirada (não futura, não anterior à aquisição nem à última movimentação), previsaoDevolucao " +
        "(>= dataRetirada) e conservacaoSaida (passa a ser o estado do bem; Inservivel é recusado) obrigatórios; observacoes opcional. 409 se o bem " +
        "estiver emprestado (um empréstimo aberto por bem, também sob concorrência), em manutenção, inservível, baixado ou excluído. Repetir a " +
        "requisição não duplica: a segunda recebe 409.")]
    [ProducesResponseType<EmprestimoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Emprestar(Guid id, [FromBody] EmprestarBemRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request, id), cancellationToken);
        return CreatedAtRoute(nameof(AlmoxarifadoEmprestimosController.ObterEmprestimo), new { id = dto.Id }, dto);
    }

    [HttpGet("{id:guid}/Manutencoes")]
    [EndpointSummary("Manutenções do bem (abertas e encerradas), da mais recente para a mais antiga.")]
    [ProducesResponseType<IReadOnlyList<ManutencaoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ManutencaoDto>>> ListarManutencoes(Guid id, CancellationToken cancellationToken) =>
        await Mediator.Send(new ListarManutencoesQuery(id), cancellationToken) is { } lista ? Ok(lista) : NaoEncontrado("Bem não encontrado.");

    [HttpPost("{id:guid}/Manutencoes")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Registra a entrada do bem em manutenção (situação EmManutencao).")]
    [EndpointDescription("dataEntrada (não futura) e motivo (1–255) obrigatórios; observacoes opcional. 409 se emprestado (devolva antes; a devolução " +
        "também pode encaminhar para manutenção), já em manutenção, baixado ou excluído.")]
    [ProducesResponseType<ManutencaoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EntradaManutencao(Guid id, [FromBody] EntradaManutencaoRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request, id), cancellationToken);
        return Created($"/api/Almoxarifado/Bens/{id}/Manutencoes", dto);
    }

    [HttpPost("{id:guid}/Baixa")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Baixa o bem do patrimônio (data e motivo obrigatórios), preservando cadastro e histórico.")]
    [EndpointDescription("dataBaixa (não futura, não anterior à aquisição nem à última movimentação), motivo (Perda, Furto, Descarte, Doacao, Outro) e " +
        "justificativa obrigatórios. Empréstimo aberto: 409 (encerre como devolução ou como perda/furto, que já baixa o bem). Em manutenção: a " +
        "manutenção é encerrada na data da baixa. 409 se já baixado ou excluído.")]
    [ProducesResponseType<BemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BemDto>> Baixar(Guid id, [FromBody] BaixarBemRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
}

/// <summary>Empréstimos (consulta, devolução, perda/furto) e saída de manutenção.</summary>
[Route("api/Almoxarifado")]
public sealed class AlmoxarifadoEmprestimosController(ISender mediator) : AlmoxarifadoControllerBase(mediator)
{
    [HttpGet("Emprestimos")]
    [EndpointSummary("Lista os empréstimos, paginados (mais recentes primeiro).")]
    [EndpointDescription("Filtros: bemId, membroId, abertos (true: só abertos; false: só encerrados) e atrasados (true: abertos com previsão anterior " +
        "a hoje no fuso do clube; false: os demais). atrasado e diasAtraso são calculados na consulta, sem atualização manual.")]
    [ProducesResponseType<PaginaDto<EmprestimoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginaDto<EmprestimoDto>>> Listar([FromQuery] Guid? bemId = null, [FromQuery] Guid? membroId = null,
        [FromQuery] bool? abertos = null, [FromQuery] bool? atrasados = null, [FromQuery] int page = 1,
        [FromQuery] int pageSize = RegrasAlmoxarifado.PageSizePadrao, CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarEmprestimosQuery
        {
            BemId = bemId, MembroId = membroId, Abertos = abertos, Atrasados = atrasados, Page = page, PageSize = pageSize,
        }, cancellationToken));

    [HttpGet("Emprestimos/{id:guid}", Name = nameof(ObterEmprestimo))]
    [EndpointSummary("Consulta um empréstimo.")]
    [ProducesResponseType<EmprestimoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EmprestimoDto>> ObterEmprestimo(Guid id, CancellationToken cancellationToken) =>
        await Mediator.Send(new ObterEmprestimoQuery(id), cancellationToken) is { } dto ? Ok(dto) : NaoEncontrado("Empréstimo não encontrado.");

    [HttpPost("Emprestimos/{id:guid}/Devolucao")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Registra a devolução: data efetiva, conservação no retorno e observações.")]
    [EndpointDescription("dataDevolucao (entre a retirada e hoje) e conservacaoRetorno obrigatórios. O bem volta a Disponivel ou, com " +
        "encaminharParaManutencao = true e motivoManutencao, segue para EmManutencao na mesma operação. 409 se o empréstimo já foi encerrado " +
        "(devolução repetida).")]
    [ProducesResponseType<EmprestimoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmprestimoDto>> Devolver(Guid id, [FromBody] DevolverEmprestimoRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    [HttpPost("Emprestimos/{id:guid}/PerdaFurto")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Encerra um empréstimo aberto por perda ou furto e baixa o bem com o mesmo motivo.")]
    [EndpointDescription("data (entre a retirada e hoje), motivo (Perda ou Furto) e justificativa obrigatórios. 409 se o empréstimo já foi encerrado.")]
    [ProducesResponseType<EmprestimoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmprestimoDto>> PerdaFurto(Guid id, [FromBody] RegistrarPerdaFurtoRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    [HttpPost("Manutencoes/{id:guid}/Saida")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Registra a saída da manutenção: o bem volta a Disponivel com a conservação informada.")]
    [EndpointDescription("dataSaida (entre a entrada e hoje) e conservacaoSaida obrigatórios. 409 se a manutenção já foi encerrada.")]
    [ProducesResponseType<ManutencaoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ManutencaoDto>> SaidaManutencao(Guid id, [FromBody] SaidaManutencaoRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
}

/// <summary>Inventários patrimoniais (fotografia imutável) e registro posterior da votação da Comissão.</summary>
[Route("api/Almoxarifado/Inventarios")]
public sealed class AlmoxarifadoInventariosController(ISender mediator) : AlmoxarifadoControllerBase(mediator)
{
    [HttpGet]
    [EndpointSummary("Lista os inventários fechados (mais recentes primeiro), sem os itens.")]
    [ProducesResponseType<PaginaDto<InventarioResumoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginaDto<InventarioResumoDto>>> Listar([FromQuery] int page = 1,
        [FromQuery] int pageSize = RegrasAlmoxarifado.PageSizePadrao, CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarInventariosQuery(page, pageSize), cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(ObterInventario))]
    [EndpointSummary("Consulta um inventário com a fotografia dos bens e valores daquela conferência.")]
    [ProducesResponseType<InventarioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InventarioDto>> ObterInventario(Guid id, CancellationToken cancellationToken) =>
        await Mediator.Send(new ObterInventarioQuery(id), cancellationToken) is { } dto ? Ok(dto) : NaoEncontrado("Inventário não encontrado.");

    [HttpPost]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Fecha um inventário: copia os bens não excluídos e não baixados, com totais e valor estimado patrimonial.")]
    [EndpointDescription("dataInventario (não futura) e responsavelId (membro ativo) obrigatórios; observacoes opcional. Os itens são imutáveis: " +
        "alterações posteriores nos bens não mudam o inventário. Começa sem votação (votado = false). Não há limite monetário nem aprovação " +
        "automática para bem de maior valor: isso depende da direção.")]
    [ProducesResponseType<InventarioDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Fechar([FromBody] FecharInventarioRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request), cancellationToken);
        return CreatedAtRoute(nameof(ObterInventario), new { id = dto.Inventario.Id }, dto);
    }

    [HttpPost("{id:guid}/Votacao")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Registra a data da votação e a referência da ata da Comissão da Igreja (uma única vez).")]
    [EndpointDescription("dataVotacao (entre a data do inventário e hoje), referenciaAta (1–100) e versao obrigatórios. O inventário só aparece como " +
        "votado depois deste registro; o sistema registra o ato, não substitui a votação. 409 se já registrada ou versão desatualizada.")]
    [ProducesResponseType<InventarioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InventarioDto>> RegistrarVotacao(Guid id, [FromBody] RegistrarVotacaoInventarioRequest request,
        CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
}

/// <summary>CRUD da lista de desejos do almoxarifado, atendimentos e estornos.</summary>
[Route("api/Almoxarifado/Desejos")]
public sealed class AlmoxarifadoDesejosController(ISender mediator) : AlmoxarifadoControllerBase(mediator)
{
    [HttpGet]
    [EndpointSummary("Lista as necessidades, paginadas: abertas primeiro, maior urgência e, no empate, a criação mais antiga.")]
    [EndpointDescription("Filtros: busca (parte do nome), urgencia (PoucoUrgenteMasNecessario, Urgente, MuitoUrgente), situacao (Pendente, " +
        "ParcialmenteAtendida, Atendida, Cancelada), categoria (exata), tipoItem (Patrimonial, Consumivel), somenteAbertas, incluirExcluidos. " +
        "atendimentos vem vazio na listagem (consulte o detalhe).")]
    [ProducesResponseType<PaginaDto<DesejoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginaDto<DesejoDto>>> Listar(
        [FromQuery] string? busca = null,
        [FromQuery] NivelUrgenciaAlmoxarifado? urgencia = null,
        [FromQuery] SituacaoDesejo? situacao = null,
        [FromQuery] string? categoria = null,
        [FromQuery] TipoItemDesejo? tipoItem = null,
        [FromQuery] bool somenteAbertas = false,
        [FromQuery] bool incluirExcluidos = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = RegrasAlmoxarifado.PageSizePadrao,
        CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarDesejosQuery
        {
            Busca = busca, Urgencia = urgencia, Situacao = situacao, Categoria = categoria, TipoItem = tipoItem, SomenteAbertas = somenteAbertas,
            IncluirExcluidos = incluirExcluidos, Page = page, PageSize = pageSize,
        }, cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(ObterDesejo))]
    [EndpointSummary("Consulta uma necessidade com os atendimentos (inclusive estornados) e os bens recebidos.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesejoDto>> ObterDesejo(Guid id, CancellationToken cancellationToken) =>
        await Mediator.Send(new ObterDesejoQuery(id), cancellationToken) is { } dto ? Ok(dto) : NaoEncontrado("Necessidade não encontrada.");

    [HttpGet("{id:guid}/Historico")]
    [EndpointSummary("Histórico da necessidade, dos atendimentos e dos vínculos de bens (filtros de, ate e tipo como no histórico de bens).")]
    [ProducesResponseType<IReadOnlyList<HistoricoAlmoxarifadoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<HistoricoAlmoxarifadoDto>>> Historico(Guid id, [FromQuery] DateOnly? de = null,
        [FromQuery] DateOnly? ate = null, [FromQuery] TipoMovimentacaoAlmoxarifado? tipo = null, CancellationToken cancellationToken = default) =>
        await Mediator.Send(new HistoricoQuery(id, true, de, ate, tipo), cancellationToken) is { } historico
            ? Ok(historico) : NaoEncontrado("Necessidade não encontrada.");

    [HttpPost]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Cadastra uma necessidade de aquisição (não cria patrimônio, despesa, pagamento nem reserva de dinheiro).")]
    [EndpointDescription("nome, descricao, justificativa (uso previsto), tipoItem (Patrimonial ou Consumivel), quantidadeDesejada (1–10000) e urgencia " +
        "obrigatórios. categoria, custoUnitarioEstimado (>= 0; ausência não é custo zero), dataNecessidade (independente da urgência, que nunca muda " +
        "sozinha), linkReferencia (URL http/https) e observacoes opcionais. O solicitante é o usuário autenticado.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Cadastrar([FromBody] CadastrarDesejoRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request), cancellationToken);
        return CreatedAtRoute(nameof(ObterDesejo), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Atualiza uma necessidade (estado anterior auditado).")]
    [EndpointDescription("Mesmas regras do cadastro, com versao. quantidadeDesejada pode aumentar, mas não ficar abaixo da atendida (409); tipoItem não " +
        "muda depois de atendimentos (409). A situação é recalculada pelas quantidades. 409: cancelada, excluída ou versão desatualizada.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DesejoDto>> Atualizar(Guid id, [FromBody] AtualizarDesejoRequest request, CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    [HttpPost("{id:guid}/Cancelar")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Cancela a necessidade (motivo obrigatório), preservando os atendimentos anteriores.")]
    [EndpointDescription("Corpo: justificativa (motivo) e versao. Impede novos atendimentos. 409 se já atendida, cancelada ou excluída.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DesejoDto>> Cancelar(Guid id, [FromBody] EncerrarDesejoRequest request, CancellationToken cancellationToken)
    {
        request.Excluir = false;
        return Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Exclui logicamente uma necessidade (justificativa obrigatória), sem perder atendimentos nem histórico.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DesejoDto>> Excluir(Guid id, [FromBody] EncerrarDesejoRequest request, CancellationToken cancellationToken)
    {
        request.Excluir = true;
        return Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
    }

    [HttpPost("{id:guid}/Atendimentos")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Registra um atendimento (recebimento por compra ou doação), parcial ou total.")]
    [EndpointDescription("Exige o header Idempotency-Key (UUID). quantidade (até a pendente), data (não futura), responsavelId (membro ativo) e origem " +
        "(Compra ou Doacao) obrigatórios. Item patrimonial: bensNovos (criados no recebimento, com origem e data de aquisição do atendimento) e/ou " +
        "bensExistentes (já cadastrados, sem outro vínculo ativo) somando exatamente a quantidade. Consumível: sem bens (não gera patrimônio nem " +
        "estoque). A situação passa a ParcialmenteAtendida ou Atendida pelas quantidades. 201: criado. 200: reenvio idempotente (mesma chave e dados) " +
        "com o atendimento original, sem duplicar recebimentos nem bens. 409: chave com dados diferentes, quantidade acima da pendente, necessidade " +
        "cancelada/excluída/atendida, bem já vinculado, excluído ou baixado.")]
    [ProducesResponseType<RegistrarAtendimentoResultado>(StatusCodes.Status201Created)]
    [ProducesResponseType<RegistrarAtendimentoResultado>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegistrarAtendimento(Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarAtendimentoRequest request, CancellationToken cancellationToken)
    {
        request.IdempotencyKey = idempotencyKey;
        var resultado = await Mediator.Send(Preencher(request, id), cancellationToken);
        return resultado.Criado ? CreatedAtRoute(nameof(ObterDesejo), new { id }, resultado) : Ok(resultado);
    }

    [HttpPost("{id:guid}/Atendimentos/{atendimentoId:guid}/Estorno")]
    [Authorize(Policy = Policies.GestaoAlmoxarifado)]
    [EndpointSummary("Estorna um atendimento indevido (justificativa obrigatória; operação auditada).")]
    [EndpointDescription("Desfaz os vínculos, exclui logicamente os bens que o atendimento criou e recalcula a quantidade atendida. 409 se já estornado " +
        "ou se algum bem criado por ele já teve empréstimo, manutenção ou baixa. Bens existentes vinculados apenas ficam livres.")]
    [ProducesResponseType<DesejoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DesejoDto>> EstornarAtendimento(Guid id, Guid atendimentoId, [FromBody] EstornarAtendimentoRequest request,
        CancellationToken cancellationToken)
    {
        request.AtendimentoId = atendimentoId;
        return Ok(await Mediator.Send(Preencher(request, id), cancellationToken));
    }
}

/// <summary>Enums, relatórios e designação do responsável pelo almoxarifado.</summary>
[Route("api/Almoxarifado")]
public sealed class AlmoxarifadoController(ISender mediator) : AlmoxarifadoControllerBase(mediator)
{
    private static readonly EnumsAlmoxarifadoDto Enums = new(
        RegrasAlmoxarifado.Itens<EstadoConservacao>(), RegrasAlmoxarifado.Itens<SituacaoBem>(), RegrasAlmoxarifado.Itens<OrigemBem>(),
        RegrasAlmoxarifado.Itens<MotivoBaixa>(), RegrasAlmoxarifado.Itens<EncerramentoEmprestimo>(), RegrasAlmoxarifado.Itens<NivelUrgenciaAlmoxarifado>(),
        RegrasAlmoxarifado.Itens<SituacaoDesejo>(), RegrasAlmoxarifado.Itens<TipoItemDesejo>(), RegrasAlmoxarifado.Itens<OrigemAtendimento>(),
        RegrasAlmoxarifado.Itens<TipoMovimentacaoAlmoxarifado>());

    [HttpGet("Enums")]
    [EndpointSummary("Valores, códigos e descrições dos enums do almoxarifado (para seletores do frontend).")]
    [EndpointDescription("valor é o número estável gravado no banco; codigo é o nome aceito nos corpos e filtros (o número também é aceito); descricao " +
        "é o rótulo de exibição. Valores fora do enum são recusados com 400.")]
    [ProducesResponseType<EnumsAlmoxarifadoDto>(StatusCodes.Status200OK)]
    public ActionResult<EnumsAlmoxarifadoDto> ObterEnums() => Ok(Enums);

    [HttpGet("Relatorios/Patrimonio")]
    [EndpointSummary("Totais por situação e valor estimado patrimonial (soma das estimativas dos bens com valor comercial não baixados).")]
    [EndpointDescription("Aceita os filtros da listagem de bens. valorEstimadoPatrimonial é uma estimativa, não um saldo financeiro: não altera o " +
        "dashboard nem gera lançamentos.")]
    [ProducesResponseType<RelatorioPatrimonioDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RelatorioPatrimonioDto>> RelatorioPatrimonio([FromQuery] string? busca = null, [FromQuery] string? categoria = null,
        [FromQuery] EstadoConservacao? conservacao = null, [FromQuery] SituacaoBem? situacao = null, [FromQuery] string? localizacao = null,
        [FromQuery] bool? possuiValorComercial = null, [FromQuery] bool incluirExcluidos = false, CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new RelatorioPatrimonioQuery(Filtros(busca, categoria, conservacao, situacao, localizacao, possuiValorComercial,
            incluirExcluidos)), cancellationToken));

    [HttpGet("Relatorios/Patrimonio/Csv")]
    [EndpointSummary("Exporta o relatório patrimonial em CSV (tombo, nome, descrição, conservação, localização, situação, aquisição e estimativa).")]
    [EndpointDescription("Mesmos filtros do relatório. CSV com separador ';', decimais com vírgula, datas yyyy-MM-dd e UTF-8 com BOM; campos que " +
        "começam com = + - @ recebem apóstrofo (proteção contra fórmulas). Acervo sem data: 'Desconhecida'.")]
    [Produces("text/csv")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportarPatrimonio([FromQuery] string? busca = null, [FromQuery] string? categoria = null,
        [FromQuery] EstadoConservacao? conservacao = null, [FromQuery] SituacaoBem? situacao = null, [FromQuery] string? localizacao = null,
        [FromQuery] bool? possuiValorComercial = null, [FromQuery] bool incluirExcluidos = false, CancellationToken cancellationToken = default) =>
        Csv(await Mediator.Send(new ExportarPatrimonioQuery(Filtros(busca, categoria, conservacao, situacao, localizacao, possuiValorComercial,
            incluirExcluidos)), cancellationToken));

    [HttpGet("Relatorios/Emprestimos")]
    [EndpointSummary("Empréstimos abertos (ou só os atrasados, com atrasados=true), da previsão mais antiga para a mais recente.")]
    [ProducesResponseType<IReadOnlyList<EmprestimoDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EmprestimoDto>>> RelatorioEmprestimos([FromQuery] bool atrasados = false,
        CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new RelatorioEmprestimosQuery(atrasados), cancellationToken));

    [HttpGet("Relatorios/Emprestimos/Csv")]
    [EndpointSummary("Exporta em CSV os empréstimos abertos (ou só os atrasados).")]
    [Produces("text/csv")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> ExportarEmprestimos([FromQuery] bool atrasados = false, CancellationToken cancellationToken = default) =>
        Csv(await Mediator.Send(new ExportarEmprestimosQuery(atrasados), cancellationToken));

    [HttpGet("Relatorios/Desejos")]
    [EndpointSummary("Necessidades abertas por urgência: quantidade, quantidade pendente e custo estimado pendente (quando disponível).")]
    [EndpointDescription("custoPendenteEstimado soma só as necessidades com custo unitário estimado; necessidadesSemCustoEstimado indica quantas não " +
        "entraram na soma (custo desconhecido não é zero).")]
    [ProducesResponseType<IReadOnlyList<DesejosPorUrgenciaDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DesejosPorUrgenciaDto>>> RelatorioDesejos(CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(new RelatorioDesejosQuery(), cancellationToken));

    [HttpGet("Responsaveis")]
    [EndpointSummary("Responsáveis designados pelo almoxarifado (vigentes; incluirRevogados=true traz também os revogados).")]
    [ProducesResponseType<IReadOnlyList<ResponsavelAlmoxarifadoDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ResponsavelAlmoxarifadoDto>>> ListarResponsaveis([FromQuery] bool incluirRevogados = false,
        CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarResponsaveisQuery(incluirRevogados), cancellationToken));

    [HttpPost("Responsaveis")]
    [Authorize(Policy = Policies.DesignacaoAlmoxarifado)]
    [EndpointSummary("Designa um membro ativo como responsável pelo almoxarifado do clube (ADM, DIR, DIRA, SEC).")]
    [EndpointDescription("membroId (membro ativo) e dataInicio (não futura) obrigatórios; observacoes opcional. O membro mantém o cargo e passa a " +
        "gerenciar o almoxarifado na requisição seguinte. 409 se já houver designação vigente do mesmo membro.")]
    [ProducesResponseType<ResponsavelAlmoxarifadoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Designar([FromBody] DesignarResponsavelRequest request, CancellationToken cancellationToken)
    {
        var dto = await Mediator.Send(Preencher(request), cancellationToken);
        return Created($"/api/Almoxarifado/Responsaveis/{dto.Id}", dto);
    }

    [HttpPost("Responsaveis/{id:guid}/Revogar")]
    [Authorize(Policy = Policies.DesignacaoAlmoxarifado)]
    [EndpointSummary("Revoga a designação (motivo obrigatório); o acesso de gestão deixa de valer na requisição seguinte.")]
    [ProducesResponseType<ResponsavelAlmoxarifadoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResponsavelAlmoxarifadoDto>> Revogar(Guid id, [FromBody] RevogarResponsavelRequest request,
        CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(Preencher(request, id), cancellationToken));

    private static ListarBensQuery Filtros(string? busca, string? categoria, EstadoConservacao? conservacao, SituacaoBem? situacao, string? localizacao,
        bool? possuiValorComercial, bool incluirExcluidos) => new()
    {
        Busca = busca, Categoria = categoria, Conservacao = conservacao, Situacao = situacao, Localizacao = localizacao,
        PossuiValorComercial = possuiValorComercial, IncluirExcluidos = incluirExcluidos,
    };
}
