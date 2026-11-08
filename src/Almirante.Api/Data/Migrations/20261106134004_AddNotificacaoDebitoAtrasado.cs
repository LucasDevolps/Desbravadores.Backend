using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Issue #184. CK_notificacoes_Tipo passa a aceitar DebitoAtrasado (4), criada pela rotina que marca como Atrasado os lançamentos
    /// pendentes vencidos. Nenhuma linha existente é alterada. Down remove as notificações do tipo 4, que a constraint anterior não
    /// aceitaria; os lançamentos que a rotina marcou continuam Atrasado, status que o schema anterior já aceitava.
    /// </summary>
    public partial class AddNotificacaoDebitoAtrasado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2, 3, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.Sql("DELETE FROM [notificacoes] WHERE [Tipo] = 4;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2, 3)");
        }
    }
}
