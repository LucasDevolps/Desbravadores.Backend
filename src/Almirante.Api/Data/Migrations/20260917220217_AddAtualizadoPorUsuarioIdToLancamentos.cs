using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAtualizadoPorUsuarioIdToLancamentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AtualizadoPorUsuarioId",
                table: "Lancamentos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_AtualizadoPorUsuarioId",
                table: "Lancamentos",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.AddForeignKey(
                name: "FK_Lancamentos_Usuarios_AtualizadoPorUsuarioId",
                table: "Lancamentos",
                column: "AtualizadoPorUsuarioId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lancamentos_Usuarios_AtualizadoPorUsuarioId",
                table: "Lancamentos");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_AtualizadoPorUsuarioId",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "AtualizadoPorUsuarioId",
                table: "Lancamentos");
        }
    }
}
