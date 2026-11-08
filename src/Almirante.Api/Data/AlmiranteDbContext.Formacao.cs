using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Almirante.Api.Data;

/// <summary>
/// Acompanhamento de classes e especialidades (issue #125). Tabelas com trigger de histórico não usam OUTPUT no SQL gerado pelo EF.
/// </summary>
public partial class AlmiranteDbContext
{
    private static void ConfigurarFormacao(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Especialidade>(entity =>
        {
            entity.ToTable("especialidades", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_especialidades_NivelDificuldade",
                    $"[NivelDificuldade] IS NULL OR [NivelDificuldade] BETWEEN {RegrasFormacao.NivelDificuldadeMinimo} AND {RegrasFormacao.NivelDificuldadeMaximo}");
                table.HasCheckConstraint("CK_especialidades_Modalidade", "[Modalidade] IS NULL OR [Modalidade] IN (1, 2)");
                table.HasCheckConstraint("CK_especialidades_Inativacao", "[Ativo] = 1 OR [MotivoInativacao] IS NOT NULL");
            });
            entity.HasKey(e => e.Id);
            Varchar(entity.Property(e => e.Nome), RegrasFormacao.NomeEspecialidadeMaximo).IsRequired();
            Varchar(entity.Property(e => e.CodigoOficial), RegrasFormacao.CodigoOficialMaximo);
            Varchar(entity.Property(e => e.Area), RegrasFormacao.AreaMaximo).IsRequired();
            Varchar(entity.Property(e => e.Descricao), RegrasFormacao.DescricaoMaximo);
            entity.Property(e => e.Modalidade).HasConversion<int?>();
            Varchar(entity.Property(e => e.PreRequisitosTexto), RegrasFormacao.PreRequisitosTextoMaximo);
            Varchar(entity.Property(e => e.ReferenciaDistintivo), RegrasFormacao.ReferenciaMaximo);
            Varchar(entity.Property(e => e.FonteOficial), RegrasFormacao.FonteMaximo).IsRequired();
            Varchar(entity.Property(e => e.EdicaoRevisao), RegrasFormacao.EdicaoMaximo);
            Varchar(entity.Property(e => e.MotivoInativacao), RegrasFormacao.JustificativaMaximo);
            entity.Property(e => e.Versao).IsRowVersion();

            entity.HasIndex(e => e.CodigoOficial).IsUnique().HasDatabaseName("UX_especialidades_CodigoOficial")
                .HasFilter("[CodigoOficial] IS NOT NULL");
            entity.HasIndex(e => new { e.Nome, e.Area, e.Modalidade }).IsUnique().HasDatabaseName("UX_especialidades_Nome_Area_Modalidade_ativa")
                .HasFilter("[Ativo] = 1");
            entity.HasIndex(e => new { e.Area, e.Ativo });

            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EspecialidadePreRequisito>(entity =>
        {
            entity.ToTable("especialidades_prerequisitos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_especialidades_prerequisitos_Proprio", "[EspecialidadeId] <> [PreRequisitoId]");
            });
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.EspecialidadeId, p.PreRequisitoId }).IsUnique();
            entity.HasIndex(p => p.PreRequisitoId);
            entity.HasOne<Especialidade>().WithMany().HasForeignKey(p => p.EspecialidadeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Especialidade>().WithMany().HasForeignKey(p => p.PreRequisitoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(p => p.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(p => p.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RevisaoRequisitos>(entity =>
        {
            entity.ToTable("requisitos_revisoes", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_requisitos_revisoes_Alvo",
                    "([Classe] IS NOT NULL AND [EspecialidadeId] IS NULL) OR ([Classe] IS NULL AND [EspecialidadeId] IS NOT NULL)");
                table.HasCheckConstraint("CK_requisitos_revisoes_Classe", $"[Classe] IS NULL OR [Classe] IN ({ValoresClasse})");
                table.HasCheckConstraint("CK_requisitos_revisoes_Situacao", "[Situacao] IN (1, 2, 3, 4)");
                table.HasCheckConstraint("CK_requisitos_revisoes_Publicacao",
                    "([Situacao] IN (1, 4) AND [PublicadaPorUsuarioId] IS NULL AND [PublicadaEmUtc] IS NULL)" +
                    " OR ([Situacao] IN (2, 3) AND [PublicadaPorUsuarioId] IS NOT NULL AND [PublicadaEmUtc] IS NOT NULL)");
                table.HasCheckConstraint("CK_requisitos_revisoes_Descarte", "[Situacao] <> 4 OR [MotivoDescarte] IS NOT NULL");
            });
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Classe).HasConversion<int?>();
            entity.Property(r => r.Situacao).HasConversion<int>();
            Varchar(entity.Property(r => r.Edicao), RegrasFormacao.EdicaoMaximo).IsRequired();
            Varchar(entity.Property(r => r.FonteOficial), RegrasFormacao.FonteMaximo).IsRequired();
            Varchar(entity.Property(r => r.Observacoes), RegrasFormacao.ObservacoesRevisaoMaximo);
            Varchar(entity.Property(r => r.MotivoDescarte), RegrasFormacao.JustificativaMaximo);
            entity.Property(r => r.Versao).IsRowVersion();

            entity.HasIndex(r => r.Classe).IsUnique().HasDatabaseName("UX_requisitos_revisoes_Classe_publicada")
                .HasFilter("[Situacao] = 2 AND [Classe] IS NOT NULL");
            entity.HasIndex(r => r.EspecialidadeId).IsUnique().HasDatabaseName("UX_requisitos_revisoes_Especialidade_publicada")
                .HasFilter("[Situacao] = 2 AND [EspecialidadeId] IS NOT NULL");
            entity.HasIndex(r => new { r.Classe, r.Situacao }).HasDatabaseName("IX_requisitos_revisoes_Classe_Situacao");
            entity.HasIndex(r => new { r.EspecialidadeId, r.Situacao }).HasDatabaseName("IX_requisitos_revisoes_Especialidade_Situacao");

            entity.HasMany(r => r.Requisitos).WithOne().HasForeignKey(q => q.RevisaoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Especialidade>().WithMany().HasForeignKey(r => r.EspecialidadeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.PublicadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Requisito>(entity =>
        {
            entity.ToTable("requisitos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_requisitos_Regra",
                    "([Regra] IS NULL AND [QuantidadeMinima] IS NULL) OR ([Regra] = 1 AND [QuantidadeMinima] IS NULL)" +
                    " OR ([Regra] = 2 AND [QuantidadeMinima] >= 1)");
                table.HasCheckConstraint("CK_requisitos_Especialidades",
                    "([EspecialidadesExigidas] IS NULL AND [EspecialidadeExigidaId] IS NULL AND [AreaEspecialidadeExigida] IS NULL" +
                    " AND [SomenteEspecialidadeNaoRealizada] = 0)" +
                    $" OR ([Regra] IS NULL AND [EspecialidadesExigidas] BETWEEN 1 AND {RegrasFormacao.EspecialidadesExigidasMaximo})");
                table.HasCheckConstraint("CK_requisitos_Ordem", "[Ordem] >= 1");
            });
            entity.HasKey(q => q.Id);
            Varchar(entity.Property(q => q.Codigo), RegrasFormacao.CodigoRequisitoMaximo).IsRequired();
            Varchar(entity.Property(q => q.Secao), RegrasFormacao.SecaoMaximo);
            Varchar(entity.Property(q => q.Descricao), RegrasFormacao.DescricaoMaximo).IsRequired();
            entity.Property(q => q.Regra).HasConversion<int?>();
            Varchar(entity.Property(q => q.ReferenciaOficial), RegrasFormacao.ReferenciaMaximo);
            Varchar(entity.Property(q => q.AreaEspecialidadeExigida), RegrasFormacao.AreaMaximo);

            entity.HasIndex(q => new { q.RevisaoId, q.Codigo }).IsUnique();
            entity.HasIndex(q => q.RequisitoPaiId);
            entity.HasOne<Requisito>().WithMany().HasForeignKey(q => q.RequisitoPaiId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Especialidade>().WithMany().HasForeignKey(q => q.EspecialidadeExigidaId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MembroClasse>(entity =>
        {
            entity.ToTable("membros_classes", table =>
            {
                ConfigurarChecksVinculo(table, "membros_classes", "Investidura", "InvestiduraRegistrada");
                table.HasCheckConstraint("CK_membros_classes_Classe", $"[Classe] IN ({ValoresClasse})");
                table.HasCheckConstraint("CK_membros_classes_Modalidade", "[Modalidade] IN (1, 2)");
            });
            ConfigurarVinculo(entity, "Investidura", "InvestiduraRegistrada");
            entity.Property(v => v.Classe).HasConversion<int>();
            entity.Property(v => v.Modalidade).HasConversion<int>();
            entity.HasIndex(v => new { v.MembroId, v.Classe }).IsUnique().HasDatabaseName("UX_membros_classes_membro_classe_ativa")
                .HasFilter("[Situacao] <> 3");
            entity.HasIndex(v => new { v.Classe, v.Situacao });
        });

        modelBuilder.Entity<MembroEspecialidade>(entity =>
        {
            entity.ToTable("membros_especialidades", table =>
                ConfigurarChecksVinculo(table, "membros_especialidades", "Entrega", "EntregaRegistrada"));
            ConfigurarVinculo(entity, "Entrega", "EntregaRegistrada");
            entity.HasIndex(v => new { v.MembroId, v.EspecialidadeId }).IsUnique()
                .HasDatabaseName("UX_membros_especialidades_membro_especialidade_ativa").HasFilter("[Situacao] <> 3");
            entity.HasIndex(v => new { v.EspecialidadeId, v.Situacao });
            entity.HasOne<Especialidade>().WithMany().HasForeignKey(v => v.EspecialidadeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProgressoRequisito>(entity =>
        {
            entity.ToTable("progresso_requisitos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_progresso_requisitos_Vinculo",
                    "([MembroClasseId] IS NOT NULL AND [MembroEspecialidadeId] IS NULL) OR ([MembroClasseId] IS NULL AND [MembroEspecialidadeId] IS NOT NULL)");
                table.HasCheckConstraint("CK_progresso_requisitos_Situacao", "[Situacao] IN (1, 2, 3)");
                table.HasCheckConstraint("CK_progresso_requisitos_Validacao",
                    "([Situacao] = 3 AND [ValidadoPorUsuarioId] IS NOT NULL AND [ValidadoEmUtc] IS NOT NULL)" +
                    " OR ([Situacao] <> 3 AND [ValidadoPorUsuarioId] IS NULL AND [ValidadoEmUtc] IS NULL)");
            });
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Situacao).HasConversion<int>();
            entity.Property(p => p.DataCumprimento).HasColumnType("date");
            Varchar(entity.Property(p => p.Observacao), RegrasFormacao.ObservacoesMaximo);
            Varchar(entity.Property(p => p.ReferenciaEvidencia), RegrasFormacao.ReferenciaMaximo);
            entity.HasIndex(p => new { p.MembroClasseId, p.RequisitoId }).IsUnique().HasFilter("[MembroClasseId] IS NOT NULL");
            entity.HasIndex(p => new { p.MembroEspecialidadeId, p.RequisitoId }).IsUnique().HasFilter("[MembroEspecialidadeId] IS NOT NULL");
            entity.HasIndex(p => p.RequisitoId);
            entity.HasOne<MembroClasse>().WithMany().HasForeignKey(p => p.MembroClasseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembroEspecialidade>().WithMany().HasForeignKey(p => p.MembroEspecialidadeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Requisito>().WithMany().HasForeignKey(p => p.RequisitoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(p => p.ValidadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(p => p.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(p => p.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EvidenciaEspecialidade>(entity =>
        {
            entity.ToTable("evidencias_especialidades", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_evidencias_especialidades_Remocao",
                    "([Ativo] = 1 AND [RemovidaPorUsuarioId] IS NULL AND [RemovidaEmUtc] IS NULL)" +
                    " OR ([Ativo] = 0 AND [RemovidaPorUsuarioId] IS NOT NULL AND [RemovidaEmUtc] IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MembroClasseId, e.MembroEspecialidadeId }).IsUnique()
                .HasDatabaseName("UX_evidencias_especialidades_classe_especialidade_ativa").HasFilter("[Ativo] = 1");
            entity.HasIndex(e => e.MembroEspecialidadeId);
            entity.HasIndex(e => e.RequisitoId);
            entity.HasOne<MembroClasse>().WithMany().HasForeignKey(e => e.MembroClasseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembroEspecialidade>().WithMany().HasForeignKey(e => e.MembroEspecialidadeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Requisito>().WithMany().HasForeignKey(e => e.RequisitoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.RemovidaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ValidacaoExterna>(entity =>
        {
            entity.ToTable("validacoes_externas", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_validacoes_externas_Data", "[Confirmada] = 0 OR [Data] IS NOT NULL");
            });
            entity.HasKey(v => v.Id);
            Varchar(entity.Property(v => v.Codigo), RegrasFormacao.CodigoValidacaoMaximo).IsRequired();
            entity.Property(v => v.Data).HasColumnType("date");
            Varchar(entity.Property(v => v.Referencia), RegrasFormacao.ReferenciaMaximo);
            Varchar(entity.Property(v => v.Observacao), RegrasFormacao.ObservacoesMaximo);
            entity.HasIndex(v => new { v.MembroClasseId, v.Codigo }).IsUnique();
            entity.HasOne<MembroClasse>().WithMany().HasForeignKey(v => v.MembroClasseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.RegistradaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HistoricoFormacao>(entity =>
        {
            entity.ToTable("formacao_historico", table =>
            {
                table.HasCheckConstraint("CK_formacao_historico_EstadoAnteriorJson", "[EstadoAnteriorJson] IS NULL OR ISJSON([EstadoAnteriorJson]) = 1");
                table.HasCheckConstraint("CK_formacao_historico_EstadoNovoJson", "ISJSON([EstadoNovoJson]) = 1");
            });
            entity.HasKey(h => h.Id);
            Varchar(entity.Property(h => h.Tabela), RegrasFormacao.AcaoMaximo).IsRequired();
            Varchar(entity.Property(h => h.Acao), RegrasFormacao.AcaoMaximo);
            Varchar(entity.Property(h => h.Motivo), RegrasFormacao.JustificativaMaximo);
            entity.Property(h => h.EstadoAnteriorJson).HasColumnType("varchar(max)").IsUnicode(false);
            entity.Property(h => h.EstadoNovoJson).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            Varchar(entity.Property(h => h.IpResponsavel), 45).IsRequired();
            entity.HasIndex(h => new { h.VinculoId, h.OcorridoEmUtc });
            entity.HasIndex(h => new { h.Tabela, h.RegistroId });
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurarChecksVinculo(TableBuilder table, string tabela, string data, string registro)
    {
        var dataRec = $"[Data{data}]";
        var porRec = $"[{registro}PorUsuarioId]";
        var emRec = $"[{registro}EmUtc]";
        table.UseSqlOutputClause(false);
        table.HasCheckConstraint($"CK_{tabela}_Situacao", "[Situacao] IN (1, 2, 3)");
        table.HasCheckConstraint($"CK_{tabela}_Fluxo",
            "([Historico] = 0 AND [RevisaoId] IS NOT NULL AND [DataInicio] IS NOT NULL AND [OrigemHistorica] IS NULL)" +
            " OR ([Historico] = 1 AND [OrigemHistorica] IS NOT NULL)");
        table.HasCheckConstraint($"CK_{tabela}_Conclusao",
            "([Situacao] = 2 AND [ConclusaoRegistradaPorUsuarioId] IS NOT NULL AND [ConclusaoRegistradaEmUtc] IS NOT NULL" +
            " AND ([DataConclusao] IS NOT NULL OR [Historico] = 1))" +
            " OR ([Situacao] <> 2 AND [DataConclusao] IS NULL AND [ConclusaoRegistradaPorUsuarioId] IS NULL AND [ConclusaoRegistradaEmUtc] IS NULL)");
        table.HasCheckConstraint($"CK_{tabela}_{data}",
            $"({dataRec} IS NULL AND {porRec} IS NULL AND {emRec} IS NULL)" +
            $" OR ([Situacao] = 2 AND {porRec} IS NOT NULL AND {emRec} IS NOT NULL AND ({dataRec} IS NOT NULL OR [Historico] = 1))");
        table.HasCheckConstraint($"CK_{tabela}_Cancelamento",
            "([Situacao] = 3 AND [MotivoCancelamento] IS NOT NULL AND [CanceladoPorUsuarioId] IS NOT NULL AND [CanceladoEmUtc] IS NOT NULL)" +
            " OR ([Situacao] <> 3 AND [MotivoCancelamento] IS NULL AND [CanceladoPorUsuarioId] IS NULL AND [CanceladoEmUtc] IS NULL)");
        table.HasCheckConstraint($"CK_{tabela}_Cronologia",
            "([DataConclusao] IS NULL OR [DataInicio] IS NULL OR [DataConclusao] >= [DataInicio])" +
            $" AND ({dataRec} IS NULL OR [DataConclusao] IS NULL OR {dataRec} >= [DataConclusao])" +
            $" AND ({dataRec} IS NULL OR [DataInicio] IS NULL OR {dataRec} >= [DataInicio])");
    }

    private static void ConfigurarVinculo<T>(EntityTypeBuilder<T> entity, string data, string registro) where T : VinculoFormacao
    {
        entity.HasKey(v => v.Id);
        entity.Property(v => v.Situacao).HasConversion<int>();
        entity.Property(v => v.DataInicio).HasColumnType("date");
        entity.Property(v => v.DataConclusao).HasColumnType("date");
        entity.Property(v => v.DataReconhecimento).HasColumnType("date").HasColumnName($"Data{data}");
        entity.Property(v => v.ReconhecimentoRegistradoPorUsuarioId).HasColumnName($"{registro}PorUsuarioId");
        entity.Property(v => v.ReconhecimentoRegistradoEmUtc).HasColumnName($"{registro}EmUtc");
        Varchar(entity.Property(v => v.OrigemHistorica), RegrasFormacao.OrigemHistoricaMaximo);
        Varchar(entity.Property(v => v.Observacoes), RegrasFormacao.ObservacoesMaximo);
        Varchar(entity.Property(v => v.MotivoCancelamento), RegrasFormacao.JustificativaMaximo);
        entity.Property(v => v.Versao).IsRowVersion();
        entity.Ignore(v => v.Reconhecido);

        entity.HasIndex(v => v.MembroId);
        entity.HasIndex(v => v.RevisaoId);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.MembroId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<RevisaoRequisitos>().WithMany().HasForeignKey(v => v.RevisaoId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.InstrutorId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.ConclusaoRegistradaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.ReconhecimentoRegistradoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.CanceladoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
    }

    private static PropertyBuilder<T> Varchar<T>(PropertyBuilder<T> property, int tamanho) =>
        property.HasColumnType($"varchar({tamanho})").HasMaxLength(tamanho).IsUnicode(false);
}
