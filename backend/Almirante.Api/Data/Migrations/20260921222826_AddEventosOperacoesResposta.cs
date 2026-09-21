using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventosOperacoesResposta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RespostaJson",
                table: "eventos_operacoes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_eventos_operacoes_RespostaJson",
                table: "eventos_operacoes",
                sql: "[RespostaJson] IS NULL OR ISJSON([RespostaJson]) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_eventos_operacoes_RespostaJson",
                table: "eventos_operacoes");

            migrationBuilder.DropColumn(
                name: "RespostaJson",
                table: "eventos_operacoes");
        }
    }
}
