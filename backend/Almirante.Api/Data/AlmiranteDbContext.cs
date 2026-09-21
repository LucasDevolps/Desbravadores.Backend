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
    public DbSet<Evento> Eventos => Set<Evento>();
    public DbSet<EventoMembro> EventosMembros => Set<EventoMembro>();
    public DbSet<EventoOperacao> EventosOperacoes => Set<EventoOperacao>();

    // Somente leitura pela aplicação: linhas são inseridas exclusivamente pelo trigger de
    // auditoria (ver Entities/LancamentoDeletado.cs).
    public DbSet<LancamentoDeletado> LancamentosDeletados => Set<LancamentoDeletado>();

    // Idem: historico_eventos só é escrito pelo trigger de exclusão lógica de eventos.
    public DbSet<HistoricoEvento> HistoricoEventos => Set<HistoricoEvento>();

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
            // Token de concorrência: todo UPDATE de Usuarios (login, rehash, reset de senha) filtra por esta
            // versão, então um login/rehash baseado em credenciais anteriores a um reset falha em vez de
            // persistir uma sessão ou restaurar o hash antigo (ver AuthService.LoginAsync).
            entity.Property(u => u.SecurityVersion).IsConcurrencyToken();
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
            entity.Property(l => l.Finalidade).HasMaxLength(50);
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
            entity.HasIndex(l => l.EventoId);
            entity.HasOne<Evento>().WithMany().HasForeignKey(l => l.EventoId).OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(d => d.Finalidade).HasMaxLength(50);
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

        modelBuilder.Entity<Evento>(entity =>
        {
            // Trigger AFTER UPDATE (TR_eventos_AuditoriaExclusaoLogica): mesmo motivo de Lancamentos, o EF
            // não pode usar OUTPUT nesta tabela (também traz a rowversion por SELECT).
            entity.ToTable("eventos", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("CK_eventos_valores_nao_negativos",
                    "[TransporteValor] >= 0 AND [AlimentacaoValor] >= 0 AND [SeguroObrigatorio] >= 0 AND [ValorPorMembro] >= 0");
                // Componentes ignorados pelos booleanos são sempre zero e o valor por membro é a soma exata.
                table.HasCheckConstraint("CK_eventos_valores_normalizados",
                    "([TransporteEhGratis] = 0 OR [TransporteValor] = 0) AND ([AlimentacaoIndividual] = 0 OR [AlimentacaoValor] = 0) " +
                    "AND [ValorPorMembro] = [TransporteValor] + [AlimentacaoValor] + [SeguroObrigatorio]");
                table.HasCheckConstraint("CK_eventos_grupo", "[EventoReferenciaId] IS NOT NULL OR [EventoGrupoId] = [Id]");
            });
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.EventoGrupoId }).HasName("AK_eventos_Id_EventoGrupoId");
            entity.Property(e => e.Local).HasMaxLength(200).IsRequired();
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

            // Proteção do banco: um membro ATIVO por EventoGrupoId, mesmo entre cadastros de preços
            // diferentes. Duas requisições simultâneas não cobram a mesma pessoa duas vezes: a segunda
            // falha na constraint (a aplicação a traduz em 409). Linhas desativadas ficam de fora.
            entity.HasIndex(m => new { m.EventoGrupoId, m.MembroId })
                .IsUnique()
                .HasDatabaseName("UX_evento_membros_grupo_membro_ativo")
                .HasFilter("[Ativo] = 1");

            // FK composta: EventoGrupoId replicado nunca diverge do grupo do evento.
            entity.HasOne(m => m.Evento).WithMany(e => e.Membros)
                .HasForeignKey(m => new { m.EventoId, m.EventoGrupoId })
                .HasPrincipalKey(e => new { e.Id, e.EventoGrupoId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(m => m.MembroId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Lancamento>().WithMany().HasForeignKey(m => m.LancamentoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventoOperacao>(entity =>
        {
            entity.ToTable("eventos_operacoes");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(o => o.RequestHash).HasMaxLength(64).IsRequired();
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
            entity.Property(h => h.Local).HasMaxLength(200).IsRequired();
            entity.Property(h => h.DataEvento).HasColumnType("date");
            entity.Property(h => h.TransporteValor).HasColumnType("decimal(18,2)");
            entity.Property(h => h.AlimentacaoValor).HasColumnType("decimal(18,2)");
            entity.Property(h => h.SeguroObrigatorio).HasColumnType("decimal(18,2)");
            entity.Property(h => h.ValorPorMembro).HasColumnType("decimal(18,2)");
            entity.Property(h => h.Total).HasColumnType("decimal(18,2)");
            entity.Property(h => h.ParticipantesJson).IsRequired();
            entity.Property(h => h.IpResponsavel).HasColumnType("varchar(45)").IsRequired();
            entity.Property(h => h.Motivo).HasMaxLength(255).IsRequired();
            entity.HasIndex(h => h.EventoId);
            entity.HasOne<Evento>().WithMany().HasForeignKey(h => h.EventoId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(h => h.UsuarioResponsavelId).OnDelete(DeleteBehavior.Restrict);
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
