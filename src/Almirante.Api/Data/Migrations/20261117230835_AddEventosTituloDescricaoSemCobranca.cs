using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Título (obrigatório) e descrição (opcional) do evento, e participação sem cobrança para eventos com valor por membro zero
    /// (<c>evento_membros.LancamentoId</c> passa a aceitar nulo). Os eventos existentes recebem o próprio local como título, o mesmo
    /// texto que já aparece na descrição dos lançamentos deles. O trigger de exclusão passa a guardar título e descrição e a manter no
    /// snapshot os participantes sem lançamento.
    /// </summary>
    public partial class AddEventosTituloDescricaoSemCobranca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Descricao",
                table: "historico_eventos",
                type: "varchar(2000)",
                unicode: false,
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Titulo",
                table: "historico_eventos",
                type: "varchar(150)",
                unicode: false,
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Descricao",
                table: "eventos",
                type: "varchar(2000)",
                unicode: false,
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Titulo",
                table: "eventos",
                type: "varchar(150)",
                unicode: false,
                maxLength: 150,
                nullable: true);

            // A transição Ativo 1 → 0 é a única que o trigger de exclusão audita: este preenchimento não gera histórico.
            migrationBuilder.Sql("EXEC(N'UPDATE [eventos] SET [Titulo] = RTRIM(LEFT([Local], 150)) WHERE [Titulo] IS NULL');");

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "eventos",
                type: "varchar(150)",
                unicode: false,
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(150)",
                oldUnicode: false,
                oldMaxLength: 150,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LancamentoId",
                table: "evento_membros",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_eventos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql(EventosTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Participação sem lançamento não cabe no schema anterior; inventar um lançamento falsearia o financeiro.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [evento_membros] WHERE [LancamentoId] IS NULL)
                    THROW 50196, 'Existem participantes de evento sem lançamento (eventos sem valor a pagar); a migration não pode ser desfeita sem perder esses vínculos.', 1;
                """);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_eventos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql(TriggerAnterior);

            migrationBuilder.DropColumn(
                name: "Descricao",
                table: "historico_eventos");

            migrationBuilder.DropColumn(
                name: "Titulo",
                table: "historico_eventos");

            migrationBuilder.DropColumn(
                name: "Descricao",
                table: "eventos");

            migrationBuilder.DropColumn(
                name: "Titulo",
                table: "eventos");

            migrationBuilder.AlterColumn<Guid>(
                name: "LancamentoId",
                table: "evento_membros",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }

        /// <summary>Versão vigente antes desta migration: a de <see cref="AddEventos"/> com o motivo em varchar (ConverterColunasLegadasParaVarchar).</summary>
        private static readonly string TriggerAnterior = AddEventos.EventosTrigger
            .Replace("@Motivo nvarchar(255)", "@Motivo varchar(255)", StringComparison.Ordinal)
            .Replace("AS nvarchar(255))", "AS varchar(255))", StringComparison.Ordinal);

        internal const string EventosTrigger = """
            CREATE TRIGGER [TR_eventos_AuditoriaExclusaoLogica] ON [eventos] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo varchar(255)=TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS varchar(255));
              IF @Usuario IS NULL OR @Ip IS NULL OR @Motivo IS NULL THROW 50011, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [historico_eventos] ([Id],[EventoId],[EventoGrupoId],[EventoReferenciaId],[Titulo],[Descricao],[DataEvento],[Local],[TransporteEhGratis],[TransporteValor],[AlimentacaoIndividual],[AlimentacaoValor],[SeguroObrigatorio],[ValorPorMembro],[QuantidadeMembros],[Total],[ParticipantesJson],[UsuarioResponsavelId],[IpResponsavel],[Motivo],[ExcluidoEmUtc])
              SELECT NEWID(),d.Id,d.EventoGrupoId,d.EventoReferenciaId,d.Titulo,d.Descricao,d.DataEvento,d.[Local],d.TransporteEhGratis,d.TransporteValor,d.AlimentacaoIndividual,d.AlimentacaoValor,d.SeguroObrigatorio,d.ValorPorMembro,
                     p.Quantidade,CAST(d.ValorPorMembro * p.Quantidade AS decimal(18,2)),p.Json,@Usuario,@Ip,@Motivo,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id
              CROSS APPLY (SELECT (SELECT COUNT(*) FROM [evento_membros] em WHERE em.EventoId=d.Id AND em.Ativo=1) AS Quantidade,
                                   ISNULL((SELECT em.MembroId AS membroId, em.LancamentoId AS lancamentoId, l.Valor AS valor,
                                                  CASE l.Status WHEN 0 THEN N'Pendente' WHEN 1 THEN N'Pago' WHEN 2 THEN N'Atrasado' END AS status
                                           FROM [evento_membros] em LEFT JOIN [Lancamentos] l ON l.Id=em.LancamentoId
                                           WHERE em.EventoId=d.Id AND em.Ativo=1 ORDER BY em.MembroId FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]') AS Json) p
              WHERE d.Ativo=1 AND i.Ativo=0;
            END
            """;
    }
}
