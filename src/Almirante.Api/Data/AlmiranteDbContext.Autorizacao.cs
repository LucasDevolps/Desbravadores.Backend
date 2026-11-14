using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

/// <summary>RBAC dinâmico (issue #182): catálogo de permissões, concessões por cargo, regras por alvo, revisão global e histórico.</summary>
public partial class AlmiranteDbContext
{
    public const string TabelaPermissoes = "Permissoes";
    public const string TabelaCargoPermissoes = "CargoPermissoes";
    public const string TabelaCargoRegrasAlvo = "CargoRegrasAlvo";
    public const string TabelaAutorizacaoEstado = "AutorizacaoEstado";
    public const string TabelaAutorizacaoHistorico = "Autorizacao_historico";
    public const string TriggerAutorizacaoHistoricoImutavel = "TR_Autorizacao_historico_Imutavel";

    public const int MotivoAutorizacaoMaximo = 255;

    public DbSet<Permissao> Permissoes => Set<Permissao>();
    public DbSet<CargoPermissao> CargoPermissoes => Set<CargoPermissao>();
    public DbSet<CargoRegraAlvo> CargoRegrasAlvo => Set<CargoRegraAlvo>();
    public DbSet<AutorizacaoEstado> AutorizacaoEstado => Set<AutorizacaoEstado>();
    public DbSet<AutorizacaoHistorico> AutorizacaoHistorico => Set<AutorizacaoHistorico>();

    private static void ConfigurarAutorizacao(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Permissao>(entity =>
        {
            entity.ToTable(TabelaPermissoes);
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Chave).HasColumnType($"varchar({CatalogoPermissoes.ChaveMaximo})")
                .HasMaxLength(CatalogoPermissoes.ChaveMaximo).IsUnicode(false).IsRequired();
            entity.Property(p => p.Modulo).HasColumnType($"varchar({CatalogoPermissoes.ModuloMaximo})")
                .HasMaxLength(CatalogoPermissoes.ModuloMaximo).IsUnicode(false).IsRequired();
            entity.Property(p => p.Descricao).HasColumnType($"varchar({CatalogoPermissoes.DescricaoMaximo})")
                .HasMaxLength(CatalogoPermissoes.DescricaoMaximo).IsUnicode(false).IsRequired();
            entity.HasIndex(p => p.Chave).IsUnique().HasDatabaseName("UX_Permissoes_Chave");
            entity.HasData(CatalogoPermissoes.Todas.Select(p => new Permissao
            {
                Id = p.Id, Chave = p.Chave, Modulo = p.Modulo, Descricao = p.Descricao, Escrita = p.Escrita, Ativo = true,
                ReservadaSistema = p.ReservadaSistema, ExigeRegraAlvo = p.ExigeRegraAlvo,
            }));
        });

        modelBuilder.Entity<CargoPermissao>(entity =>
        {
            entity.ToTable(TabelaCargoPermissoes);
            entity.HasKey(c => c.Id);
            entity.Property(c => c.ConcedidaEmUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.HasIndex(c => new { c.CargoId, c.PermissaoId }).IsUnique().HasDatabaseName("UX_CargoPermissoes_Cargo_Permissao");
            entity.HasIndex(c => c.PermissaoId);
            entity.HasOne(c => c.Cargo).WithMany().HasForeignKey(c => c.CargoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(c => c.Permissao).WithMany().HasForeignKey(c => c.PermissaoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(c => c.ConcedidaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(c => c.RevogadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CargoRegraAlvo>(entity =>
        {
            entity.ToTable(TabelaCargoRegrasAlvo);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ConcedidaEmUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.HasIndex(r => new { r.CargoSolicitanteId, r.PermissaoId, r.CargoAlvoId }).IsUnique()
                .HasDatabaseName("UX_CargoRegrasAlvo_Solicitante_Permissao_Alvo");
            entity.HasIndex(r => r.PermissaoId);
            entity.HasIndex(r => r.CargoAlvoId);
            entity.HasOne(r => r.CargoSolicitante).WithMany().HasForeignKey(r => r.CargoSolicitanteId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.CargoAlvo).WithMany().HasForeignKey(r => r.CargoAlvoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.Permissao).WithMany().HasForeignKey(r => r.PermissaoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.ConcedidaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.RevogadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AutorizacaoEstado>(entity =>
        {
            entity.ToTable(TabelaAutorizacaoEstado, table => table.HasCheckConstraint("CK_AutorizacaoEstado_Unico", $"[Id] = {Entities.AutorizacaoEstado.IdUnico}"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Revisao).IsConcurrencyToken();
            entity.HasData(new AutorizacaoEstado { Id = Entities.AutorizacaoEstado.IdUnico, Revisao = 1 });
        });

        modelBuilder.Entity<AutorizacaoHistorico>(entity =>
        {
            entity.ToTable(TabelaAutorizacaoHistorico, table =>
            {
                // Tabela com trigger: sem cláusula OUTPUT nos comandos do EF.
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_Autorizacao_historico_Operacao",
                    $"[Operacao] IN ('{Entities.AutorizacaoHistorico.CargoCriado}', '{Entities.AutorizacaoHistorico.CargoAlterado}', " +
                    $"'{Entities.AutorizacaoHistorico.CargoDesativado}', '{Entities.AutorizacaoHistorico.CargoReativado}', " +
                    $"'{Entities.AutorizacaoHistorico.ConcessoesSubstituidas}', '{Entities.AutorizacaoHistorico.RegrasAlvoSubstituidas}')");
                table.HasCheckConstraint("CK_Autorizacao_historico_Json", "([Antes] IS NULL OR ISJSON([Antes]) = 1) AND ISJSON([Depois]) = 1");
            });
            entity.HasKey(h => h.Id);
            entity.Property(h => h.OcorridoEmUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.Property(h => h.Operacao).HasColumnType("varchar(40)").HasMaxLength(40).IsUnicode(false).IsRequired();
            entity.Property(h => h.Antes).HasColumnType("varchar(max)").IsUnicode(false);
            entity.Property(h => h.Depois).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            entity.Property(h => h.Motivo).HasColumnType($"varchar({MotivoAutorizacaoMaximo})").HasMaxLength(MotivoAutorizacaoMaximo)
                .IsUnicode(false).IsRequired();
            entity.Property(h => h.IpResponsavel).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false).IsRequired();
            entity.Property(h => h.TraceId).HasColumnType("varchar(64)").HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.HasIndex(h => new { h.CargoId, h.OcorridoEmUtc }).HasDatabaseName("IX_Autorizacao_historico_cargo_data");
            entity.HasIndex(h => new { h.UsuarioResponsavelId, h.OcorridoEmUtc }).HasDatabaseName("IX_Autorizacao_historico_responsavel_data");
            entity.HasOne<Cargo>().WithMany().HasForeignKey(h => h.CargoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
