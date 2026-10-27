using System.Reflection;
using Almirante.Api.Controllers;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #160: regras puras e guardas exercitados só com dados fora do caminho comum (metadados sem XML, ação de histórico desconhecida,
/// pessoa ausente, revisão sem requisitos, tabela sem trava prevista, sessão que deixou de valer).
/// </summary>
public sealed class RegrasPurasBordasTests
{
    private static OperationFilterContext Contexto(Type controller, params object[] metadados)
    {
        var acao = new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo(), EndpointMetadata = [.. metadados] };
        return new OperationFilterContext(new ApiDescription { ActionDescriptor = acao, HttpMethod = "POST" }, null!, new SchemaRepository(), null!, null!);
    }

    public static TheoryData<string> FiltrosOpenApi => ["eventos", "almoxarifado"];

    [Theory]
    [MemberData(nameof(FiltrosOpenApi))]
    public void FiltroOpenApi_SemResumoNoXml_UsaOsMetadadosDoEndpoint_EToleraAAusenciaDeles(string caso)
    {
        (IOperationFilter Filtro, Type Controller) alvo = caso == "eventos"
            ? (new EventosOperationFilter(), typeof(EventosController))
            : (new AlmoxarifadoOperationFilter(), typeof(AlmoxarifadoBensController));

        var comMetadados = new OpenApiOperation();
        alvo.Filtro.Apply(comMetadados, Contexto(alvo.Controller, new EndpointSummaryAttribute("resumo"), new EndpointDescriptionAttribute("descrição")));
        Assert.Equal(("resumo", "descrição"), (comMetadados.Summary, comMetadados.Description));

        var semMetadados = new OpenApiOperation();
        alvo.Filtro.Apply(semMetadados, Contexto(alvo.Controller));
        Assert.Null(semMetadados.Summary);
        Assert.Null(semMetadados.Description);
    }

    [Fact]
    public void NotificacoesDoEvento_SoParaLancamentosComMembroDesteEvento()
    {
        var evento = new Evento { Id = Guid.NewGuid(), Local = "Parque", DataEvento = new DateOnly(2026, 11, 7), ValorPorMembro = 30m };
        var membro = Guid.NewGuid();
        Lancamento Lancamento(Guid? membroId, Guid? eventoId) =>
            new() { Id = Guid.NewGuid(), MembroId = membroId, EventoId = eventoId, Categoria = CategoriaLancamento.Evento, Valor = 30m };

        var notificacoes = NotificacoesFabrica.ParaEvento(evento,
            [Lancamento(membro, evento.Id), Lancamento(null, evento.Id), Lancamento(Guid.NewGuid(), Guid.NewGuid()), Lancamento(Guid.NewGuid(), null)],
            DateTime.UtcNow);

        Assert.Equal(membro, Assert.Single(notificacoes).DestinatarioId);
    }

    [Fact]
    public void Progresso_RevisaoSemRequisitosObrigatorios_FicaEmZeroPorCento()
    {
        var progresso = ProgressoFormacao.Calcular([], new Dictionary<Guid, ProgressoRequisito>(),
            new Dictionary<Guid, IReadOnlyList<EvidenciaAvaliacao>>(), null);

        Assert.Equal((0, 0, 0), (progresso.UnidadesObrigatorias, progresso.UnidadesSatisfeitas, progresso.Percentual));
    }

    [Fact]
    public void DescricaoDeEnum_ValorForaDoEnum_UsaONumero()
    {
        Assert.Equal("99", RegrasAlmoxarifado.Descricao((SituacaoBem)99));
        Assert.Equal("Monday", RegrasAlmoxarifado.Descricao(DayOfWeek.Monday)); // membro sem [Description]
        Assert.Null(RegrasAlmoxarifado.Descricao((SituacaoBem?)null));
    }

    [Fact]
    public void ExigenciaDeEspecialidade_ResumeAEspecialidadeSoQuandoElaEstaNoCatalogo()
    {
        var conhecida = new Especialidade { Id = Guid.NewGuid(), Nome = "Nós e Amarras", Area = "Atividades Recreativas", FonteOficial = "manual" };
        var catalogo = new Dictionary<Guid, Especialidade> { [conhecida.Id] = conhecida };
        Requisito Requisito(int? quantidade, Guid? especialidadeId) => new()
        {
            Codigo = "1", Descricao = "Ter especialidades", EspecialidadesExigidas = quantidade, EspecialidadeExigidaId = especialidadeId,
            AreaEspecialidadeExigida = "Atividades Recreativas",
        };

        Assert.Null(FormacaoMapping.Exigencia(Requisito(null, conhecida.Id), catalogo));
        Assert.Equal(conhecida.Id, FormacaoMapping.Exigencia(Requisito(1, conhecida.Id), catalogo)!.Especialidade!.Id);
        Assert.Null(FormacaoMapping.Exigencia(Requisito(1, Guid.NewGuid()), catalogo)!.Especialidade);
        var porArea = FormacaoMapping.Exigencia(Requisito(2, null), catalogo)!;
        Assert.Equal((2, null, "Atividades Recreativas"), (porArea.Quantidade, porArea.Especialidade, porArea.Area));
    }

    [Fact]
    public void HistoricoDoAlmoxarifado_AcaoDesconhecidaFicaSemTipo_EPessoaAusenteFicaSemNome()
    {
        var linha = new HistoricoAlmoxarifado
        {
            Id = Guid.NewGuid(), Tabela = "patrimonio_bens", RegistroId = Guid.NewGuid(), Acao = null, EstadoNovoJson = "{}",
            IpResponsavel = "127.0.0.1", UsuarioResponsavelId = Guid.NewGuid(), OcorridoEmUtc = DateTime.UtcNow,
        };
        var historico = Assert.Single(AlmoxarifadoBanco.Historico([(linha, "Responsável")]));
        Assert.Null(historico.Tipo);
        Assert.Null(historico.TipoDescricao);

        var ausente = Guid.NewGuid();
        Assert.Equal(new PessoaResumoDto(ausente, string.Empty), AlmoxarifadoBanco.Registro(new Dictionary<Guid, PessoaResumoDto>(), ausente, DateTime.UtcNow).Por);
    }

    [Fact]
    public async Task TravaDeRegistro_EmTabelaSemTravaPrevista_ETransacaoDeFormacaoSemSqlServer_SaoErrosDeProgramacao()
    {
        await using var factory = new AlmiranteApiFactory();
        using var escopo = factory.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AlmiranteDbContext>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AlmoxarifadoBanco.BloquearAsync(db, "usuarios", Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FormacaoBanco.BloquearAsync(db, "usuarios", Guid.NewGuid(), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => FormacaoBanco.ExecutarAsync(db, NullLogger.Instance, Guid.NewGuid(), "127.0.0.1",
            "Teste", null, _ => Task.FromResult(1), ApiProblemException.Conflict("Conflito."), default));
    }

    [Fact]
    public void Email_AusenteNaoEValido_ELongoDemaisNaoCabeNaColuna()
    {
        Assert.False(UsuariosService.EmailValido(null));
        Assert.False(UsuariosService.EmailArmazenavel(new string('A', UsuariosService.EmailMaximo - 8) + "@EX.COM.BR"));
        Assert.True(UsuariosService.EmailArmazenavel("A@EX.COM"));
    }

    [Fact]
    public async Task RecuperacoesPendentes_SolicitanteInexistenteOuInativo_TemAcessoNegado()
    {
        await using var factory = new AlmiranteApiFactory();
        using var escopo = factory.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var admin = await db.Usuarios.SingleAsync(u => u.Email == AlmiranteApiFactory.AdminEmail);
        var recuperacoes = escopo.ServiceProvider.GetRequiredService<RecuperacaoSenhaService>();
        async Task Negado(Guid solicitante) => Assert.Equal(StatusCodes.Status403Forbidden, (await Assert.ThrowsAsync<ApiProblemException>(() =>
            recuperacoes.ListarPendentesAsync(new ListarRecuperacoesSenhaPendentesQuery(solicitante, 20, null), default))).Status);

        await Negado(Guid.NewGuid());
        admin.Ativo = false;
        await db.SaveChangesAsync();
        await Negado(admin.Id);
    }

    [Fact]
    public async Task SolicitanteDeSenha_ComSessaoQueDeixouDeValer_Recebe401()
    {
        await using var factory = new AlmiranteApiFactory();
        using var escopo = factory.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var adminId = await db.Usuarios.Where(u => u.Email == AlmiranteApiFactory.AdminEmail).Select(u => u.Id).SingleAsync();
        var contas = escopo.ServiceProvider.GetRequiredService<ContasSenha>();

        async Task Recusado(Guid usuarioId) => Assert.Equal(StatusCodes.Status401Unauthorized, (await Assert.ThrowsAsync<ApiProblemException>(() =>
            contas.CarregarSolicitanteAsync(new ContextoSolicitanteSenha(usuarioId, Guid.NewGuid(), "127.0.0.1", "trace"), DateTime.UtcNow, default))).Status);

        await Recusado(adminId);
        await Recusado(Guid.NewGuid());
        (await db.Usuarios.SingleAsync(u => u.Id == adminId)).Ativo = false;
        await db.SaveChangesAsync();
        await Recusado(adminId);
    }
}
