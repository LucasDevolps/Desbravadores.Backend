using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventosItensAdicionais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_eventos_valores_nao_negativos",
                table: "eventos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_eventos_valores_normalizados",
                table: "eventos");

            migrationBuilder.AddColumn<decimal>(
                name: "ItensAdicionaisValor",
                table: "eventos",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "evento_itens_adicionais",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordem = table.Column<int>(type: "int", nullable: false),
                    Titulo = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Valor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DesativadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_itens_adicionais", x => x.Id);
                    table.CheckConstraint("CK_evento_itens_adicionais_Valor", "[Valor] > 0");
                    table.ForeignKey(
                        name: "FK_evento_itens_adicionais_eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_eventos_valores_nao_negativos",
                table: "eventos",
                sql: "[TransporteValor] >= 0 AND [AlimentacaoValor] >= 0 AND [SeguroObrigatorio] >= 0 AND [ItensAdicionaisValor] >= 0 AND [ValorPorMembro] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_eventos_valores_normalizados",
                table: "eventos",
                sql: "([TransporteEhGratis] = 0 OR [TransporteValor] = 0) AND ([AlimentacaoIndividual] = 0 OR [AlimentacaoValor] = 0) AND [ValorPorMembro] = [TransporteValor] + [AlimentacaoValor] + [SeguroObrigatorio] + [ItensAdicionaisValor]");

            migrationBuilder.CreateIndex(
                name: "UX_evento_itens_adicionais_evento_ordem_ativo",
                table: "evento_itens_adicionais",
                columns: new[] { "EventoId", "Ordem" },
                unique: true,
                filter: "[Ativo] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [evento_itens_adicionais]) OR EXISTS (SELECT 1 FROM [eventos] WHERE [ItensAdicionaisValor] <> 0)
                    THROW 50225, 'Existem itens adicionais de evento gravados; a migration não pode ser desfeita sem alterar o valor por membro.', 1;
                """);

            migrationBuilder.DropTable(
                name: "evento_itens_adicionais");

            migrationBuilder.DropCheckConstraint(
                name: "CK_eventos_valores_nao_negativos",
                table: "eventos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_eventos_valores_normalizados",
                table: "eventos");

            migrationBuilder.DropColumn(
                name: "ItensAdicionaisValor",
                table: "eventos");

            migrationBuilder.AddCheckConstraint(
                name: "CK_eventos_valores_nao_negativos",
                table: "eventos",
                sql: "[TransporteValor] >= 0 AND [AlimentacaoValor] >= 0 AND [SeguroObrigatorio] >= 0 AND [ValorPorMembro] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_eventos_valores_normalizados",
                table: "eventos",
                sql: "([TransporteEhGratis] = 0 OR [TransporteValor] = 0) AND ([AlimentacaoIndividual] = 0 OR [AlimentacaoValor] = 0) AND [ValorPorMembro] = [TransporteValor] + [AlimentacaoValor] + [SeguroObrigatorio]");
        }
    }
}
