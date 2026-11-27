using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAjusteIsencaoCobrancasEvento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ValorAjustado",
                table: "Lancamentos",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "IsencaoLancamentoId",
                table: "evento_membros",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsencaoMotivo",
                table: "evento_membros",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Isento",
                table: "evento_membros",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "IsentoEmUtc",
                table: "evento_membros",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IsentoPorUsuarioId",
                table: "evento_membros",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_ValorAjustado",
                table: "Lancamentos",
                sql: "[ValorAjustado] = 0 OR ([EventoId] IS NOT NULL AND [LancamentoOrigemId] IS NULL AND [TipoFluxo] = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_IsencaoLancamentoId",
                table: "evento_membros",
                column: "IsencaoLancamentoId",
                filter: "[IsencaoLancamentoId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_IsentoPorUsuarioId",
                table: "evento_membros",
                column: "IsentoPorUsuarioId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_evento_membros_Isencao",
                table: "evento_membros",
                sql: "([Isento] = 0 AND [IsencaoLancamentoId] IS NULL AND [IsencaoMotivo] IS NULL AND [IsentoPorUsuarioId] IS NULL AND [IsentoEmUtc] IS NULL) OR ([Isento] = 1 AND [LancamentoId] IS NULL AND [IsencaoLancamentoId] IS NOT NULL AND [IsencaoMotivo] IS NOT NULL AND [IsentoPorUsuarioId] IS NOT NULL AND [IsentoEmUtc] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_evento_membros_Lancamentos_IsencaoLancamentoId",
                table: "evento_membros",
                column: "IsencaoLancamentoId",
                principalTable: "Lancamentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_evento_membros_Usuarios_IsentoPorUsuarioId",
                table: "evento_membros",
                column: "IsentoPorUsuarioId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [evento_membros] WHERE [Isento] = 1) OR EXISTS (SELECT 1 FROM [Lancamentos] WHERE [ValorAjustado] = 1)
                    THROW 50223, 'Existem cobranças de evento ajustadas ou isentas; a migration não pode ser desfeita sem perder esses registros.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_evento_membros_Lancamentos_IsencaoLancamentoId",
                table: "evento_membros");

            migrationBuilder.DropForeignKey(
                name: "FK_evento_membros_Usuarios_IsentoPorUsuarioId",
                table: "evento_membros");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_ValorAjustado",
                table: "Lancamentos");

            migrationBuilder.DropIndex(
                name: "IX_evento_membros_IsencaoLancamentoId",
                table: "evento_membros");

            migrationBuilder.DropIndex(
                name: "IX_evento_membros_IsentoPorUsuarioId",
                table: "evento_membros");

            migrationBuilder.DropCheckConstraint(
                name: "CK_evento_membros_Isencao",
                table: "evento_membros");

            migrationBuilder.DropColumn(
                name: "ValorAjustado",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "IsencaoLancamentoId",
                table: "evento_membros");

            migrationBuilder.DropColumn(
                name: "IsencaoMotivo",
                table: "evento_membros");

            migrationBuilder.DropColumn(
                name: "Isento",
                table: "evento_membros");

            migrationBuilder.DropColumn(
                name: "IsentoEmUtc",
                table: "evento_membros");

            migrationBuilder.DropColumn(
                name: "IsentoPorUsuarioId",
                table: "evento_membros");
        }
    }
}
