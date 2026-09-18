using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
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

    // SQL error 1801 = "Database '...' already exists". Under container restarts the database
    // created by a previous run already exists (thanks to the persistent volume), but EF Core's
    // existence check can occasionally race with SQL Server still settling right after a restart
    // and attempt CREATE DATABASE again. Retrying once, now that the database is visible, makes
    // startup idempotent instead of crashing the whole process on an unhandled SqlException.
    public static async Task MigrateWithRetryAsync(AlmiranteDbContext db, CancellationToken cancellationToken)
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
        if (string.IsNullOrWhiteSpace(seedOptions.Email))
        {
            throw new InvalidOperationException("SeedAdmin:Email é obrigatório.");
        }

        var emailNormalizado = seedOptions.Email.Trim().ToUpperInvariant();

        // Idempotente: um admin existente nunca tem a senha sobrescrita, e a senha configurada só é
        // exigida/validada quando o bootstrap realmente vai criar o usuário.
        var exists = await db.Usuarios.AnyAsync(u => u.EmailNormalizado == emailNormalizado, cancellationToken);
        if (exists)
        {
            return;
        }

        var erros = PasswordPolicy.Validate(seedOptions.Senha, seedOptions.Email);
        if (erros.Count > 0)
        {
            // Nunca inclui o valor da senha na mensagem.
            throw new InvalidOperationException(
                "SeedAdmin:Senha não atende à política de senha e o administrador inicial não foi criado. " +
                "Defina-a fora do Git (dotnet user-secrets, variável de ambiente SeedAdmin__Senha/SEED_ADMIN_SENHA ou cofre). " +
                string.Join(" ", erros));
        }

        var cargoAdminId = await db.Cargos
            .Where(c => c.Role == Roles.Admin)
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
        if (await db.Lancamentos.AnyAsync(cancellationToken)) return;
        var membroId = await db.Usuarios.Select(u => u.Id).FirstOrDefaultAsync(cancellationToken);
        if (membroId == Guid.Empty) return;
        db.Lancamentos.Add(new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = membroId, Finalidade = LancamentoFinalidades.Mensalidade,
            Descricao = "Mensalidade do clube", Categoria = LancamentoCategorias.Clube, Valor = 20m,
            Vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), Status = LancamentoStatuses.Pendente,
            TipoFluxo = TipoFluxoLancamento.Entrada
        });
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
            new Cargo { Id = Guid.NewGuid(), Nome = "Administrador", Descricao = "Acesso administrativo total à plataforma, responsável pela gestão do sistema e não corresponde a um cargo de unidade do clube.", CriadoPor = criadoPor, Role = Roles.Admin },
            new Cargo { Id = Guid.NewGuid(), Nome = "Diretor", Descricao = "Lidera todo o clube, dirige as reuniões, define metas do ano e preside as comissões.", CriadoPor = criadoPor, Role = Roles.Diretor },
            new Cargo { Id = Guid.NewGuid(), Nome = "Diretor Associado", Descricao = "Coordena classes, especialidades e unidades, substituindo o diretor em sua ausência.", CriadoPor = criadoPor, Role = Roles.DiretorAssociado },
            new Cargo { Id = Guid.NewGuid(), Nome = "Secretário", Descricao = "Registra pontos, presenças, atas e relatórios, além de cuidar do cadastro e da comunicação do clube.", CriadoPor = criadoPor, Role = Roles.Secretario },
            new Cargo { Id = Guid.NewGuid(), Nome = "Tesoureiro", Descricao = "Administra as finanças do clube junto com a tesouraria da igreja.", CriadoPor = criadoPor, Role = Roles.Tesoureiro },
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
