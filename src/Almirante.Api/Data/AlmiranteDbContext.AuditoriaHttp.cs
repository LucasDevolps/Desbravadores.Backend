using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

/// <summary>Auditoria das requisições e respostas HTTP (issue #158).</summary>
public partial class AlmiranteDbContext
{
    public const string TabelaAuditoriaHttp = "auditoria_http";
    public const string TriggerAuditoriaHttpImutavel = "TR_auditoria_http_Imutavel";

    public static string CheckAuditoriaHttpEstados =>
        $"[EstadoCapturaRequest] IN ({ValoresEstadoCaptura}) AND [EstadoCapturaResponse] IN ({ValoresEstadoCaptura})";

    public const string CheckAuditoriaHttpJson =
        "ISJSON([Request]) = 1 AND ISJSON([Headers]) = 1 AND ([Response] IS NULL OR ISJSON([Response]) = 1)" +
        " AND ([Exception] IS NULL OR ISJSON([Exception]) = 1)";

    public const string CheckAuditoriaHttpUsuario =
        "[IdentificacaoUsuario] = 'ANONIMO' OR TRY_CONVERT(uniqueidentifier, [IdentificacaoUsuario]) IS NOT NULL";

    private static string ValoresEstadoCaptura => string.Join(", ", Enum.GetValues<EstadoCapturaHttp>().Select(e => (int)e));

    public DbSet<AuditoriaHttp> AuditoriasHttp => Set<AuditoriaHttp>();

    private static void ConfigurarAuditoriaHttp(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditoriaHttp>(entity =>
        {
            entity.ToTable(TabelaAuditoriaHttp, table =>
            {
                // Tabela com trigger: sem cláusula OUTPUT nos comandos do EF.
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_auditoria_http_Estados", CheckAuditoriaHttpEstados);
                table.HasCheckConstraint("CK_auditoria_http_Json", CheckAuditoriaHttpJson);
                table.HasCheckConstraint("CK_auditoria_http_Usuario", CheckAuditoriaHttpUsuario);
                table.HasCheckConstraint("CK_auditoria_http_DuracaoMs", "[DuracaoMs] >= 0");
                table.HasCheckConstraint("CK_auditoria_http_StatusCode", "[StatusCode] IS NULL OR [StatusCode] BETWEEN 100 AND 999");
            });
            // Inserção sempre no fim da tabela: a chave clusterizada é a data, e o Id (aleatório) só deduplica.
            entity.HasKey(a => a.Id).IsClustered(false);
            entity.Property(a => a.Url).HasColumnType($"varchar({AuditoriaHttp.UrlMaximo})").HasMaxLength(AuditoriaHttp.UrlMaximo).IsUnicode(false).IsRequired();
            entity.Property(a => a.MetodoHttp).HasColumnType($"varchar({AuditoriaHttp.MetodoMaximo})").HasMaxLength(AuditoriaHttp.MetodoMaximo).IsUnicode(false).IsRequired();
            entity.Property(a => a.Request).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            entity.Property(a => a.Response).HasColumnType("varchar(max)").IsUnicode(false);
            entity.Property(a => a.Headers).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            entity.Property(a => a.Exception).HasColumnType("varchar(max)").IsUnicode(false);
            entity.Property(a => a.DataRequestUtc).HasColumnType("datetimeoffset(7)");
            entity.Property(a => a.IpUsuario).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false).IsRequired();
            entity.Property(a => a.IdentificacaoUsuario).HasColumnType("varchar(36)").HasMaxLength(36).IsUnicode(false).IsRequired();
            entity.Property(a => a.TraceId).HasColumnType("varchar(64)").HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.Property(a => a.EstadoCapturaRequest).HasConversion<int>().IsRequired();
            entity.Property(a => a.EstadoCapturaResponse).HasConversion<int>().IsRequired();

            entity.HasIndex(a => a.DataRequestUtc).IsClustered().HasDatabaseName("CIX_auditoria_http_data");
            entity.HasIndex(a => new { a.StatusCode, a.DataRequestUtc }).HasDatabaseName("IX_auditoria_http_status_data");
            entity.HasIndex(a => new { a.IdentificacaoUsuario, a.DataRequestUtc }).HasDatabaseName("IX_auditoria_http_usuario_data");
            entity.HasIndex(a => a.TraceId).HasDatabaseName("IX_auditoria_http_trace");
        });
    }
}
