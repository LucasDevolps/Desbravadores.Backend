using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

/// <summary>Auditoria das alterações de senha e recuperações assistidas (issue #159).</summary>
public partial class AlmiranteDbContext
{
    public const string TabelaAlteracoesSenha = "Usuarios_alteracoes_senha";
    public const string TabelaRecuperacoesSenha = "Usuarios_recuperacoes_senha";
    public const string TriggerAlteracoesSenhaImutavel = "TR_Usuarios_alteracoes_senha_Imutavel";

    /// <summary>
    /// Responsável obrigatório, exceto na recuperação autônoma (sem usuário autenticado, sempre ligada a uma recuperação). Própria: responsável
    /// é o afetado; administrativa e assistida: outra pessoa; assistida exige a recuperação consumida. Todo termo com coluna anulável tem
    /// IS [NOT] NULL explícito: num CHECK, comparação com NULL dá UNKNOWN, que o SQL Server aceita.
    /// </summary>
    public const string CheckAlteracoesSenhaResponsavel =
        "([TipoAlteracao] = 1 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] = [UsuarioAfetadoId] AND [SolicitacaoRecuperacaoId] IS NULL)" +
        " OR ([TipoAlteracao] = 2 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] <> [UsuarioAfetadoId])" +
        " OR ([TipoAlteracao] = 3 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] <> [UsuarioAfetadoId] AND [SolicitacaoRecuperacaoId] IS NOT NULL)" +
        " OR ([TipoAlteracao] = 4 AND [UsuarioResponsavelId] IS NULL AND [SolicitacaoRecuperacaoId] IS NOT NULL)";

    /// <summary>Autorização inteira ou nenhuma: token, validade, versão de segurança, autorizador, IP, método e instante juntos.</summary>
    public const string CheckRecuperacoesSenhaAutorizacao =
        "([TokenHash] IS NULL AND [TokenExpiraEmUtc] IS NULL AND [SecurityVersion] IS NULL)" +
        " OR ([TokenHash] IS NOT NULL AND [TokenExpiraEmUtc] IS NOT NULL AND [SecurityVersion] IS NOT NULL AND [UsuarioAutorizadorId] IS NOT NULL" +
        " AND [IpAutorizador] IS NOT NULL AND [MetodoVerificacao] IS NOT NULL AND [AutorizadaEmUtc] IS NOT NULL)";

    /// <summary>Pendente sem token e aberta; autorizada com token e aberta; concluída/invalidada com instante e motivo.</summary>
    public const string CheckRecuperacoesSenhaStatus =
        "([Status] = 1 AND [TokenHash] IS NULL AND [EncerradaEmUtc] IS NULL AND [MotivoEncerramento] IS NULL)" +
        " OR ([Status] = 2 AND [TokenHash] IS NOT NULL AND [EncerradaEmUtc] IS NULL AND [MotivoEncerramento] IS NULL)" +
        " OR ([Status] IN (3, 4) AND [EncerradaEmUtc] IS NOT NULL AND [MotivoEncerramento] IS NOT NULL)";

    public DbSet<AlteracaoSenha> AlteracoesSenha => Set<AlteracaoSenha>();
    public DbSet<RecuperacaoSenha> RecuperacoesSenha => Set<RecuperacaoSenha>();

