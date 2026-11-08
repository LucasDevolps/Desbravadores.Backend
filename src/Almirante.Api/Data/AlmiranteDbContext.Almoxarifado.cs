using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

/// <summary>
/// Patrimônio e lista de desejos do almoxarifado (issue #126). Tabelas com trigger de histórico não usam OUTPUT no SQL gerado pelo EF.
/// Os índices únicos filtrados são a última barreira contra operações simultâneas (dois empréstimos abertos do mesmo bem, um bem
/// vinculado a dois atendimentos, duas designações vigentes do mesmo membro, a mesma Idempotency-Key duas vezes).
/// </summary>
public partial class AlmiranteDbContext
{
    public DbSet<BemPatrimonial> BensPatrimoniais => Set<BemPatrimonial>();
    public DbSet<EmprestimoBem> EmprestimosBens => Set<EmprestimoBem>();
    public DbSet<ManutencaoBem> ManutencoesBens => Set<ManutencaoBem>();
    public DbSet<InventarioPatrimonio> InventariosPatrimonio => Set<InventarioPatrimonio>();
    public DbSet<InventarioItem> InventarioItens => Set<InventarioItem>();
    public DbSet<DesejoAlmoxarifado> DesejosAlmoxarifado => Set<DesejoAlmoxarifado>();
    public DbSet<AtendimentoDesejo> AtendimentosDesejos => Set<AtendimentoDesejo>();
    public DbSet<AtendimentoBem> AtendimentosBens => Set<AtendimentoBem>();
    public DbSet<ResponsavelAlmoxarifado> ResponsaveisAlmoxarifado => Set<ResponsavelAlmoxarifado>();
    public DbSet<HistoricoAlmoxarifado> HistoricoAlmoxarifado => Set<HistoricoAlmoxarifado>();

    private static string Valores<T>() where T : struct, Enum => string.Join(", ", Enum.GetValues<T>().Select(v => Convert.ToInt32(v)));

