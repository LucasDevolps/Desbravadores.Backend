using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Pagamento efetivo em Lancamentos (issue #129): DataPagamento (date NULL), ValorPago (decimal(18,2) NULL) e quem/quando
    /// registrou (PagamentoRegistradoPorUsuarioId FK Usuarios, PagamentoRegistradoEmUtc), todos NULL nas linhas existentes:
    /// nenhuma data ou valor de pagamento é inventado, nem para os lançamentos já marcados como Pago (ficam fora do dashboard até
    /// serem regularizados por POST /api/Lancamentos/{id}/Pagamento). CHECK CK_Lancamentos_Pagamento (tudo nulo, ou Status Pago com
    /// data, 0 &lt; ValorPago &lt;= Valor e responsável). Índice filtrado por DataPagamento para o dashboard. lancamentos_deletados
    /// ganha as mesmas colunas e TR_Lancamentos_AuditoriaExclusaoLogica passa a copiá-las. Nenhuma linha existente é alterada.
    /// </summary>
    public partial class AddLancamentosPagamento : Migration
    {
        private const string Trigger = """
            CREATE TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica] ON [Lancamentos] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo nvarchar(255)=TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(255));
              IF @Usuario IS NULL OR @Ip IS NULL OR @Motivo IS NULL THROW 50001, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [lancamentos_deletados] ([Id],[LancamentoId],[UsuarioResponsavelId],[IpResponsavel],[ExcluidoEmUtc],[Motivo],[MembroId],[Finalidade],[Categoria],[TipoFluxo],[Valor],[Vencimento],[Status],[OperacaoId],[DataCriacaoOriginal],[EventoId],[DataPagamento],[ValorPago],[PagamentoRegistradoPorUsuarioId],[PagamentoRegistradoEmUtc])
              SELECT NEWID(),d.Id,@Usuario,@Ip,SYSUTCDATETIME(),@Motivo,d.MembroId,d.Finalidade,d.Categoria,d.TipoFluxo,d.Valor,d.Vencimento,d.Status,d.OperacaoId,d.DataCriacao,d.EventoId,d.DataPagamento,d.ValorPago,d.PagamentoRegistradoPorUsuarioId,d.PagamentoRegistradoEmUtc
              FROM deleted d JOIN inserted i ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DataPagamento",
                table: "lancamentos_deletados",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PagamentoRegistradoEmUtc",
                table: "lancamentos_deletados",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PagamentoRegistradoPorUsuarioId",
                table: "lancamentos_deletados",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ValorPago",
                table: "lancamentos_deletados",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DataPagamento",
                table: "Lancamentos",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PagamentoRegistradoEmUtc",
                table: "Lancamentos",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ValorPago",
                table: "Lancamentos",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_DataPagamento",
                table: "Lancamentos",
                column: "DataPagamento",
                filter: "[DataPagamento] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "TipoFluxo", "ValorPago", "Status", "Ativo" });

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos",
                column: "PagamentoRegistradoPorUsuarioId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_Pagamento",
                table: "Lancamentos",
                sql: "([DataPagamento] IS NULL AND [ValorPago] IS NULL AND [PagamentoRegistradoPorUsuarioId] IS NULL AND [PagamentoRegistradoEmUtc] IS NULL) OR ([Status] = 1 AND [DataPagamento] IS NOT NULL AND [ValorPago] > 0 AND [ValorPago] <= [Valor] AND [PagamentoRegistradoPorUsuarioId] IS NOT NULL AND [PagamentoRegistradoEmUtc] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Lancamentos_Usuarios_PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos",
                column: "PagamentoRegistradoPorUsuarioId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql(Trigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql(AddEventos.LancamentosTrigger(comEventoId: true));

            migrationBuilder.DropForeignKey(
                name: "FK_Lancamentos_Usuarios_PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_DataPagamento",
                table: "Lancamentos");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_Pagamento",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "DataPagamento",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "PagamentoRegistradoEmUtc",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "PagamentoRegistradoPorUsuarioId",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "ValorPago",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "DataPagamento",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "PagamentoRegistradoEmUtc",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "PagamentoRegistradoPorUsuarioId",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "ValorPago",
                table: "Lancamentos");
        }
    }
}