    private static void ConfigurarSenhas(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlteracaoSenha>(entity =>
        {
            entity.ToTable(TabelaAlteracoesSenha, table =>
            {
                // Tabela com trigger: sem cláusula OUTPUT nos comandos do EF.
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_Usuarios_alteracoes_senha_Tipo", "[TipoAlteracao] IN (1, 2, 3, 4)");
                table.HasCheckConstraint("CK_Usuarios_alteracoes_senha_Responsavel", CheckAlteracoesSenhaResponsavel);
            });
            entity.HasKey(a => a.Id);
            entity.Property(a => a.IpResponsavel).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false).IsRequired();
            entity.Property(a => a.TraceId).HasColumnType("varchar(64)").HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.Property(a => a.TipoAlteracao).HasConversion<int>().IsRequired();
            entity.Property(a => a.AlteradaEmUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.HasIndex(a => new { a.UsuarioAfetadoId, a.AlteradaEmUtc }).HasDatabaseName("IX_Usuarios_alteracoes_senha_afetado_data");
            entity.HasIndex(a => new { a.UsuarioResponsavelId, a.AlteradaEmUtc }).HasDatabaseName("IX_Usuarios_alteracoes_senha_responsavel_data");
            entity.HasIndex(a => a.SolicitacaoRecuperacaoId)
                .IsUnique()
                .HasDatabaseName("UX_Usuarios_alteracoes_senha_recuperacao")
                .HasFilter("[SolicitacaoRecuperacaoId] IS NOT NULL");

            entity.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioAfetadoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RecuperacaoSenha>().WithMany().HasForeignKey(a => a.SolicitacaoRecuperacaoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecuperacaoSenha>(entity =>
        {
            entity.ToTable(TabelaRecuperacoesSenha, table =>
            {
                table.HasCheckConstraint("CK_Usuarios_recuperacoes_senha_Origem",
                    "([Origem] = 1 AND [IpSolicitacao] IS NOT NULL) OR ([Origem] = 2 AND [IpSolicitacao] IS NULL)");
                table.HasCheckConstraint("CK_Usuarios_recuperacoes_senha_Status", CheckRecuperacoesSenhaStatus);
                table.HasCheckConstraint("CK_Usuarios_recuperacoes_senha_Autorizacao", CheckRecuperacoesSenhaAutorizacao);
                table.HasCheckConstraint("CK_Usuarios_recuperacoes_senha_Metodo", "[MetodoVerificacao] IS NULL OR [MetodoVerificacao] IN (1, 2)");
                table.HasCheckConstraint("CK_Usuarios_recuperacoes_senha_Solicitacoes", "[Solicitacoes] >= 1");
            });
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Origem).HasConversion<int>().IsRequired();
            entity.Property(r => r.Status).HasConversion<int>().IsRequired();
            entity.Property(r => r.MetodoVerificacao).HasConversion<int?>();
            entity.Property(r => r.IpSolicitacao).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false);
            entity.Property(r => r.IpAutorizador).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false);
            entity.Property(r => r.IpConclusao).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false);
            entity.Property(r => r.MotivoEncerramento).HasColumnType("varchar(40)").HasMaxLength(40).IsUnicode(false);
            entity.Property(r => r.TokenHash).HasColumnType("binary(32)");
            entity.Property(r => r.RowVersion).IsRowVersion();
            foreach (var data in new[] { nameof(RecuperacaoSenha.SolicitadaEmUtc), nameof(RecuperacaoSenha.UltimaSolicitacaoEmUtc), nameof(RecuperacaoSenha.ExpiraEmUtc) })
            {
                entity.Property<DateTime>(data).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            }
            foreach (var data in new[] { nameof(RecuperacaoSenha.AutorizadaEmUtc), nameof(RecuperacaoSenha.TokenExpiraEmUtc), nameof(RecuperacaoSenha.EncerradaEmUtc) })
            {
                entity.Property<DateTime?>(data).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
            }

            entity.HasIndex(r => r.UsuarioId)
                .IsUnique()
                .HasDatabaseName("UX_Usuarios_recuperacoes_senha_aberta")
                .HasFilter("[Status] IN (1, 2)");
            entity.HasIndex(r => r.TokenHash)
                .IsUnique()
                .HasDatabaseName("UX_Usuarios_recuperacoes_senha_token")
                .HasFilter("[TokenHash] IS NOT NULL");
            entity.HasIndex(r => new { r.Status, r.SolicitadaEmUtc, r.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("IX_Usuarios_recuperacoes_senha_abertas")
                .HasFilter("[Status] IN (1, 2)")
                .IncludeProperties(r => new { r.UsuarioId, r.ExpiraEmUtc });
            entity.HasIndex(r => r.UsuarioAutorizadorId);

            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.UsuarioAutorizadorId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
