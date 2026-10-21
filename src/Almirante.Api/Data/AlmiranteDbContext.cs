using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Data;

public class AlmiranteDbContext(DbContextOptions<AlmiranteDbContext> options) : DbContext(options)
{
    public const string CheckFotoCaminho =
        "[FotoCaminho] IS NULL OR (DATALENGTH([FotoCaminho]) = 79" +
        " AND [FotoCaminho] COLLATE Latin1_General_BIN2 LIKE 'usuarios/' + LOWER(REPLACE(CONVERT(char(36), [Id]), '-', '')) + '/%.webp'" +
        " AND SUBSTRING([FotoCaminho], 43, 32) COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9a-f]%')";

    public const string CheckTipoSanguineo =
        "[TipoSanguineo] IS NULL OR [TipoSanguineo] COLLATE Latin1_General_BIN2 IN ('A+', 'A-', 'B+', 'B-', 'AB+', 'AB-', 'O+', 'O-')";

    /// <summary>Colunas varchar do endereço, iguais em Usuarios e _usuarios_hist (EnderecoCep primeiro).</summary>
    public static readonly (string Coluna, int Tamanho)[] ColunasEndereco =
    [
        (nameof(Usuario.EnderecoCep), Validation.Usuarios.Cep.Digitos),
        (nameof(Usuario.EnderecoLogradouro), Validation.Usuarios.Endereco.LogradouroMaximo),
        (nameof(Usuario.EnderecoNumero), Validation.Usuarios.Endereco.NumeroMaximo),
        (nameof(Usuario.EnderecoComplemento), Validation.Usuarios.Endereco.ComplementoMaximo),
        (nameof(Usuario.EnderecoComplementoCep), Validation.Usuarios.Endereco.ComplementoCepMaximo),
        (nameof(Usuario.EnderecoUnidade), Validation.Usuarios.Endereco.UnidadeMaximo),
        (nameof(Usuario.EnderecoBairro), Validation.Usuarios.Endereco.BairroMaximo),
        (nameof(Usuario.EnderecoLocalidade), Validation.Usuarios.Endereco.LocalidadeMaximo),
        (nameof(Usuario.EnderecoUf), Validation.Usuarios.Endereco.UfTamanho),
        (nameof(Usuario.EnderecoEstado), Validation.Usuarios.Endereco.EstadoMaximo),
        (nameof(Usuario.EnderecoRegiao), Validation.Usuarios.Endereco.RegiaoMaximo),
        (nameof(Usuario.EnderecoIbge), Validation.Usuarios.Endereco.IbgeMaximo),
        (nameof(Usuario.EnderecoGia), Validation.Usuarios.Endereco.GiaMaximo),
        (nameof(Usuario.EnderecoDdd), Validation.Usuarios.Endereco.DddMaximo),
        (nameof(Usuario.EnderecoSiafi), Validation.Usuarios.Endereco.SiafiMaximo),
    ];

    /// <summary>Sem CEP, nenhum campo de endereço; com CEP, 8 dígitos, cidade e UF (duas letras maiúsculas).</summary>
    public static readonly string CheckEndereco =
        "(" + string.Join(" AND ", ColunasEndereco.Select(c => $"[{c.Coluna}] IS NULL")) + ")" +
        " OR ([EnderecoCep] IS NOT NULL AND DATALENGTH([EnderecoCep]) = 8 AND [EnderecoCep] COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9]%'" +
        " AND [EnderecoLocalidade] IS NOT NULL AND [EnderecoUf] IS NOT NULL AND [EnderecoUf] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z]')";

    /// <summary>
    /// Sem pagamento registrado, nenhum campo de pagamento (vale para os "Pago" históricos);
    /// com pagamento, status Pago, data, 0 &lt; ValorPago &lt;= Valor e o responsável pelo registro.
    /// </summary>
    public const string CheckPagamento =
        "([DataPagamento] IS NULL AND [ValorPago] IS NULL AND [PagamentoRegistradoPorUsuarioId] IS NULL AND [PagamentoRegistradoEmUtc] IS NULL)" +
        " OR ([Status] = 1 AND [DataPagamento] IS NOT NULL AND [ValorPago] > 0 AND [ValorPago] <= [Valor]" +
        " AND [PagamentoRegistradoPorUsuarioId] IS NOT NULL AND [PagamentoRegistradoEmUtc] IS NOT NULL)";

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<Cargo> Cargos => Set<Cargo>();
    public DbSet<LancamentoOperacao> LancamentosOperacoes => Set<LancamentoOperacao>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Evento> Eventos => Set<Evento>();
    public DbSet<EventoMembro> EventosMembros => Set<EventoMembro>();
    public DbSet<EventoOperacao> EventosOperacoes => Set<EventoOperacao>();

