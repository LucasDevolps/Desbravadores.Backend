using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

/// <summary>
/// Testes unitários (sem host HTTP e sem SQL Server): usuário excluído logicamente (Ativo = false) não é cobrado, não recebe
/// notificação e não mantém sessão. Regressão do E2E de 2026-10-21, em que um membro inativo recebia lançamentos e avisos.
/// </summary>
public sealed class MembrosAtivosUnitTests
{
    private static AlmiranteDbContext NovoDb() =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseInMemoryDatabase($"membros-ativos-{Guid.NewGuid():N}").Options);

    private static Usuario Usuario(Cargo cargo, bool ativo)
    {
        var email = $"membro-{Guid.NewGuid():N}@local.dev";
        return new Usuario
        {
            Id = Guid.NewGuid(), Nome = ativo ? "Ativo" : "Inativo", Email = email, EmailNormalizado = email.ToUpperInvariant(),
            SenhaHash = string.Empty, CargoId = cargo.Id, Cargo = cargo, Ativo = ativo,
        };
    }

    private static async Task<(AlmiranteDbContext Db, Usuario Ativo, Usuario Inativo)> CenarioAsync()
    {
        var db = NovoDb();
        var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "DS", CriadoPor = "teste", Role = Roles.Desbravador };
        var ativo = Usuario(cargo, ativo: true);
        var inativo = Usuario(cargo, ativo: false);
        db.Cargos.Add(cargo);
        db.Usuarios.AddRange(ativo, inativo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, ativo, inativo);
    }

    private static LancamentosService Servico(AlmiranteDbContext db)
    {
        var sinal = new SinalAvisosNotificacoes();
        return new(db, TimeProvider.System, new LancamentoGeralService(db, TimeProvider.System, sinal),
            new LancamentoExclusaoService(db, NullLogger<LancamentoExclusaoService>.Instance), sinal);
    }

    private static RegistrarLancamentoRequest Request(Guid? membroId, bool todos = false, string? chave = null) => new()
    {
        MembroId = membroId, Finalidade = LancamentoFinalidades.Mensalidade, Descricao = "Outubro", Categoria = CategoriaLancamento.Clube,
        TipoFluxo = TipoFluxoLancamento.Entrada, Valor = 50m, Vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5),
        AplicarATodosOsMembros = todos, IdempotencyKey = chave, UsuarioSolicitanteId = Guid.NewGuid(),
    };

    [Fact]
    public async Task RegistrarIndividual_MembroInativo_MembroNaoEncontrado_SemLancamentoNemNotificacao()
    {
        var (db, _, inativo) = await CenarioAsync();
        await using var _db = db;

        var resultado = await Servico(db).RegistrarAsync(Request(inativo.Id), CancellationToken.None);

        Assert.Equal(RegistrarLancamentoOutcome.MembroNaoEncontrado, resultado.Outcome);
        Assert.Null(resultado.Lancamento);
        Assert.Empty(await db.Lancamentos.ToListAsync());
        Assert.Empty(await db.Notificacoes.ToListAsync());
    }

    [Fact]
    public async Task RegistrarIndividual_MembroInativoComIdempotencyKey_NaoGravaOperacao()
    {
        var (db, _, inativo) = await CenarioAsync();
        await using var _db = db;

        var resultado = await Servico(db).RegistrarAsync(Request(inativo.Id, chave: Guid.NewGuid().ToString()), CancellationToken.None);

        Assert.Equal(RegistrarLancamentoOutcome.MembroNaoEncontrado, resultado.Outcome);
        Assert.Empty(await db.LancamentosOperacoes.ToListAsync());
    }

    [Fact]
    public async Task RegistrarIndividual_MembroAtivo_CriaLancamentoENotificacao()
    {
        var (db, ativo, _) = await CenarioAsync();
        await using var _db = db;

        var resultado = await Servico(db).RegistrarAsync(Request(ativo.Id), CancellationToken.None);

        Assert.Equal(RegistrarLancamentoOutcome.LancamentoUnicoCriado, resultado.Outcome);
        Assert.Equal(ativo.Id, Assert.Single(await db.Lancamentos.ToListAsync()).MembroId);
        Assert.Equal(ativo.Id, Assert.Single(await db.Notificacoes.ToListAsync()).DestinatarioId);
    }

    [Fact]
    public async Task RegistrarGeral_SoMembrosAtivos_RecebemLancamentoENotificacao()
    {
        var (db, ativo, inativo) = await CenarioAsync();
        await using var _db = db;

        var resultado = await Servico(db).RegistrarAsync(Request(null, todos: true, chave: Guid.NewGuid().ToString()), CancellationToken.None);

        Assert.Equal(RegistrarLancamentoOutcome.GeralCriado, resultado.Outcome);
        Assert.Equal(1, resultado.Geral!.UsuariosProcessados);
        Assert.Equal(1, resultado.Geral.LancamentosCriados);
        var lancamentos = await db.Lancamentos.ToListAsync();
        Assert.Equal(ativo.Id, Assert.Single(lancamentos).MembroId);
        Assert.DoesNotContain(lancamentos, l => l.MembroId == inativo.Id);
        Assert.Equal(ativo.Id, Assert.Single(await db.Notificacoes.ToListAsync()).DestinatarioId);
    }

    [Fact]
    public async Task RegistrarGeral_TodosInativos_NenhumLancamento()
    {
        var (db, ativo, _) = await CenarioAsync();
        await using var _db = db;
        (await db.Usuarios.SingleAsync(u => u.Id == ativo.Id)).Ativo = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var resultado = await Servico(db).RegistrarAsync(Request(null, todos: true, chave: Guid.NewGuid().ToString()), CancellationToken.None);

        Assert.Equal(0, resultado.Geral!.LancamentosCriados);
        Assert.Empty(await db.Lancamentos.ToListAsync());
        Assert.Empty(await db.Notificacoes.ToListAsync());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SessaoVigente_ExigeUsuarioAtivo(bool usuarioAtivo, bool esperado)
    {
        var (db, ativo, inativo) = await CenarioAsync();
        await using var _db = db;
        var usuario = usuarioAtivo ? ativo : inativo;
        var agora = DateTime.UtcNow;
        db.AuthSessions.Add(new AuthSession
        {
            Id = Guid.NewGuid(), UsuarioId = usuario.Id, CreatedAtUtc = agora, LastRenewedAtUtc = agora,
            AbsoluteExpiresAtUtc = agora.AddDays(1), SecurityVersion = usuario.SecurityVersion,
        });
        await db.SaveChangesAsync();

        Assert.Equal(esperado, await db.AuthSessions.AnyAsync(SessoesAutenticacao.Vigente(agora)));
    }
}