    private static void ConfigurarAlmoxarifado(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BemPatrimonial>(entity =>
        {
            entity.ToTable("patrimonio_bens", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_patrimonio_bens_Conservacao", $"[EstadoConservacao] IN ({Valores<EstadoConservacao>()})");
                table.HasCheckConstraint("CK_patrimonio_bens_Situacao", $"[Situacao] IN ({Valores<SituacaoBem>()})");
                table.HasCheckConstraint("CK_patrimonio_bens_Origem",
                    $"[Origem] IN ({Valores<OrigemBem>()}) AND ([Origem] <> {(int)OrigemBem.Outro} OR [OrigemDetalhe] IS NOT NULL)");
                table.HasCheckConstraint("CK_patrimonio_bens_ValorEstimado",
                    "([PossuiValorComercial] = 1 AND [ValorEstimado] IS NOT NULL AND [ValorEstimado] >= 0)" +
                    " OR ([PossuiValorComercial] = 0 AND [ValorEstimado] IS NULL)");
                table.HasCheckConstraint("CK_patrimonio_bens_ValorAquisicao", "[ValorAquisicao] IS NULL OR [ValorAquisicao] >= 0");
                table.HasCheckConstraint("CK_patrimonio_bens_DataAquisicao",
                    "([DataAquisicaoDesconhecida] = 0 AND [DataAquisicao] IS NOT NULL AND [JustificativaDataDesconhecida] IS NULL)" +
                    " OR ([DataAquisicaoDesconhecida] = 1 AND [DataAquisicao] IS NULL AND [JustificativaDataDesconhecida] IS NOT NULL)");
                table.HasCheckConstraint("CK_patrimonio_bens_Baixa",
                    $"([Situacao] = {(int)SituacaoBem.Baixado} AND [DataBaixa] IS NOT NULL AND [MotivoBaixa] IN ({Valores<MotivoBaixa>()})" +
                    " AND [JustificativaBaixa] IS NOT NULL AND [BaixadoPorUsuarioId] IS NOT NULL AND [BaixadoEmUtc] IS NOT NULL)" +
                    $" OR ([Situacao] <> {(int)SituacaoBem.Baixado} AND [DataBaixa] IS NULL AND [MotivoBaixa] IS NULL AND [JustificativaBaixa] IS NULL" +
                    " AND [BaixadoPorUsuarioId] IS NULL AND [BaixadoEmUtc] IS NULL)");
                table.HasCheckConstraint("CK_patrimonio_bens_Exclusao",
                    "([Excluido] = 1 AND [MotivoExclusao] IS NOT NULL AND [ExcluidoPorUsuarioId] IS NOT NULL AND [ExcluidoEmUtc] IS NOT NULL)" +
                    " OR ([Excluido] = 0 AND [MotivoExclusao] IS NULL AND [ExcluidoPorUsuarioId] IS NULL AND [ExcluidoEmUtc] IS NULL)");
            });
            entity.HasKey(b => b.Id);
            entity.Property(b => b.NumeroTombo).UseIdentityColumn();
            entity.Property(b => b.Tombo).HasColumnType($"varchar({RegrasAlmoxarifado.TomboMaximo})").IsUnicode(false)
                .HasComputedColumnSql(RegrasAlmoxarifado.TomboSql, stored: true);
            Varchar(entity.Property(b => b.Nome), RegrasAlmoxarifado.NomeMaximo).IsRequired();
            Varchar(entity.Property(b => b.Descricao), RegrasAlmoxarifado.DescricaoMaximo).IsRequired();
            Varchar(entity.Property(b => b.Categoria), RegrasAlmoxarifado.CategoriaMaximo);
            entity.Property(b => b.EstadoConservacao).HasConversion<int>();
            entity.Property(b => b.ValorEstimado).HasColumnType("decimal(18,2)");
            entity.Property(b => b.DataAquisicao).HasColumnType("date");
            Varchar(entity.Property(b => b.JustificativaDataDesconhecida), RegrasAlmoxarifado.OrigemDetalheMaximo);
            entity.Property(b => b.Origem).HasConversion<int>();
            Varchar(entity.Property(b => b.OrigemDetalhe), RegrasAlmoxarifado.OrigemDetalheMaximo);
            Varchar(entity.Property(b => b.Localizacao), RegrasAlmoxarifado.LocalizacaoMaximo).IsRequired();
            Varchar(entity.Property(b => b.Marca), RegrasAlmoxarifado.MarcaModeloSerieMaximo);
            Varchar(entity.Property(b => b.Modelo), RegrasAlmoxarifado.MarcaModeloSerieMaximo);
            Varchar(entity.Property(b => b.NumeroSerie), RegrasAlmoxarifado.MarcaModeloSerieMaximo);
            entity.Property(b => b.ValorAquisicao).HasColumnType("decimal(18,2)");
            Varchar(entity.Property(b => b.Observacoes), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Property(b => b.Situacao).HasConversion<int>();
            entity.Property(b => b.DataBaixa).HasColumnType("date");
            entity.Property(b => b.MotivoBaixa).HasConversion<int?>();
            Varchar(entity.Property(b => b.JustificativaBaixa), RegrasAlmoxarifado.JustificativaMaximo);
            Varchar(entity.Property(b => b.MotivoExclusao), RegrasAlmoxarifado.JustificativaMaximo);
            entity.Property(b => b.Versao).IsRowVersion();

            entity.HasIndex(b => b.NumeroTombo).IsUnique();
            entity.HasIndex(b => b.Tombo).IsUnique();
            entity.HasIndex(b => new { b.Excluido, b.Situacao, b.Nome });
            entity.HasIndex(b => b.Categoria);
            entity.HasIndex(b => b.ResponsavelGuardaId);

            entity.HasOne<Usuario>().WithMany().HasForeignKey(b => b.ResponsavelGuardaId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(b => b.BaixadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(b => b.ExcluidoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(b => b.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(b => b.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EmprestimoBem>(entity =>
        {
            entity.ToTable("patrimonio_emprestimos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_patrimonio_emprestimos_Previsao", "[PrevisaoDevolucao] >= [DataRetirada]");
                table.HasCheckConstraint("CK_patrimonio_emprestimos_ConservacaoSaida",
                    $"[ConservacaoSaida] IN ({Valores<EstadoConservacao>()}) AND [ConservacaoSaida] <> {(int)EstadoConservacao.Inservivel}");
                table.HasCheckConstraint("CK_patrimonio_emprestimos_Encerramento",
                    "([Encerramento] IS NULL AND [DataEncerramento] IS NULL AND [ConservacaoRetorno] IS NULL AND [ObservacoesRetorno] IS NULL" +
                    " AND [EncerramentoRegistradoPorUsuarioId] IS NULL AND [EncerramentoRegistradoEmUtc] IS NULL)" +
                    $" OR ([Encerramento] IN ({Valores<EncerramentoEmprestimo>()}) AND [DataEncerramento] >= [DataRetirada]" +
                    " AND [EncerramentoRegistradoPorUsuarioId] IS NOT NULL AND [EncerramentoRegistradoEmUtc] IS NOT NULL" +
                    $" AND ([Encerramento] <> {(int)EncerramentoEmprestimo.Devolvido} OR [ConservacaoRetorno] IN ({Valores<EstadoConservacao>()})))");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DataRetirada).HasColumnType("date");
            entity.Property(e => e.PrevisaoDevolucao).HasColumnType("date");
            entity.Property(e => e.ConservacaoSaida).HasConversion<int>();
            Varchar(entity.Property(e => e.ObservacoesSaida), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Property(e => e.Encerramento).HasConversion<int?>();
            entity.Property(e => e.DataEncerramento).HasColumnType("date");
            entity.Property(e => e.ConservacaoRetorno).HasConversion<int?>();
            Varchar(entity.Property(e => e.ObservacoesRetorno), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Ignore(e => e.Aberto);

            entity.HasIndex(e => e.BemId).IsUnique().HasDatabaseName("UX_patrimonio_emprestimos_bem_aberto").HasFilter("[Encerramento] IS NULL");
            entity.HasIndex(e => new { e.BemId, e.DataRetirada }).HasDatabaseName("IX_patrimonio_emprestimos_Bem_Retirada");
            entity.HasIndex(e => new { e.Encerramento, e.PrevisaoDevolucao });
            entity.HasIndex(e => e.MembroId);

            entity.HasOne<BemPatrimonial>().WithMany().HasForeignKey(e => e.BemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.MembroId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.RegistradoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.EncerramentoRegistradoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManutencaoBem>(entity =>
        {
            entity.ToTable("patrimonio_manutencoes", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_patrimonio_manutencoes_Saida",
                    "([DataSaida] IS NULL AND [ConservacaoSaida] IS NULL AND [ObservacoesSaida] IS NULL AND [SaidaRegistradaPorUsuarioId] IS NULL" +
                    " AND [SaidaRegistradaEmUtc] IS NULL)" +
                    $" OR ([DataSaida] >= [DataEntrada] AND [ConservacaoSaida] IN ({Valores<EstadoConservacao>()})" +
                    " AND [SaidaRegistradaPorUsuarioId] IS NOT NULL AND [SaidaRegistradaEmUtc] IS NOT NULL)");
            });
            entity.HasKey(m => m.Id);
            entity.Property(m => m.DataEntrada).HasColumnType("date");
            Varchar(entity.Property(m => m.Motivo), RegrasAlmoxarifado.JustificativaMaximo).IsRequired();
            Varchar(entity.Property(m => m.ObservacoesEntrada), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Property(m => m.DataSaida).HasColumnType("date");
            entity.Property(m => m.ConservacaoSaida).HasConversion<int?>();
            Varchar(entity.Property(m => m.ObservacoesSaida), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Ignore(m => m.Aberta);

            entity.HasIndex(m => m.BemId).IsUnique().HasDatabaseName("UX_patrimonio_manutencoes_bem_aberta").HasFilter("[DataSaida] IS NULL");
            entity.HasIndex(m => new { m.BemId, m.DataEntrada }).HasDatabaseName("IX_patrimonio_manutencoes_Bem_Entrada");

            entity.HasOne<BemPatrimonial>().WithMany().HasForeignKey(m => m.BemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(m => m.RegistradaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(m => m.SaidaRegistradaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventarioPatrimonio>(entity =>
        {
            entity.ToTable("patrimonio_inventarios", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_patrimonio_inventarios_Totais",
                    "[TotalBens] >= 0 AND [TotalBensComValorComercial] BETWEEN 0 AND [TotalBens] AND [ValorEstimadoPatrimonial] >= 0");
                table.HasCheckConstraint("CK_patrimonio_inventarios_Votacao",
                    "([DataVotacao] IS NULL AND [ReferenciaAta] IS NULL AND [VotacaoRegistradaPorUsuarioId] IS NULL AND [VotacaoRegistradaEmUtc] IS NULL)" +
                    " OR ([DataVotacao] >= [DataInventario] AND [ReferenciaAta] IS NOT NULL AND [VotacaoRegistradaPorUsuarioId] IS NOT NULL" +
                    " AND [VotacaoRegistradaEmUtc] IS NOT NULL)");
            });
            entity.HasKey(i => i.Id);
            entity.Property(i => i.DataInventario).HasColumnType("date");
            Varchar(entity.Property(i => i.Observacoes), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Property(i => i.ValorEstimadoPatrimonial).HasColumnType("decimal(18,2)");
            entity.Property(i => i.DataVotacao).HasColumnType("date");
            Varchar(entity.Property(i => i.ReferenciaAta), RegrasAlmoxarifado.ReferenciaAtaMaximo);
            entity.Property(i => i.Versao).IsRowVersion();

            entity.HasIndex(i => new { i.DataInventario, i.FechadoEmUtc });
            entity.HasOne<Usuario>().WithMany().HasForeignKey(i => i.ResponsavelId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(i => i.FechadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(i => i.VotacaoRegistradaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventarioItem>(entity =>
        {
            entity.ToTable("patrimonio_inventario_itens", table => table.UseSqlOutputClause(false));
            entity.HasKey(i => i.Id);
            Varchar(entity.Property(i => i.Tombo), RegrasAlmoxarifado.TomboMaximo).IsRequired();
            Varchar(entity.Property(i => i.Nome), RegrasAlmoxarifado.NomeMaximo).IsRequired();
            Varchar(entity.Property(i => i.Descricao), RegrasAlmoxarifado.DescricaoMaximo).IsRequired();
            Varchar(entity.Property(i => i.Categoria), RegrasAlmoxarifado.CategoriaMaximo);
            entity.Property(i => i.EstadoConservacao).HasConversion<int>();
            entity.Property(i => i.Situacao).HasConversion<int>();
            Varchar(entity.Property(i => i.Localizacao), RegrasAlmoxarifado.LocalizacaoMaximo).IsRequired();
            entity.Property(i => i.Origem).HasConversion<int>();
            entity.Property(i => i.DataAquisicao).HasColumnType("date");
            entity.Property(i => i.ValorAquisicao).HasColumnType("decimal(18,2)");
            entity.Property(i => i.ValorEstimado).HasColumnType("decimal(18,2)");

            entity.HasIndex(i => new { i.InventarioId, i.BemId }).IsUnique();
            entity.HasIndex(i => i.BemId);
            entity.HasOne<InventarioPatrimonio>().WithMany().HasForeignKey(i => i.InventarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BemPatrimonial>().WithMany().HasForeignKey(i => i.BemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DesejoAlmoxarifado>(entity =>
        {
            entity.ToTable("almoxarifado_desejos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_almoxarifado_desejos_Quantidades",
                    $"[QuantidadeDesejada] BETWEEN 1 AND {RegrasAlmoxarifado.QuantidadeMaxima} AND [QuantidadeAtendida] BETWEEN 0 AND [QuantidadeDesejada]");
                table.HasCheckConstraint("CK_almoxarifado_desejos_Urgencia", $"[Urgencia] IN ({Valores<NivelUrgenciaAlmoxarifado>()})");
                table.HasCheckConstraint("CK_almoxarifado_desejos_TipoItem", $"[TipoItem] IN ({Valores<TipoItemDesejo>()})");
                table.HasCheckConstraint("CK_almoxarifado_desejos_Custo", "[CustoUnitarioEstimado] IS NULL OR [CustoUnitarioEstimado] >= 0");
                table.HasCheckConstraint("CK_almoxarifado_desejos_Situacao",
                    $"([CanceladoEmUtc] IS NOT NULL AND [Situacao] = {(int)SituacaoDesejo.Cancelada} AND [MotivoCancelamento] IS NOT NULL" +
                    " AND [CanceladoPorUsuarioId] IS NOT NULL)" +
                    " OR ([CanceladoEmUtc] IS NULL AND [MotivoCancelamento] IS NULL AND [CanceladoPorUsuarioId] IS NULL AND [Situacao] =" +
                    $" CASE WHEN [QuantidadeAtendida] = 0 THEN {(int)SituacaoDesejo.Pendente}" +
                    $" WHEN [QuantidadeAtendida] < [QuantidadeDesejada] THEN {(int)SituacaoDesejo.ParcialmenteAtendida}" +
                    $" ELSE {(int)SituacaoDesejo.Atendida} END)");
                table.HasCheckConstraint("CK_almoxarifado_desejos_Exclusao",
                    "([Excluido] = 1 AND [MotivoExclusao] IS NOT NULL AND [ExcluidoPorUsuarioId] IS NOT NULL AND [ExcluidoEmUtc] IS NOT NULL)" +
                    " OR ([Excluido] = 0 AND [MotivoExclusao] IS NULL AND [ExcluidoPorUsuarioId] IS NULL AND [ExcluidoEmUtc] IS NULL)");
            });
            entity.HasKey(d => d.Id);
            Varchar(entity.Property(d => d.Nome), RegrasAlmoxarifado.NomeMaximo).IsRequired();
            Varchar(entity.Property(d => d.Descricao), RegrasAlmoxarifado.DescricaoMaximo).IsRequired();
            Varchar(entity.Property(d => d.Justificativa), RegrasAlmoxarifado.JustificativaDesejoMaximo).IsRequired();
            entity.Property(d => d.TipoItem).HasConversion<int>();
            entity.Property(d => d.Urgencia).HasConversion<int>();
            Varchar(entity.Property(d => d.Categoria), RegrasAlmoxarifado.CategoriaMaximo);
            entity.Property(d => d.CustoUnitarioEstimado).HasColumnType("decimal(18,2)");
            entity.Property(d => d.DataNecessidade).HasColumnType("date");
            Varchar(entity.Property(d => d.LinkReferencia), RegrasAlmoxarifado.LinkMaximo);
            Varchar(entity.Property(d => d.Observacoes), RegrasAlmoxarifado.ObservacoesMaximo);
            entity.Property(d => d.Situacao).HasConversion<int>();
            Varchar(entity.Property(d => d.MotivoCancelamento), RegrasAlmoxarifado.JustificativaMaximo);
            Varchar(entity.Property(d => d.MotivoExclusao), RegrasAlmoxarifado.JustificativaMaximo);
            entity.Property(d => d.Versao).IsRowVersion();

            entity.HasIndex(d => new { d.Excluido, d.Situacao, d.Urgencia, d.CriadoEmUtc }).HasDatabaseName("IX_almoxarifado_desejos_ordem_padrao");
            entity.HasIndex(d => d.Categoria);

            entity.HasOne<Usuario>().WithMany().HasForeignKey(d => d.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(d => d.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(d => d.CanceladoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(d => d.ExcluidoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AtendimentoDesejo>(entity =>
        {
            entity.ToTable("almoxarifado_atendimentos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_almoxarifado_atendimentos_Quantidade", $"[Quantidade] BETWEEN 1 AND {RegrasAlmoxarifado.QuantidadeMaxima}");
                table.HasCheckConstraint("CK_almoxarifado_atendimentos_Origem", $"[Origem] IN ({Valores<OrigemAtendimento>()})");
                table.HasCheckConstraint("CK_almoxarifado_atendimentos_Estorno",
                    "([EstornadoEmUtc] IS NULL AND [MotivoEstorno] IS NULL AND [EstornadoPorUsuarioId] IS NULL)" +
                    " OR ([EstornadoEmUtc] IS NOT NULL AND [MotivoEstorno] IS NOT NULL AND [EstornadoPorUsuarioId] IS NOT NULL)");
            });
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Data).HasColumnType("date");
            entity.Property(a => a.Origem).HasConversion<int>();
            Varchar(entity.Property(a => a.Observacoes), RegrasAlmoxarifado.ObservacoesMaximo);
            Varchar(entity.Property(a => a.IdempotencyKey), RegrasAlmoxarifado.IdempotencyKeyTamanho).IsRequired();
            Varchar(entity.Property(a => a.RequestHash), 64).IsRequired();
            Varchar(entity.Property(a => a.MotivoEstorno), RegrasAlmoxarifado.JustificativaMaximo);
            entity.Ignore(a => a.Estornado);

            entity.HasIndex(a => new { a.RegistradoPorUsuarioId, a.IdempotencyKey }).IsUnique().HasDatabaseName("UX_almoxarifado_atendimentos_idempotencia");
            entity.HasIndex(a => new { a.DesejoId, a.Data });

            entity.HasOne<DesejoAlmoxarifado>().WithMany().HasForeignKey(a => a.DesejoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(a => a.ResponsavelId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(a => a.RegistradoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(a => a.EstornadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AtendimentoBem>(entity =>
        {
            entity.ToTable("almoxarifado_atendimentos_bens", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_almoxarifado_atendimentos_bens_Vinculo",
                    "([Ativo] = 1 AND [DesvinculadoPorUsuarioId] IS NULL AND [DesvinculadoEmUtc] IS NULL)" +
                    " OR ([Ativo] = 0 AND [DesvinculadoPorUsuarioId] IS NOT NULL AND [DesvinculadoEmUtc] IS NOT NULL)");
            });
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => v.BemId).IsUnique().HasDatabaseName("UX_almoxarifado_atendimentos_bens_bem_ativo").HasFilter("[Ativo] = 1");
            entity.HasIndex(v => v.AtendimentoId);
            entity.HasOne<AtendimentoDesejo>().WithMany().HasForeignKey(v => v.AtendimentoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BemPatrimonial>().WithMany().HasForeignKey(v => v.BemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(v => v.DesvinculadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ResponsavelAlmoxarifado>(entity =>
        {
            entity.ToTable("almoxarifado_responsaveis", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_almoxarifado_responsaveis_Revogacao",
                    "([RevogadoEmUtc] IS NULL AND [MotivoRevogacao] IS NULL AND [RevogadoPorUsuarioId] IS NULL)" +
                    " OR ([RevogadoEmUtc] IS NOT NULL AND [MotivoRevogacao] IS NOT NULL AND [RevogadoPorUsuarioId] IS NOT NULL)");
            });
            entity.HasKey(r => r.Id);
            entity.Property(r => r.DataInicio).HasColumnType("date");
            Varchar(entity.Property(r => r.Observacoes), RegrasAlmoxarifado.ObservacoesMaximo);
            Varchar(entity.Property(r => r.MotivoRevogacao), RegrasAlmoxarifado.JustificativaMaximo);
            entity.Property(r => r.Versao).IsRowVersion();
            entity.Ignore(r => r.Vigente);

            entity.HasIndex(r => r.MembroId).IsUnique().HasDatabaseName("UX_almoxarifado_responsaveis_membro_vigente").HasFilter("[RevogadoEmUtc] IS NULL");
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.MembroId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.DesignadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(r => r.RevogadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HistoricoAlmoxarifado>(entity =>
        {
            entity.ToTable("almoxarifado_historico", table =>
            {
                table.HasCheckConstraint("CK_almoxarifado_historico_EstadoAnteriorJson", "[EstadoAnteriorJson] IS NULL OR ISJSON([EstadoAnteriorJson]) = 1");
                table.HasCheckConstraint("CK_almoxarifado_historico_EstadoNovoJson", "ISJSON([EstadoNovoJson]) = 1");
            });
            entity.HasKey(h => h.Id);
            Varchar(entity.Property(h => h.Tabela), RegrasAlmoxarifado.AcaoMaximo).IsRequired();
            Varchar(entity.Property(h => h.Acao), RegrasAlmoxarifado.AcaoMaximo);
            Varchar(entity.Property(h => h.Motivo), RegrasAlmoxarifado.JustificativaMaximo);
            entity.Property(h => h.EstadoAnteriorJson).HasColumnType("varchar(max)").IsUnicode(false);
            entity.Property(h => h.EstadoNovoJson).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            Varchar(entity.Property(h => h.IpResponsavel), 45).IsRequired();
            entity.HasIndex(h => new { h.BemId, h.OcorridoEmUtc });
            entity.HasIndex(h => new { h.DesejoId, h.OcorridoEmUtc });
            entity.HasIndex(h => new { h.Tabela, h.RegistroId });
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