    public DbSet<LancamentoDeletado> LancamentosDeletados => Set<LancamentoDeletado>();

    public DbSet<HistoricoEvento> HistoricoEventos => Set<HistoricoEvento>();

    public DbSet<UsuarioHistorico> UsuariosHistorico => Set<UsuarioHistorico>();

    public DbSet<Notificacao> Notificacoes => Set<Notificacao>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Usuario>(entity =>
        {
            entity.ToTable("Usuarios", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_Usuarios_FotoCaminho", CheckFotoCaminho);
                table.HasCheckConstraint("CK_Usuarios_TipoSanguineo", CheckTipoSanguineo);
                table.HasCheckConstraint("CK_Usuarios_Endereco", CheckEndereco);
            });
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Nome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(u => u.Email).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false).IsRequired();
            entity.Property(u => u.EmailNormalizado).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false).IsRequired();
            entity.Property(u => u.Cpf).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(u => u.DataNascimento).HasColumnType("date");
            entity.Property(u => u.Telefone).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(u => u.ResponsavelNome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false);
            entity.Property(u => u.ResponsavelEmail).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false);
            entity.Property(u => u.ResponsavelCpf).HasColumnType("varchar(11)").HasMaxLength(11).IsUnicode(false);
            entity.Property(u => u.ResponsavelTelefone).HasColumnType("varchar(11)").HasMaxLength(11).IsUnicode(false);
            entity.Property(u => u.FotoCaminho).HasColumnType("varchar(79)").HasMaxLength(79).IsUnicode(false);
            entity.Property(u => u.Rg).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(u => u.TipoSanguineo).HasColumnType("varchar(3)").HasMaxLength(3).IsUnicode(false);
            entity.Property(u => u.DoencasAlergias).HasColumnType("varchar(2000)").HasMaxLength(2000).IsUnicode(false);
            entity.Property(u => u.EhCin).IsRequired();
            entity.Property(u => u.DataExpiracao).HasColumnType("date");
            foreach (var (coluna, tamanho) in ColunasEndereco)
            {
                entity.Property<string?>(coluna).HasColumnType($"varchar({tamanho})").HasMaxLength(tamanho).IsUnicode(false);
            }
            entity.Property(u => u.SenhaHash).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            entity.Property(u => u.SecurityVersion).IsConcurrencyToken();
            entity.HasIndex(u => u.EmailNormalizado).IsUnique();
            entity.HasIndex(u => u.Cpf)
                .IsUnique()
                .HasDatabaseName("UX_Usuarios_Cpf")
                .HasFilter("[Cpf] IS NOT NULL");
            entity.Property(u => u.Ativo).IsRequired();

            entity.HasOne(u => u.Cargo)
                .WithMany()
                .HasForeignKey(u => u.CargoId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("AuthSessions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RevocationReason).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false);
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
            entity.ToTable("Lancamentos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_Lancamentos_Categoria", "[Categoria] IN (0, 1)");
                table.HasCheckConstraint("CK_Lancamentos_Status", "[Status] IN (0, 1, 2)");
                table.HasCheckConstraint("CK_Lancamentos_TipoFluxo", "[TipoFluxo] IN (0, 1)");
                table.HasCheckConstraint("CK_Lancamentos_Pagamento", CheckPagamento);
            });
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Finalidade).HasColumnType("varchar(50)").HasMaxLength(50).IsUnicode(false);
            entity.Property(l => l.Descricao).HasColumnType("varchar(500)").HasMaxLength(500).IsUnicode(false);
            entity.Property(l => l.Categoria).HasConversion<int>().IsRequired();
            entity.Property(l => l.TipoFluxo).HasConversion<int>().IsRequired();
            entity.Property(l => l.Valor).HasColumnType("decimal(18,2)").IsConcurrencyToken();
            entity.Property(l => l.Status).HasConversion<int>().IsRequired().IsConcurrencyToken();
            entity.Property(l => l.Ativo).IsRequired();
            entity.Property(l => l.DataPagamento).HasColumnType("date").IsConcurrencyToken();
            entity.Property(l => l.ValorPago).HasColumnType("decimal(18,2)").IsConcurrencyToken();

            entity.HasIndex(l => l.DataPagamento)
                .HasFilter("[DataPagamento] IS NOT NULL")
                .IncludeProperties(l => new { l.TipoFluxo, l.ValorPago, l.Status, l.Ativo });

            entity.HasIndex(l => l.Status);
            entity.HasIndex(l => l.Finalidade);
            entity.HasIndex(l => l.Vencimento);
            entity.HasIndex(l => l.MembroId);
            entity.HasIndex(l => l.Ativo);
            entity.HasIndex(l => l.OperacaoId);
            entity.HasIndex(l => l.EventoId);
            entity.HasOne<Evento>().WithMany().HasForeignKey(l => l.EventoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(l => l.Membro).WithMany(u => u.Lancamentos).HasForeignKey(l => l.MembroId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(l => l.AtualizadoPorUsuarioId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(l => l.PagamentoRegistradoPorUsuarioId)
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
            entity.Property(o => o.IdempotencyKey).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false).IsRequired();
            entity.Property(o => o.RequestHash).HasColumnType("varchar(64)").HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.Property(o => o.Finalidade).HasColumnType("varchar(50)").HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(o => o.Descricao).HasColumnType("varchar(500)").HasMaxLength(500).IsUnicode(false);
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
            entity.Property(d => d.IpResponsavel).HasColumnType("varchar(45)").HasMaxLength(45).IsUnicode(false).IsRequired();
            entity.Property(d => d.Motivo).HasColumnType("varchar(255)").HasMaxLength(255).IsUnicode(false).IsRequired();
            entity.Property(d => d.Finalidade).HasColumnType("varchar(50)").HasMaxLength(50).IsUnicode(false);
            entity.Property(d => d.Categoria).HasConversion<int>().IsRequired();
            entity.Property(d => d.TipoFluxo).HasConversion<int>().IsRequired();
            entity.Property(d => d.Valor).HasColumnType("decimal(18,2)");
            entity.Property(d => d.Status).HasConversion<int>().IsRequired();
            entity.Property(d => d.DataPagamento).HasColumnType("date");
            entity.Property(d => d.ValorPago).HasColumnType("decimal(18,2)");

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

        modelBuilder.Entity<Evento>(entity =>
        {
            entity.ToTable("eventos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_eventos_valores_nao_negativos",
                    "[TransporteValor] >= 0 AND [AlimentacaoValor] >= 0 AND [SeguroObrigatorio] >= 0 AND [ValorPorMembro] >= 0");
                table.HasCheckConstraint("CK_eventos_valores_normalizados",
                    "([TransporteEhGratis] = 0 OR [TransporteValor] = 0) AND ([AlimentacaoIndividual] = 0 OR [AlimentacaoValor] = 0) " +
                    "AND [ValorPorMembro] = [TransporteValor] + [AlimentacaoValor] + [SeguroObrigatorio]");
                table.HasCheckConstraint("CK_eventos_grupo", "[EventoReferenciaId] IS NOT NULL OR [EventoGrupoId] = [Id]");
            });
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.EventoGrupoId }).HasName("AK_eventos_Id_EventoGrupoId");
            entity.Property(e => e.Local).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(e => e.DataEvento).HasColumnType("date");
            entity.Property(e => e.TransporteValor).HasColumnType("decimal(18,2)");
            entity.Property(e => e.AlimentacaoValor).HasColumnType("decimal(18,2)");
            entity.Property(e => e.SeguroObrigatorio).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ValorPorMembro).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Versao).IsRowVersion();

            entity.HasIndex(e => new { e.Ativo, e.DataEvento });
            entity.HasIndex(e => e.EventoGrupoId);
            entity.HasIndex(e => e.EventoReferenciaId);

            entity.HasOne<Evento>().WithMany().HasForeignKey(e => e.EventoReferenciaId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.CriadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(e => e.AtualizadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventoMembro>(entity =>
        {
            entity.ToTable("evento_membros");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.EventoId);
            entity.HasIndex(m => m.MembroId);
            entity.HasIndex(m => m.LancamentoId);

            entity.HasIndex(m => new { m.EventoGrupoId, m.MembroId })
                .IsUnique()
                .HasDatabaseName("UX_evento_membros_grupo_membro_ativo")
                .HasFilter("[Ativo] = 1");

            entity.HasOne(m => m.Evento).WithMany(e => e.Membros)
                .HasForeignKey(m => new { m.EventoId, m.EventoGrupoId })
                .HasPrincipalKey(e => new { e.Id, e.EventoGrupoId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(m => m.MembroId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Lancamento>().WithMany().HasForeignKey(m => m.LancamentoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventoOperacao>(entity =>
        {
            entity.ToTable("eventos_operacoes", table =>
                table.HasCheckConstraint("CK_eventos_operacoes_RespostaJson", "[RespostaJson] IS NULL OR ISJSON([RespostaJson]) = 1"));
            entity.HasKey(o => o.Id);
            entity.Property(o => o.IdempotencyKey).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false).IsRequired();
            entity.Property(o => o.RequestHash).HasColumnType("varchar(64)").HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.Property(o => o.RespostaJson).HasColumnType("varchar(max)").IsUnicode(false);
            entity.HasIndex(o => new { o.UsuarioId, o.IdempotencyKey }).IsUnique();
            entity.HasIndex(o => o.EventoId);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(o => o.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Evento>().WithMany().HasForeignKey(o => o.EventoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HistoricoEvento>(entity =>
        {
            entity.ToTable("historico_eventos", table =>
                table.HasCheckConstraint("CK_historico_eventos_ParticipantesJson", "ISJSON([ParticipantesJson]) = 1"));
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Local).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(h => h.DataEvento).HasColumnType("date");
            entity.Property(h => h.TransporteValor).HasColumnType("decimal(18,2)");
            entity.Property(h => h.AlimentacaoValor).HasColumnType("decimal(18,2)");
            entity.Property(h => h.SeguroObrigatorio).HasColumnType("decimal(18,2)");
            entity.Property(h => h.ValorPorMembro).HasColumnType("decimal(18,2)");
            entity.Property(h => h.Total).HasColumnType("decimal(18,2)");
            entity.Property(h => h.ParticipantesJson).HasColumnType("varchar(max)").IsUnicode(false).IsRequired();
            entity.Property(h => h.IpResponsavel).HasColumnType("varchar(45)").IsRequired();
            entity.Property(h => h.Motivo).HasColumnType("varchar(255)").HasMaxLength(255).IsUnicode(false).IsRequired();
            entity.HasIndex(h => h.EventoId);
            entity.HasOne<Evento>().WithMany().HasForeignKey(h => h.EventoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UsuarioHistorico>(entity =>
        {
            entity.ToTable("_usuarios_hist", table =>
                table.HasCheckConstraint("CK__usuarios_hist_TipoOperacao", "[TipoOperacao] IN ('UPDATE', 'DELETE')"));
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Nome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(h => h.Email).HasColumnType("varchar(256)").HasMaxLength(256).IsUnicode(false).IsRequired();
            entity.Property(h => h.Cpf).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(h => h.DataNascimento).HasColumnType("date");
            entity.Property(h => h.Telefone).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(h => h.ResponsavelNome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false);
            entity.Property(h => h.ResponsavelEmail).HasColumnType("varchar(100)").HasMaxLength(100).IsUnicode(false);
            entity.Property(h => h.ResponsavelCpf).HasColumnType("varchar(11)").HasMaxLength(11).IsUnicode(false);
            entity.Property(h => h.ResponsavelTelefone).HasColumnType("varchar(11)").HasMaxLength(11).IsUnicode(false);
            entity.Property(h => h.FotoCaminho).HasColumnType("varchar(79)").HasMaxLength(79).IsUnicode(false);
            entity.Property(h => h.Rg).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false);
            entity.Property(h => h.TipoSanguineo).HasColumnType("varchar(3)").HasMaxLength(3).IsUnicode(false);
            entity.Property(h => h.DoencasAlergias).HasColumnType("varchar(2000)").HasMaxLength(2000).IsUnicode(false);
            entity.Property(h => h.DataExpiracao).HasColumnType("date");
            foreach (var (coluna, tamanho) in ColunasEndereco)
            {
                entity.Property<string?>(coluna).HasColumnType($"varchar({tamanho})").HasMaxLength(tamanho).IsUnicode(false);
            }
            entity.Property(h => h.CargoNome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(h => h.TipoOperacao).HasColumnType("varchar(6)").IsRequired();
            entity.Property(h => h.UsuarioResponsavelLogin).HasColumnType("varchar(256)").HasMaxLength(256).IsUnicode(false).IsRequired();
            entity.Property(h => h.IpResponsavel).HasColumnType("varchar(45)").IsRequired();
            entity.HasIndex(h => h.UsuarioId);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notificacao>(entity =>
        {
            entity.ToTable("notificacoes", table =>
            {
                table.HasCheckConstraint("CK_notificacoes_Tipo", "[Tipo] IN (1, 2)");
                table.HasCheckConstraint("CK_notificacoes_Aviso",
                    "([AvisoPendente] IS NULL AND [AvisoProximaTentativaEmUtc] IS NULL)" +
                    " OR ([AvisoPendente] IN (1, 2) AND [AvisoProximaTentativaEmUtc] IS NOT NULL)");
                table.HasCheckConstraint("CK_notificacoes_AvisoTentativas", "[AvisoTentativas] >= 0");
            });
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Tipo).HasConversion<int>().IsRequired();
            entity.Property(n => n.Titulo).HasColumnType($"varchar({NotificacoesFabrica.TituloMaximo})")
                .HasMaxLength(NotificacoesFabrica.TituloMaximo).IsUnicode(false).IsRequired();
            entity.Property(n => n.Mensagem).HasColumnType($"varchar({NotificacoesFabrica.MensagemMaximo})")
                .HasMaxLength(NotificacoesFabrica.MensagemMaximo).IsUnicode(false).IsRequired();
            entity.Property(n => n.ChaveOrigem).HasColumnType($"varchar({NotificacoesFabrica.ChaveOrigemMaximo})")
                .HasMaxLength(NotificacoesFabrica.ChaveOrigemMaximo).IsUnicode(false).IsRequired();
            entity.Property(n => n.CriadaEmUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.Property(n => n.LidaEmUtc).IsConcurrencyToken()
                .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
            entity.Property(n => n.AvisoPendente).HasConversion<int?>();

            entity.HasIndex(n => new { n.DestinatarioId, n.ChaveOrigem })
                .IsUnique()
                .HasDatabaseName("UX_notificacoes_destinatario_origem");
            entity.HasIndex(n => new { n.DestinatarioId, n.CriadaEmUtc, n.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("IX_notificacoes_pendentes")
                .HasFilter("[LidaEmUtc] IS NULL");
            entity.HasIndex(n => n.AvisoProximaTentativaEmUtc)
                .HasDatabaseName("IX_notificacoes_aviso_pendente")
                .HasFilter("[AvisoPendente] IS NOT NULL")
                .IncludeProperties(n => new { n.AvisoPendente, n.AvisoTentativas });
            entity.HasIndex(n => n.EventoId);
            entity.HasIndex(n => n.LancamentoId);

            entity.HasOne<Usuario>().WithMany().HasForeignKey(n => n.DestinatarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Evento>().WithMany().HasForeignKey(n => n.EventoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Lancamento>().WithMany().HasForeignKey(n => n.LancamentoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Cargo>(entity =>
        {
            entity.ToTable("Cargos");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Nome).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(c => c.Descricao).HasColumnType("varchar(1000)").HasMaxLength(1000).IsUnicode(false).IsRequired();
            entity.Property(c => c.CriadoPor).HasColumnType("varchar(200)").HasMaxLength(200).IsUnicode(false).IsRequired();
            entity.Property(c => c.Role).HasColumnType("varchar(20)").HasMaxLength(20).IsUnicode(false).IsRequired();
            entity.HasIndex(c => c.Role).IsUnique();
        });
    }
}
