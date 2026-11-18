using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Issue #209. Devoluções de evento: <c>Lancamentos.LancamentoOrigemId</c> (FK para o lançamento Pago de origem), <c>DevolucaoMotivo</c>
    /// e <c>DevolucaoJustificativa</c> (varchar), com CK_Lancamentos_Devolucao garantindo que só uma Saída de membro ligada a evento seja
    /// devolução. CK_notificacoes_Tipo passa a aceitar DevolucaoEvento (5). Nenhuma linha existente é alterada. O Down é recusado enquanto
    /// houver devolução gravada: apagar a ligação com o pagamento de origem falsearia o caixa.
    /// </summary>
    public partial class AddDevolucoesEvento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.AddColumn<string>(
                name: "DevolucaoJustificativa",
                table: "Lancamentos",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DevolucaoMotivo",
                table: "Lancamentos",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LancamentoOrigemId",
                table: "Lancamentos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2, 3, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_LancamentoOrigemId",
                table: "Lancamentos",
                column: "LancamentoOrigemId",
                filter: "[LancamentoOrigemId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_Devolucao",
                table: "Lancamentos",
                sql: "([LancamentoOrigemId] IS NULL AND [DevolucaoMotivo] IS NULL AND [DevolucaoJustificativa] IS NULL) OR ([LancamentoOrigemId] IS NOT NULL AND [TipoFluxo] = 1 AND [EventoId] IS NOT NULL AND [MembroId] IS NOT NULL AND [DevolucaoMotivo] IS NOT NULL AND ([DevolucaoJustificativa] IS NULL OR [DataPagamento] IS NOT NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_Lancamentos_Lancamentos_LancamentoOrigemId",
                table: "Lancamentos",
                column: "LancamentoOrigemId",
                principalTable: "Lancamentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Lancamentos] WHERE [LancamentoOrigemId] IS NOT NULL)
                    THROW 50209, 'Existem devoluções de evento gravadas; a migration não pode ser desfeita sem perder a ligação com os pagamentos de origem.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Lancamentos_Lancamentos_LancamentoOrigemId",
                table: "Lancamentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_LancamentoOrigemId",
                table: "Lancamentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_Devolucao",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "DevolucaoJustificativa",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "DevolucaoMotivo",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "LancamentoOrigemId",
                table: "Lancamentos");

            migrationBuilder.Sql("DELETE FROM [notificacoes] WHERE [Tipo] = 5;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2, 3, 4)");
        }
    }
}
