using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificacoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notificacoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinatarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    Titulo = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Mensagem = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: false),
                    CriadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LancamentoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChaveOrigem = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    LidaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AvisoPendente = table.Column<int>(type: "int", nullable: true),
                    AvisoTentativas = table.Column<int>(type: "int", nullable: false),
                    AvisoProximaTentativaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notificacoes", x => x.Id);
                    table.CheckConstraint("CK_notificacoes_Aviso", "([AvisoPendente] IS NULL AND [AvisoProximaTentativaEmUtc] IS NULL) OR ([AvisoPendente] IN (1, 2) AND [AvisoProximaTentativaEmUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_notificacoes_AvisoTentativas", "[AvisoTentativas] >= 0");
                    table.CheckConstraint("CK_notificacoes_Tipo", "[Tipo] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_notificacoes_Lancamentos_LancamentoId",
                        column: x => x.LancamentoId,
                        principalTable: "Lancamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notificacoes_Usuarios_DestinatarioId",
                        column: x => x.DestinatarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notificacoes_eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notificacoes_aviso_pendente",
                table: "notificacoes",
                column: "AvisoProximaTentativaEmUtc",
                filter: "[AvisoPendente] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "AvisoPendente", "AvisoTentativas" });

            migrationBuilder.CreateIndex(
                name: "IX_notificacoes_EventoId",
                table: "notificacoes",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_notificacoes_LancamentoId",
                table: "notificacoes",
                column: "LancamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_notificacoes_pendentes",
                table: "notificacoes",
                columns: new[] { "DestinatarioId", "CriadaEmUtc", "Id" },
                descending: new[] { false, true, true },
                filter: "[LidaEmUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_notificacoes_destinatario_origem",
                table: "notificacoes",
                columns: new[] { "DestinatarioId", "ChaveOrigem" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notificacoes");
        }
    }
}
