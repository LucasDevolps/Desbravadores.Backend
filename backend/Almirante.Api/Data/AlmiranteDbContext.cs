using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

public class AlmiranteDbContext(DbContextOptions<AlmiranteDbContext> options) : DbContext(options)
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<Cargo> Cargos => Set<Cargo>();
    public DbSet<LancamentoOperacao> LancamentosOperacoes => Set<LancamentoOperacao>();

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

        modelBuilder.Entity<Lancamento>(entity =>
        {
            // Lancamentos tem um trigger AFTER UPDATE (TR_Lancamentos_AuditoriaExclusaoLogica,
            // ver migration AddLancamentoGeral). O SQL Server rejeita OUTPUT sem INTO em DML sobre
            // uma tabela com trigger ("Msg 334"); desligar a cláusula OUTPUT aqui faz o EF Core
            // usar SELECT @@ROWCOUNT no lugar, compatível com o trigger, para qualquer INSERT/
            // UPDATE/DELETE gerado por SaveChanges nesta tabela (não afeta o UPDATE de exclusão
            // lógica do lançamento geral, que é SQL bruto e nunca usa OUTPUT).
            entity.ToTable("Lancamentos", tb => tb.UseSqlOutputClause(false));
            entity.HasKey(l => l.Id);
            entity.Property(l => l.MembroNome).HasMaxLength(200).IsRequired();
            entity.Property(l => l.Tipo).HasMaxLength(50).IsRequired();
            entity.Property(l => l.Descricao).HasMaxLength(500);
            entity.Property(l => l.Categoria).HasMaxLength(50).IsRequired();
            entity.Property(l => l.Valor).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Moeda).HasMaxLength(3).IsRequired();
            entity.Property(l => l.Status).HasMaxLength(20).IsRequired();
            entity.Property(l => l.Ativo).IsRequired();

            entity.HasIndex(l => l.Status);
            entity.HasIndex(l => l.Tipo);
            entity.HasIndex(l => l.Vencimento);
            entity.HasIndex(l => l.MembroId);
            entity.HasIndex(l => l.Ativo);
            entity.HasIndex(l => l.OperacaoId);
        });

        modelBuilder.Entity<LancamentoOperacao>(entity =>
        {
            entity.ToTable("LancamentosOperacoes");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(o => o.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(o => o.Tipo).HasMaxLength(50).IsRequired();
            entity.Property(o => o.Categoria).HasMaxLength(50).IsRequired();
            entity.Property(o => o.Valor).HasColumnType("decimal(18,2)");

            entity.HasIndex(o => o.IdempotencyKey).IsUnique();

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(o => o.CriadoPorUsuarioId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LancamentoDeletado>(entity =>
        {
            entity.ToTable("lancamentos_deletados");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.IpResponsavel).HasMaxLength(45).IsRequired();
            entity.Property(d => d.Motivo).HasMaxLength(255).IsRequired();
            entity.Property(d => d.MembroNome).HasMaxLength(200).IsRequired();
            entity.Property(d => d.Tipo).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Categoria).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Valor).HasColumnType("decimal(18,2)");
            entity.Property(d => d.Moeda).HasMaxLength(3).IsRequired();
            entity.Property(d => d.Status).HasMaxLength(20).IsRequired();

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
