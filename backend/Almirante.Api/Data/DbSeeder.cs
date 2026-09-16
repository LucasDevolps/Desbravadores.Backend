using Almirante.Api.Entities;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(
        AlmiranteDbContext db,
        IPasswordHasher<Usuario> passwordHasher,
        IOptions<SeedOptions> seedOptions,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.IsRelational())
        {
            await MigrateWithRetryAsync(db, cancellationToken);
        }
        else
        {
            // Test doubles (EF Core InMemory provider) do not support migrations.
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        await SeedCargosAsync(db, cancellationToken);
        await SeedAdminAsync(db, passwordHasher, seedOptions.Value, cancellationToken);
        await SeedLancamentosAsync(db, cancellationToken);
    }

    private const string CargoAdminRole = "ADM";

    // SQL error 1801 = "Database '...' already exists". Under container restarts the database
    // created by a previous run already exists (thanks to the persistent volume), but EF Core's
    // existence check can occasionally race with SQL Server still settling right after a restart
    // and attempt CREATE DATABASE again. Retrying once, now that the database is visible, makes
    // startup idempotent instead of crashing the whole process on an unhandled SqlException.
    private static async Task MigrateWithRetryAsync(AlmiranteDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1801)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
    }

    private static async Task SeedAdminAsync(
        AlmiranteDbContext db,
        IPasswordHasher<Usuario> passwordHasher,
        SeedOptions seedOptions,
        CancellationToken cancellationToken)
    {
        var emailNormalizado = seedOptions.Email.Trim().ToUpperInvariant();

        var exists = await db.Usuarios.AnyAsync(u => u.EmailNormalizado == emailNormalizado, cancellationToken);
        if (exists)
        {
            return;
        }

        var cargoAdminId = await db.Cargos
            .Where(c => c.Role == CargoAdminRole)
            .Select(c => c.Id)
            .SingleAsync(cancellationToken);

        var admin = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = seedOptions.Nome,
            Email = seedOptions.Email,
            EmailNormalizado = emailNormalizado,
            SenhaHash = string.Empty,
            CargoId = cargoAdminId,
            DataCriacao = DateTime.UtcNow,
        };

        admin.SenhaHash = passwordHasher.HashPassword(admin, seedOptions.Senha);

        db.Usuarios.Add(admin);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedLancamentosAsync(AlmiranteDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Lancamentos.AnyAsync(cancellationToken))
        {
            return;
        }

        var demo = new[]
        {
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade do clube", Categoria = LancamentoCategorias.Clube, Valor = 20m, Vencimento = new DateOnly(2026, 8, 10), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 200m, Vencimento = new DateOnly(2027, 2, 20), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento de verão", Categoria = LancamentoCategorias.Evento, Valor = 50m, Vencimento = new DateOnly(2026, 4, 16), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash anual", Categoria = LancamentoCategorias.Evento, Valor = 5m, Vencimento = new DateOnly(2026, 5, 29), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Mateus", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 25m, Vencimento = new DateOnly(2026, 8, 11), Status = LancamentoStatuses.Pendente, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Pedro", Tipo = LancamentoTipos.Campori, Descricao = "Campori estadual", Categoria = LancamentoCategorias.Evento, Valor = 180m, Vencimento = new DateOnly(2027, 3, 1), Status = LancamentoStatuses.Atrasado, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Ana", Tipo = LancamentoTipos.Doacao, Descricao = "Doação para o clube", Categoria = LancamentoCategorias.Clube, Valor = 75m, Vencimento = new DateOnly(2026, 8, 12), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Julia", Tipo = LancamentoTipos.Evento, Descricao = "Evento especial", Categoria = LancamentoCategorias.Evento, Valor = 120m, Vencimento = new DateOnly(2026, 4, 2), Status = LancamentoStatuses.Pendente, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Arthur", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 30m, Vencimento = new DateOnly(2026, 8, 15), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Beatriz", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento", Categoria = LancamentoCategorias.Evento, Valor = 90m, Vencimento = new DateOnly(2026, 5, 18), Status = LancamentoStatuses.Atrasado, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Lucas", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash", Categoria = LancamentoCategorias.Evento, Valor = 15m, Vencimento = new DateOnly(2026, 5, 21), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Rafael", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 35m, Vencimento = new DateOnly(2026, 8, 18), Status = LancamentoStatuses.Pendente, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Sofia", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 210m, Vencimento = new DateOnly(2027, 3, 8), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Marina", Tipo = LancamentoTipos.Doacao, Descricao = "Doação para eventos", Categoria = LancamentoCategorias.Clube, Valor = 50m, Vencimento = new DateOnly(2026, 8, 22), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "João", Tipo = LancamentoTipos.Evento, Descricao = "Evento especial", Categoria = LancamentoCategorias.Evento, Valor = 140m, Vencimento = new DateOnly(2026, 4, 17), Status = LancamentoStatuses.Atrasado, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Letícia", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 27m, Vencimento = new DateOnly(2026, 8, 19), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Gabriel", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento", Categoria = LancamentoCategorias.Evento, Valor = 110m, Vencimento = new DateOnly(2026, 6, 25), Status = LancamentoStatuses.Pendente, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Alice", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash", Categoria = LancamentoCategorias.Evento, Valor = 12m, Vencimento = new DateOnly(2026, 5, 30), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Thiago", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 190m, Vencimento = new DateOnly(2027, 3, 7), Status = LancamentoStatuses.Pendente, TipoFluxo = LancamentoTiposFluxo.Entrada },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Camila", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 22m, Vencimento = new DateOnly(2026, 8, 20), Status = LancamentoStatuses.Pago, TipoFluxo = LancamentoTiposFluxo.Entrada },
        };

        db.Lancamentos.AddRange(demo);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Cargos com base nas funções descritas no Manual Administrativo do Clube de
    // Desbravadores (diretoria do clube e cargos exercidos dentro das unidades).
    private static async Task SeedCargosAsync(AlmiranteDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Cargos.AnyAsync(cancellationToken))
        {
            return;
        }

        const string criadoPor = "Sistema";

        var cargos = new[]
        {
            new Cargo { Id = Guid.NewGuid(), Nome = "Administrador", Descricao = "Acesso administrativo total à plataforma, responsável pela gestão do sistema e não corresponde a um cargo de unidade do clube.", CriadoPor = criadoPor, Role = CargoAdminRole },
            new Cargo { Id = Guid.NewGuid(), Nome = "Diretor", Descricao = "Lidera todo o clube, dirige as reuniões, define metas do ano e preside as comissões.", CriadoPor = criadoPor, Role = "DIR" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Diretor Associado", Descricao = "Coordena classes, especialidades e unidades, substituindo o diretor em sua ausência.", CriadoPor = criadoPor, Role = "DIRA" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Secretário", Descricao = "Registra pontos, presenças, atas e relatórios, além de cuidar do cadastro e da comunicação do clube.", CriadoPor = criadoPor, Role = "SEC" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Tesoureiro", Descricao = "Administra as finanças do clube junto com a tesouraria da igreja.", CriadoPor = criadoPor, Role = "TES" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Capelão", Descricao = "Conduz a vida espiritual do clube, coordenando o devocional e a classe bíblica.", CriadoPor = criadoPor, Role = "CAP" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Conselheiro", Descricao = "Acompanha de perto uma unidade em todas as atividades e avalia o desenvolvimento de cada membro.", CriadoPor = criadoPor, Role = "CONS" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Conselheiro Associado", Descricao = "Auxilia o conselheiro e assume a unidade quando ele falta.", CriadoPor = criadoPor, Role = "CONSA" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Instrutor", Descricao = "Ensina uma classe específica ou especialidades, podendo ser convidado externo ao clube.", CriadoPor = criadoPor, Role = "INST" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Capitão de Unidade", Descricao = "Anima e representa a unidade, eleito por votação, e porta o bandeirim.", CriadoPor = criadoPor, Role = "CPT" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Secretário de Unidade", Descricao = "Vice-líder da unidade, cuida da ficha do Cantinho da Unidade e assume quando o capitão falta.", CriadoPor = criadoPor, Role = "SECU" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Tesoureiro de Unidade", Descricao = "Recolhe as mensalidades da unidade e presta contas.", CriadoPor = criadoPor, Role = "TESU" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Almoxarife", Descricao = "Guarda e conserva o material da unidade.", CriadoPor = criadoPor, Role = "ALM" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Padioleiro", Descricao = "Cuida da caixa de primeiros socorros da unidade.", CriadoPor = criadoPor, Role = "PAD" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Coordenador de Recreação", Descricao = "Inventa jogos e desafios para o Cantinho da Unidade.", CriadoPor = criadoPor, Role = "COREC" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Capelão de Unidade", Descricao = "Lidera os momentos espirituais e incentiva o ano bíblico na unidade.", CriadoPor = criadoPor, Role = "CAPU" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Jovem de 10 a 15 anos, membro do clube, que pode ocupar qualquer um dos cargos de unidade.", CriadoPor = criadoPor, Role = "DS" },
        };

        db.Cargos.AddRange(cargos);
        await db.SaveChangesAsync(cancellationToken);
    }
}
