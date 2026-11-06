using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Usuarios")]
[Authorize(Policy = Policies.GestaoCadastros)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class UsuariosController(UsuariosService usuariosService, ISender mediator, LimitadorEnvioFotos limitador,
    IOptions<MediaOptions> media, ILogger<UsuariosController> logger, LimitadorTentativasSenha limitadorSenha)
    : FotoUsuarioControllerBase(mediator, limitador, media, logger)
{
    private const string DescricaoMatriz = "Matriz explícita (cargos relidos do banco): Administrador e Diretor → Secretário, Tesoureiro e " +
        "Desbravador; Secretário → Desbravador; nenhum outro par (Diretor Associado, Tesoureiro e Desbravador não atendem terceiros; " +
        "Administrador, Diretor e Diretor Associado nunca são alvo). Ter acesso ao cadastro de usuários não basta.";

    [HttpPost("{usuarioId:guid}/alterar-senha")]
    [Authorize(Policy = Policies.AlteracaoSenhaTerceiros)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Altera a senha de outro usuário, confirmando a senha do responsável logado (a senha antiga do afetado nunca é pedida).")]
    [EndpointDescription(DescricaoMatriz + " Corpo: senhaAtualSolicitante (a senha de quem está logado, verificada no servidor), novaSenha e " +
        "confirmacaoNovaSenha (igualdade exata), com a mesma política da troca própria, e metodoVerificacao opcional (AtendimentoPresencial " +
        "ou ProcedimentoIndependenteEstabelecido), registrado no pedido de recuperação aberto do usuário, que esta troca conclui. O afetado " +
        "não precisa estar logado. Para a própria senha use POST /api/Auth/alterar-senha (400 aqui). Exige X-CSRF-TOKEN. Sucesso: 204; todas " +
        "as sessões do afetado são revogadas e ele recebe uma notificação interna (consultável após o novo login); a sessão do responsável " +
        "continua válida; nenhum token é emitido e nenhum e-mail é enviado. 400 dados inválidos ou senha do responsável incorreta (nada é " +
        "alterado), 403 par fora da matriz, 404 usuário inexistente, 409 usuário inativo ou conta alterada ao mesmo tempo, 429 excesso de tentativas.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AlterarSenha(Guid usuarioId, [FromBody] AlterarSenhaUsuarioRequest request, CancellationToken cancellationToken)
    {
        var contexto = ContextoSenha();
        if (LimiteSenha(contexto) is { } recusa)
        {
            return recusa;
        }

        request.UsuarioAfetadoId = usuarioId;
        request.Contexto = contexto;
        await Mediator.Send(request, cancellationToken);
        return NoContent();
    }

    [HttpGet("recuperacao-senha/pendentes")]
    [Authorize(Policy = Policies.AlteracaoSenhaTerceiros)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [EndpointSummary("Lista os pedidos de recuperação de senha abertos que o usuário logado pode atender.")]
    [EndpointDescription(DescricaoMatriz + " Somente pedidos não expirados de usuários ativos com cargo que o solicitante atende: id, usuarioId, " +
        "nome, cargo, origem (SolicitacaoPublica ou Atendimento), status (Pendente ou Autorizada), datas, quantidade de pedidos repetidos e " +
        "validade da autorização emitida. Nunca traz token, IP ou contato. pageSize de 1 a 50 (padrão 20); paginação por cursor " +
        "(proximoCursor). Antes de atender, confirme a identidade pessoalmente ou pelo procedimento independente do clube: conferir CPF, " +
        "nascimento, telefone ou e-mail não comprova identidade.")]
    [ProducesResponseType<RecuperacoesSenhaPendentesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListarRecuperacoesPendentes(
        [FromQuery] int pageSize = NotificacoesService.PageSizePadrao,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Ok(await Mediator.Send(new ListarRecuperacoesSenhaPendentesQuery(User.UsuarioId(), pageSize, cursor), cancellationToken));

    [HttpPost("{usuarioId:guid}/recuperacao-senha/autorizacao")]
    [Authorize(Policy = Policies.AlteracaoSenhaTerceiros)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Emite uma autorização de recuperação de uso único para o titular escolher a nova senha sem login.")]
    [EndpointDescription(DescricaoMatriz + " Só depois de confirmar a identidade do titular pessoalmente ou por procedimento independente já " +
        "estabelecido. Corpo: senhaAtualSolicitante (a senha de quem está logado; reautenticação) e metodoVerificacao (AtendimentoPresencial " +
        "ou ProcedimentoIndependenteEstabelecido), registrados com o responsável, o IP e o instante. Usa o pedido aberto do titular ou cria " +
        "um de origem Atendimento, e substitui qualquer autorização anterior dele. A resposta (200, no-store) é o único lugar em que o token " +
        "aparece: entregue-o ao titular pessoalmente e reservadamente; ele vale 15 minutos, uma vez, em POST " +
        "/api/Auth/recuperacao-senha/confirmar, e não é guardado nem pode ser consultado de novo. Exige X-CSRF-TOKEN. 400 dados inválidos ou " +
        "senha do responsável incorreta, 403 par fora da matriz, 404 usuário inexistente, 409 usuário inativo ou conta alterada ao mesmo " +
        "tempo, 429 excesso de tentativas.")]
    [ProducesResponseType<AutorizacaoRecuperacaoSenhaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> EmitirAutorizacaoRecuperacao(Guid usuarioId, [FromBody] EmitirAutorizacaoRecuperacaoRequest request,
        CancellationToken cancellationToken)
    {
        var contexto = ContextoSenha();
        if (LimiteSenha(contexto) is { } recusa)
        {
            return recusa;
        }

        request.UsuarioAfetadoId = usuarioId;
        request.Contexto = contexto;
        return Ok(await Mediator.Send(request, cancellationToken));
    }

    private ContextoSolicitanteSenha ContextoSenha() =>
        new(User.UsuarioId(), User.SessaoId(), HttpContext.IpDeOrigem(), HttpContext.TraceIdSeguro());

    private ObjectResult? LimiteSenha(ContextoSolicitanteSenha contexto)
    {
        using var tentativa = limitadorSenha.RegistrarTentativa(contexto.UsuarioId, contexto.Ip);
        return tentativa.IsAcquired ? null : RespostaMuitasTentativas.Criar(Response, tentativa, "Muitas tentativas de operações de senha.",
            "Aguarde antes de tentar novamente.");
    }

    [HttpGet]
    [EndpointSummary("Lista os usuários; por padrão, somente os ativos.")]
    [EndpointDescription("includeInactive=true inclui os usuários excluídos logicamente. funcao traz o nome do cargo (ex.: \"Diretor\"). " +
        "cpf (normalizado), dataNascimento (yyyy-MM-dd), telefone, rg, tipoSanguineo, doencasAlergias, dataExpiracao e os dados do responsável vêm null quando não cadastrados; " +
        "ehCin é false em cadastros antigos; endereco é null sem endereço ou traz cep (8 dígitos), numero e complemento do usuário e os campos do ViaCEP " +
        "(logradouro, complementoCep, unidade, bairro, localidade, uf, estado, regiao, ibge, gia, ddd, siafi; vazios como null); " +
        "fotoUrl é a rota autenticada da foto (null sem foto).")]
    [ProducesResponseType<IReadOnlyList<UsuarioListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<UsuarioListItemDto>>> List(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var usuarios = await usuariosService.ListAsync(includeInactive, cancellationToken);
        return Ok(usuarios);
    }

    [HttpPost]
    [EndpointSummary("Cadastra um usuário.")]
    [EndpointDescription("E-mail único sem diferenciar maiúsculas/minúsculas (409 se já existir, inclusive entre inativos). " +
        "cpf é obrigatório: um CPF válido (11 dígitos, com ou sem pontos e hífen), único entre todos os usuários (409 se já existir). " +
        "rg é obrigatório quando ehCin é false ou omitido (400) e opcional com ehCin true (a CIN usa só o CPF). " +
        "dataNascimento (yyyy-MM-dd, não futura), telefone, tipoSanguineo e doencasAlergias são opcionais; telefone é fixo ou celular " +
        "brasileiro com DDD, gravado só com dígitos; rg tem até 20 caracteres (letras, dígitos, ponto, hífen, barra, espaço) e não é único; " +
        "tipoSanguineo é A+, A-, B+, B-, AB+, AB-, O+ ou O-; doencasAlergias é um texto livre de até 2000 caracteres (recusado acima disso, nunca truncado). " +
        "responsavelNome e responsavelEmail são obrigatórios quando dataNascimento indica menos de 18 anos na data de hoje; " +
        "responsavelCpf e responsavelTelefone seguem as mesmas regras de cpf e telefone (sem unicidade). " +
        "ehCin (true quando o documento é a Carteira de Identidade Nacional; omitido = false) e dataExpiracao (validade do documento, yyyy-MM-dd, " +
        "pode ser passada) são opcionais. endereco é opcional: { cep (8 dígitos, com ou sem hífen, obrigatório no objeto), numero " +
        "(até 20), complemento (até 100) }; a API consulta o ViaCEP e grava os dados oficiais do CEP (400 CEP mal formado ou inexistente, " +
        "503 ViaCEP indisponível, lento ou com resposta inválida — nada é gravado). " +
        "cargoId deve ser um cargo ativo. Hierarquia (403): somente um Administrador cadastra outro Administrador; Diretor, Diretor Associado, " +
        "Secretário e Tesoureiro só são atribuídos pela diretoria (ADM, DIR, DIRA). Política GestaoCadastros: ADM, DIR, DIRA, SEC.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Registrar([FromBody] RegistrarUsuarioRequest request, CancellationToken cancellationToken)
    {
        request.SolicitanteRole = User.Role();
        var usuario = await Mediator.Send(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, usuario);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Altera nome, e-mail, cargo, dados pessoais, documento, endereço e responsável de um usuário ativo (estado anterior completo auditado em _usuarios_hist).")]
    [EndpointDescription("nome, email e cargoId são obrigatórios e substituídos. cpf, dataNascimento, telefone, rg, tipoSanguineo, doencasAlergias, " +
        "ehCin, dataExpiracao, endereco e os campos do responsável omitidos mantêm o valor atual; null (ou \"\" nos textos) remove, exceto o cpf já " +
        "gravado, que não pode ser removido (400), e ehCin, que só aceita true ou false (false é gravado, não é omissão). " +
        "Com ehCin efetivo false, o rg é exigido (400) de quem já tinha RG ou era CIN e de quem envia rg ou ehCin; com ehCin true o rg é opcional; " +
        "um cadastro antigo sem RG e sem CIN que não envia esses campos continua editável. " +
        "endereco: omitido mantém; null ou cep null/\"\" remove o endereço inteiro; dentro do objeto, cep, numero e complemento omitidos mantêm o atual. " +
        "Só um CEP diferente do gravado é consultado no ViaCEP, e então todos os dados do CEP são substituídos e numero/complemento omitidos ficam vazios. " +
        "Se a consulta falhar (400 CEP inexistente, 503 ViaCEP indisponível/lento/resposta inválida), nada é alterado. " +
        "A obrigatoriedade do responsável usa a data de nascimento informada ou, se omitida, " +
        "a já gravada (400 se o menor de 18 anos ficar sem nome ou e-mail do responsável). " +
        "409 se o e-mail ou o CPF pertencer a outro usuário ou se o usuário estiver inativo; 404 se inexistente. " +
        "Hierarquia (403) vale para o cargo atual e para o novo: Usuário com cargo Administrador só por um Administrador; Diretor, Diretor Associado, Secretário ou Tesoureiro só pela diretoria (ADM, DIR, DIRA) (403). O próprio usuário altera os dados pessoais em PUT /api/Me.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUsuarioRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = HttpContext.IpDeOrigem();
        request.SolicitanteRole = User.Role();
        return Ok(await Mediator.Send(request, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Exclui logicamente (inativa) um usuário (estado anterior auditado em _usuarios_hist).")]
    [EndpointDescription("409 se o usuário estiver vinculado a eventos de hoje em diante (extensão eventos lista todos), se já estiver inativo " +
        "ou se for o próprio solicitante; 404 se inexistente. Eventos passados não impedem a exclusão. A foto, se houver, é mantida. " +
        "Usuário com cargo Administrador só por um Administrador; Diretor, Diretor Associado, Secretário ou Tesoureiro só pela diretoria (ADM, DIR, DIRA) (403).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        await Mediator.Send(new DeleteUsuarioRequest(id, usuarioId, HttpContext.IpDeOrigem(), User.Role()), cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/foto")]
    [DisableFormValueModelBinding]
    [Consumes("multipart/form-data")]
    [EndpointSummary("Envia ou substitui a foto de perfil de um usuário ativo (troca auditada em _usuarios_hist).")]
    [EndpointDescription("multipart/form-data com um único campo 'arquivo': JPEG, PNG ou WebP, com extensão, Content-Type e assinatura coincidentes. " +
        "Limites em Media:Images (padrão: até 10 MB, de 64x64 até 25 megapixels). A API decodifica, corrige a orientação, remove todos os metadados " +
        "(EXIF/GPS, XMP, IPTC, ICC) e regrava em WebP de até 1024x1024 e 1 MB; o arquivo original nunca é guardado. Foto de outro usuário segue a " +
        "hierarquia dos cargos (403; a própria foto também pode ser trocada em PUT /api/Me/foto). 400 multipart inválido, 404 usuário inexistente, 409 usuário inativo, 413 acima do limite, " +
        "415 formato ou Content-Encoding não suportado, 422 imagem inválida/fora dos limites, 429 excesso de envios, 503 armazenamento indisponível.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnviarFoto(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        return await EnviarFotoAsync(id, usuarioId, cancellationToken);
    }

    [HttpGet("{id:guid}/foto")]
    [EndpointSummary("Baixa a foto de perfil de um usuário (WebP gerado pela API).")]
    [EndpointDescription("Exige o token (Bearer): no navegador, busque com HttpClient (responseType blob) e use URL.createObjectURL; uma <img src> " +
        "comum não envia o token. ETag muda a cada troca de foto (If-None-Match -> 304); Cache-Control private, no-cache. 404 sem usuário ou sem foto.")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "image/webp")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObterFoto(Guid id, CancellationToken cancellationToken) => ObterFotoAsync(id, cancellationToken);

    [HttpDelete("{id:guid}/foto")]
    [EndpointSummary("Remove a foto de perfil de um usuário (remoção auditada em _usuarios_hist; o arquivo é apagado).")]
    [EndpointDescription("Também para usuário inativo. 404 se o usuário não existir ou não tiver foto; foto de outro usuário segue a hierarquia dos cargos (403).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExcluirFoto(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        return await ExcluirFotoAsync(id, usuarioId, cancellationToken);
    }
}
