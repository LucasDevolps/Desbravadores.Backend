using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertLancamentoEnums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As constraints anteriores foram criadas por SQL e não constavam no snapshot.
            // A conversão mantém IDs, valores, auditoria e hashes de idempotência intactos.
            migrationBuilder.DropCheckConstraint(
                name: "CK_LancamentosOperacoes_TipoFluxo",
                table: "LancamentosOperacoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_lancamentos_deletados_TipoFluxo",
                table: "lancamentos_deletados");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_TipoFluxo",
                table: "Lancamentos");

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [LancamentosOperacoes]
                    WHERE [Categoria] NOT IN (N'Evento', N'Clube')
                       OR [TipoFluxo] NOT IN (N'Entrada', N'Despesa'))
                    THROW 50002, 'Valor legado inválido em LancamentosOperacoes; a migração foi cancelada.', 1;
                UPDATE [LancamentosOperacoes] SET
                    [Categoria] = CASE [Categoria] WHEN N'Evento' THEN N'0' WHEN N'Clube' THEN N'1' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'Entrada' THEN N'0' WHEN N'Despesa' THEN N'1' END;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [lancamentos_deletados]
                    WHERE [Categoria] NOT IN (N'Evento', N'Clube')
                       OR [TipoFluxo] NOT IN (N'Entrada', N'Despesa') OR [Status] NOT IN (N'Pendente', N'Pago', N'Atrasado'))
                    THROW 50002, 'Valor legado inválido em lancamentos_deletados; a migração foi cancelada.', 1;
                UPDATE [lancamentos_deletados] SET
                    [Categoria] = CASE [Categoria] WHEN N'Evento' THEN N'0' WHEN N'Clube' THEN N'1' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'Entrada' THEN N'0' WHEN N'Despesa' THEN N'1' END,
                    [Status] = CASE [Status] WHEN N'Pendente' THEN N'0' WHEN N'Pago' THEN N'1' WHEN N'Atrasado' THEN N'2' END;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Lancamentos]
                    WHERE [Categoria] NOT IN (N'Evento', N'Clube')
                       OR [TipoFluxo] NOT IN (N'Entrada', N'Despesa') OR [Status] NOT IN (N'Pendente', N'Pago', N'Atrasado'))
                    THROW 50002, 'Valor legado inválido em Lancamentos; a migração foi cancelada.', 1;
                UPDATE [Lancamentos] SET
                    [Categoria] = CASE [Categoria] WHEN N'Evento' THEN N'0' WHEN N'Clube' THEN N'1' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'Entrada' THEN N'0' WHEN N'Despesa' THEN N'1' END,
                    [Status] = CASE [Status] WHEN N'Pendente' THEN N'0' WHEN N'Pago' THEN N'1' WHEN N'Atrasado' THEN N'2' END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "TipoFluxo",
                table: "LancamentosOperacoes",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "Categoria",
                table: "LancamentosOperacoes",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<int>(
                name: "TipoFluxo",
                table: "lancamentos_deletados",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "lancamentos_deletados",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "Categoria",
                table: "lancamentos_deletados",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<int>(
                name: "TipoFluxo",
                table: "Lancamentos",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Lancamentos",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "Categoria",
                table: "Lancamentos",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AddCheckConstraint(
                name: "CK_LancamentosOperacoes_Categoria",
                table: "LancamentosOperacoes",
                sql: "[Categoria] IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LancamentosOperacoes_TipoFluxo",
                table: "LancamentosOperacoes",
                sql: "[TipoFluxo] IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lancamentos_deletados_Categoria",
                table: "lancamentos_deletados",
                sql: "[Categoria] IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lancamentos_deletados_Status",
                table: "lancamentos_deletados",
                sql: "[Status] IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_lancamentos_deletados_TipoFluxo",
                table: "lancamentos_deletados",
                sql: "[TipoFluxo] IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_Categoria",
                table: "Lancamentos",
                sql: "[Categoria] IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_Status",
                table: "Lancamentos",
                sql: "[Status] IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_TipoFluxo",
                table: "Lancamentos",
                sql: "[TipoFluxo] IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LancamentosOperacoes_Categoria",
                table: "LancamentosOperacoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LancamentosOperacoes_TipoFluxo",
                table: "LancamentosOperacoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_lancamentos_deletados_Categoria",
                table: "lancamentos_deletados");

            migrationBuilder.DropCheckConstraint(
                name: "CK_lancamentos_deletados_Status",
                table: "lancamentos_deletados");

            migrationBuilder.DropCheckConstraint(
                name: "CK_lancamentos_deletados_TipoFluxo",
                table: "lancamentos_deletados");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_Categoria",
                table: "Lancamentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_Status",
                table: "Lancamentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Lancamentos_TipoFluxo",
                table: "Lancamentos");

            migrationBuilder.AlterColumn<string>(
                name: "TipoFluxo",
                table: "LancamentosOperacoes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Categoria",
                table: "LancamentosOperacoes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "TipoFluxo",
                table: "lancamentos_deletados",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "lancamentos_deletados",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Categoria",
                table: "lancamentos_deletados",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "TipoFluxo",
                table: "Lancamentos",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Lancamentos",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Categoria",
                table: "Lancamentos",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            // Restaura os textos esperados pela versão anterior da aplicação.
            migrationBuilder.Sql("""
                UPDATE [LancamentosOperacoes] SET
                    [Categoria] = CASE [Categoria] WHEN N'0' THEN N'Evento' WHEN N'1' THEN N'Clube' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'0' THEN N'Entrada' WHEN N'1' THEN N'Despesa' END;
                """);
            migrationBuilder.AddCheckConstraint(
                name: "CK_LancamentosOperacoes_TipoFluxo",
                table: "LancamentosOperacoes",
                sql: "[TipoFluxo] IN (N'Entrada', N'Despesa')");

            migrationBuilder.Sql("""
                UPDATE [lancamentos_deletados] SET
                    [Categoria] = CASE [Categoria] WHEN N'0' THEN N'Evento' WHEN N'1' THEN N'Clube' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'0' THEN N'Entrada' WHEN N'1' THEN N'Despesa' END,
                    [Status] = CASE [Status] WHEN N'0' THEN N'Pendente' WHEN N'1' THEN N'Pago' WHEN N'2' THEN N'Atrasado' END;
                """);
            migrationBuilder.AddCheckConstraint(
                name: "CK_lancamentos_deletados_TipoFluxo",
                table: "lancamentos_deletados",
                sql: "[TipoFluxo] IN (N'Entrada', N'Despesa')");

            migrationBuilder.Sql("""
                UPDATE [Lancamentos] SET
                    [Categoria] = CASE [Categoria] WHEN N'0' THEN N'Evento' WHEN N'1' THEN N'Clube' END,
                    [TipoFluxo] = CASE [TipoFluxo] WHEN N'0' THEN N'Entrada' WHEN N'1' THEN N'Despesa' END,
                    [Status] = CASE [Status] WHEN N'0' THEN N'Pendente' WHEN N'1' THEN N'Pago' WHEN N'2' THEN N'Atrasado' END;
                """);
            migrationBuilder.AddCheckConstraint(
                name: "CK_Lancamentos_TipoFluxo",
                table: "Lancamentos",
                sql: "[TipoFluxo] IN (N'Entrada', N'Despesa')");

        }
    }
}
