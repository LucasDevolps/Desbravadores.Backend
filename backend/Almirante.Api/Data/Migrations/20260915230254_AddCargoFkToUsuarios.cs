using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCargoFkToUsuarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Usuarios não tem endpoint de criação (a única linha é o admin recriado pelo
            // DbSeeder a cada start). Não há como preencher a nova FK obrigatória CargoId com
            // um Cargo válido para linhas pré-existentes nesta migration (a tabela Cargos só é
            // populada pelo seeder em C#, depois que as migrations rodam), então limpamos a
            // tabela aqui; o DbSeeder recria o admin logo em seguida com o mesmo e-mail/senha
            // configurados, já apontando para o Cargo correto.
            migrationBuilder.Sql("DELETE FROM [Usuarios];");

            migrationBuilder.DropColumn(
                name: "Roles",
                table: "Usuarios");

            migrationBuilder.AddColumn<Guid>(
                name: "CargoId",
                table: "Usuarios",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_CargoId",
                table: "Usuarios",
                column: "CargoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Usuarios_Cargos_CargoId",
                table: "Usuarios",
                column: "CargoId",
                principalTable: "Cargos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Usuarios_Cargos_CargoId",
                table: "Usuarios");

            migrationBuilder.DropIndex(
                name: "IX_Usuarios_CargoId",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "CargoId",
                table: "Usuarios");

            migrationBuilder.AddColumn<string>(
                name: "Roles",
                table: "Usuarios",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }
    }
}
