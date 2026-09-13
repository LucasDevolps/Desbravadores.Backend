using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

public class AlmiranteDbContext(DbContextOptions<AlmiranteDbContext> options) : DbContext(options)
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();

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
            entity.Property(u => u.Roles).HasMaxLength(200).IsRequired();
            entity.HasIndex(u => u.EmailNormalizado).IsUnique();
        });

        modelBuilder.Entity<Lancamento>(entity =>
        {
            entity.ToTable("Lancamentos");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.MembroNome).HasMaxLength(200).IsRequired();
            entity.Property(l => l.Tipo).HasMaxLength(50).IsRequired();
            entity.Property(l => l.Descricao).HasMaxLength(500);
            entity.Property(l => l.Categoria).HasMaxLength(50).IsRequired();
            entity.Property(l => l.Valor).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Moeda).HasMaxLength(3).IsRequired();
            entity.Property(l => l.Status).HasMaxLength(20).IsRequired();

            entity.HasIndex(l => l.Status);
            entity.HasIndex(l => l.Tipo);
            entity.HasIndex(l => l.Vencimento);
            entity.HasIndex(l => l.MembroId);
        });
    }
}
