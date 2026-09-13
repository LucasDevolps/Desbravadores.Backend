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

        await SeedAdminAsync(db, passwordHasher, seedOptions.Value, cancellationToken);
        await SeedLancamentosAsync(db, cancellationToken);
    }

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

        var admin = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = seedOptions.Nome,
            Email = seedOptions.Email,
            EmailNormalizado = emailNormalizado,
            SenhaHash = string.Empty,
            Roles = "Admin",
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
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade do clube", Categoria = LancamentoCategorias.Clube, Valor = 20m, Vencimento = new DateOnly(2026, 8, 10), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 200m, Vencimento = new DateOnly(2027, 2, 20), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento de verão", Categoria = LancamentoCategorias.Evento, Valor = 50m, Vencimento = new DateOnly(2026, 4, 16), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Guilherme", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash anual", Categoria = LancamentoCategorias.Evento, Valor = 5m, Vencimento = new DateOnly(2026, 5, 29), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Mateus", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 25m, Vencimento = new DateOnly(2026, 8, 11), Status = LancamentoStatuses.Pendente },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Pedro", Tipo = LancamentoTipos.Campori, Descricao = "Campori estadual", Categoria = LancamentoCategorias.Evento, Valor = 180m, Vencimento = new DateOnly(2027, 3, 1), Status = LancamentoStatuses.Atrasado },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Ana", Tipo = LancamentoTipos.Doacao, Descricao = "Doação para o clube", Categoria = LancamentoCategorias.Clube, Valor = 75m, Vencimento = new DateOnly(2026, 8, 12), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Julia", Tipo = LancamentoTipos.Evento, Descricao = "Evento especial", Categoria = LancamentoCategorias.Evento, Valor = 120m, Vencimento = new DateOnly(2026, 4, 2), Status = LancamentoStatuses.Pendente },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Arthur", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 30m, Vencimento = new DateOnly(2026, 8, 15), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Beatriz", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento", Categoria = LancamentoCategorias.Evento, Valor = 90m, Vencimento = new DateOnly(2026, 5, 18), Status = LancamentoStatuses.Atrasado },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Lucas", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash", Categoria = LancamentoCategorias.Evento, Valor = 15m, Vencimento = new DateOnly(2026, 5, 21), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Rafael", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 35m, Vencimento = new DateOnly(2026, 8, 18), Status = LancamentoStatuses.Pendente },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Sofia", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 210m, Vencimento = new DateOnly(2027, 3, 8), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Marina", Tipo = LancamentoTipos.Doacao, Descricao = "Doação para eventos", Categoria = LancamentoCategorias.Clube, Valor = 50m, Vencimento = new DateOnly(2026, 8, 22), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "João", Tipo = LancamentoTipos.Evento, Descricao = "Evento especial", Categoria = LancamentoCategorias.Evento, Valor = 140m, Vencimento = new DateOnly(2026, 4, 17), Status = LancamentoStatuses.Atrasado },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Letícia", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 27m, Vencimento = new DateOnly(2026, 8, 19), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Gabriel", Tipo = LancamentoTipos.Acampamento, Descricao = "Acampamento", Categoria = LancamentoCategorias.Evento, Valor = 110m, Vencimento = new DateOnly(2026, 6, 25), Status = LancamentoStatuses.Pendente },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Alice", Tipo = LancamentoTipos.Uniflash, Descricao = "Uniflash", Categoria = LancamentoCategorias.Evento, Valor = 12m, Vencimento = new DateOnly(2026, 5, 30), Status = LancamentoStatuses.Pago },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Thiago", Tipo = LancamentoTipos.Campori, Descricao = "Campori regional", Categoria = LancamentoCategorias.Evento, Valor = 190m, Vencimento = new DateOnly(2027, 3, 7), Status = LancamentoStatuses.Pendente },
            new Lancamento { Id = Guid.NewGuid(), MembroNome = "Camila", Tipo = LancamentoTipos.Mensalidade, Descricao = "Mensalidade", Categoria = LancamentoCategorias.Clube, Valor = 22m, Vencimento = new DateOnly(2026, 8, 20), Status = LancamentoStatuses.Pago },
        };

        db.Lancamentos.AddRange(demo);
        await db.SaveChangesAsync(cancellationToken);
    }
}
