using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

public class AlmiranteDbContext(DbContextOptions<AlmiranteDbContext> options) : DbContext(options)
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<Cargo> Cargos => Set<Cargo>();
    public DbSet<LancamentoOperacao> LancamentosOperacoes => Set<LancamentoOperacao>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Somente leitura pela aplicação: linhas são inseridas exclusivamente pelo trigger de
    // auditoria (ver Entities/LancamentoDeletado.cs).
    public DbSet<LancamentoDeletado> LancamentosDeletados => Set<LancamentoDeletado>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Usuario>(entity =>
        {
            entity.ToTable("Usuarios");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Nome).HasMaxLength(200).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.EmailNormalizado).HasMaxLength(256).IsRequired();
            entity.Property(u => u.SenhaHash).IsRequired();
            entity.HasIndex(u => u.EmailNormalizado).IsUnique();

            entity.HasOne(u => u.Cargo)
                .WithMany()
                .HasForeignKey(u => u.CargoId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("AuthSessions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RevocationReason).HasMaxLength(100);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => new { x.UsuarioId, x.AbsoluteExpiresAtUtc });
            entity.HasIndex(x => x.AbsoluteExpiresAtUtc);
            entity.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens"); entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasColumnType("binary(32)").IsRequired();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.SessionId, x.ExpiresAtUtc });
            entity.HasIndex(x => x.ExpiresAtUtc);
            entity.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ReplacedByToken).WithMany().HasForeignKey(x => x.ReplacedByTokenId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Lancamento>(entity =>
        {
            // Lancamentos tem um trigger AFTER UPDATE (TR_Lancamentos_AuditoriaExclusaoLogica,
            // ver migration AddLancamentoGeral). O SQL Server rejeita OUTPUT sem INTO em DML sobre
            // uma tabela com trigger ("Msg 334"); desligar a cláusula OUTPUT aqui faz o EF Core
            // usar SELECT @@ROWCOUNT no lugar, compatível com o trigger, para qualquer INSERT/
            // UPDATE/DELETE gerado por SaveChanges nesta tabela (não afeta o UPDATE de exclusão
            // lógica do lançamento geral, que é SQL bruto e nunca usa OUTPUT).
            entity.ToTable("Lancamentos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_Lancamentos_Categoria", "[Categoria] IN (0, 1)");
                table.HasCheckConstraint("CK_Lancamentos_Status", "[Status] IN (0, 1, 2)");
                table.HasCheckConstraint("CK_Lancamentos_TipoFluxo", "[TipoFluxo] IN (0, 1)");
            });
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Finalidade).HasMaxLength(50).IsRequired();
            entity.Property(l => l.Descricao).HasMaxLength(500);
            entity.Property(l => l.Categoria).HasConversion<int>().IsRequired();
            entity.Property(l => l.TipoFluxo).HasConversion<int>().IsRequired();
            entity.Property(l => l.Valor).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Status).HasConversion<int>().IsRequired();
            entity.Property(l => l.Ativo).IsRequired();

            entity.HasIndex(l => l.Status);
            entity.HasIndex(l => l.Finalidade);
            entity.HasIndex(l => l.Vencimento);
            entity.HasIndex(l => l.MembroId);
            entity.HasIndex(l => l.Ativo);
            entity.HasIndex(l => l.OperacaoId);
            entity.HasOne(l => l.Membro).WithMany(u => u.Lancamentos).HasForeignKey(l => l.MembroId).OnDelete(DeleteBehavior.Restrict);

            // Auditoria mínima do PUT (issue #50): só o último responsável, sem navegação (mesmo padrão
            // de LancamentoOperacao.CriadoPorUsuarioId/LancamentoDeletado.UsuarioResponsavelId abaixo).
            // Restrict (não Cascade): excluir um usuário nunca deve apagar o histórico de lançamentos que
            // ele atualizou.
            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(l => l.AtualizadoPorUsuarioId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LancamentoOperacao>(entity =>
        {
            entity.ToTable("LancamentosOperacoes", table =>
            {
                table.HasCheckConstraint("CK_LancamentosOperacoes_Categoria", "[Categoria] IN (0, 1)");
                table.HasCheckConstraint("CK_LancamentosOperacoes_TipoFluxo", "[TipoFluxo] IN (0, 1)");
            });
            entity.HasKey(o => o.Id);
            entity.Property(o => o.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(o => o.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(o => o.Finalidade).HasMaxLength(50).IsRequired();
            entity.Property(o => o.Descricao).HasMaxLength(500);
            entity.Property(o => o.Categoria).HasConversion<int>().IsRequired();
            entity.Property(o => o.TipoFluxo).HasConversion<int>().IsRequired();
            entity.Property(o => o.Valor).HasColumnType("decimal(18,2)");

            entity.HasIndex(o => o.IdempotencyKey).IsUnique();

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(o => o.CriadoPorUsuarioId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LancamentoDeletado>(entity =>
        {
            entity.ToTable("lancamentos_deletados", table =>
            {
                table.HasCheckConstraint("CK_lancamentos_deletados_Categoria", "[Categoria] IN (0, 1)");
                table.HasCheckConstraint("CK_lancamentos_deletados_Status", "[Status] IN (0, 1, 2)");
                table.HasCheckConstraint("CK_lancamentos_deletados_TipoFluxo", "[TipoFluxo] IN (0, 1)");
            });
            entity.HasKey(d => d.Id);
            entity.Property(d => d.IpResponsavel).HasMaxLength(45).IsRequired();
            entity.Property(d => d.Motivo).HasMaxLength(255).IsRequired();
            entity.Property(d => d.Finalidade).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Categoria).HasConversion<int>().IsRequired();
            entity.Property(d => d.TipoFluxo).HasConversion<int>().IsRequired();
            entity.Property(d => d.Valor).HasColumnType("decimal(18,2)");
            entity.Property(d => d.Status).HasConversion<int>().IsRequired();

            entity.HasIndex(d => d.LancamentoId);

            entity.HasOne<Lancamento>()
                .WithMany()
                .HasForeignKey(d => d.LancamentoId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(d => d.UsuarioResponsavelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Cargo>(entity =>
        {
            entity.ToTable("Cargos");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Nome).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Descricao).HasMaxLength(1000).IsRequired();
            entity.Property(c => c.CriadoPor).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Role).HasMaxLength(20).IsRequired();
            entity.HasIndex(c => c.Role).IsUnique();
        });
    }
}
