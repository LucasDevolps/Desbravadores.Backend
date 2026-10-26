using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Issue #159. Usuarios_alteracoes_senha: auditoria só de inserção das alterações de senha concluídas (FKs sem cascade para Usuarios e
    /// para a recuperação; o trigger TR_Usuarios_alteracoes_senha_Imutavel recusa UPDATE e DELETE). Usuarios_recuperacoes_senha: pedidos
    /// e autorizações de recuperação, com só o hash do token. CK_notificacoes_Tipo passa a aceitar SenhaAlterada (3). Tabelas novas e
    /// vazias: nenhuma linha existente é alterada, e a API anterior ignora as tabelas novas. Down remove as tabelas (com o histórico de
    /// alterações de senha) e as notificações do tipo 3, que a constraint anterior não aceitaria.
    /// </summary>
    public partial class AddAlteracaoERecuperacaoSenha : Migration
    {
        private const string TriggerImutavel = $"""
            CREATE TRIGGER [dbo].[{AlmiranteDbContext.TriggerAlteracoesSenhaImutavel}] ON [dbo].[{AlmiranteDbContext.TabelaAlteracoesSenha}]
            INSTEAD OF UPDATE, DELETE AS
            BEGIN
              SET NOCOUNT ON;
              THROW 50160, 'Usuarios_alteracoes_senha aceita somente insercao: a auditoria de senha nao pode ser alterada nem excluida.', 1;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.CreateTable(
                name: "Usuarios_recuperacoes_senha",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origem = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SolicitadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UltimaSolicitacaoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Solicitacoes = table.Column<int>(type: "int", nullable: false),
                    IpSolicitacao = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    ExpiraEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioAutorizadorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IpAutorizador = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    MetodoVerificacao = table.Column<int>(type: "int", nullable: true),
                    AutorizadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    TokenExpiraEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: true),
                    EncerradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IpConclusao = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    MotivoEncerramento = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios_recuperacoes_senha", x => x.Id);
                    table.CheckConstraint("CK_Usuarios_recuperacoes_senha_Autorizacao", "([TokenHash] IS NULL AND [TokenExpiraEmUtc] IS NULL AND [SecurityVersion] IS NULL) OR ([TokenHash] IS NOT NULL AND [TokenExpiraEmUtc] IS NOT NULL AND [SecurityVersion] IS NOT NULL AND [UsuarioAutorizadorId] IS NOT NULL AND [IpAutorizador] IS NOT NULL AND [MetodoVerificacao] IS NOT NULL AND [AutorizadaEmUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_Usuarios_recuperacoes_senha_Metodo", "[MetodoVerificacao] IS NULL OR [MetodoVerificacao] IN (1, 2)");
                    table.CheckConstraint("CK_Usuarios_recuperacoes_senha_Origem", "([Origem] = 1 AND [IpSolicitacao] IS NOT NULL) OR ([Origem] = 2 AND [IpSolicitacao] IS NULL)");
                    table.CheckConstraint("CK_Usuarios_recuperacoes_senha_Solicitacoes", "[Solicitacoes] >= 1");
                    table.CheckConstraint("CK_Usuarios_recuperacoes_senha_Status", "([Status] = 1 AND [TokenHash] IS NULL AND [EncerradaEmUtc] IS NULL AND [MotivoEncerramento] IS NULL) OR ([Status] = 2 AND [TokenHash] IS NOT NULL AND [EncerradaEmUtc] IS NULL AND [MotivoEncerramento] IS NULL) OR ([Status] IN (3, 4) AND [EncerradaEmUtc] IS NOT NULL AND [MotivoEncerramento] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Usuarios_recuperacoes_senha_Usuarios_UsuarioAutorizadorId",
                        column: x => x.UsuarioAutorizadorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Usuarios_recuperacoes_senha_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Usuarios_alteracoes_senha",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UsuarioAfetadoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpResponsavel = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: false),
                    AlteradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TipoAlteracao = table.Column<int>(type: "int", nullable: false),
                    TraceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SolicitacaoRecuperacaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios_alteracoes_senha", x => x.Id);
                    table.CheckConstraint("CK_Usuarios_alteracoes_senha_Responsavel", "([TipoAlteracao] = 1 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] = [UsuarioAfetadoId] AND [SolicitacaoRecuperacaoId] IS NULL) OR ([TipoAlteracao] = 2 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] <> [UsuarioAfetadoId]) OR ([TipoAlteracao] = 3 AND [UsuarioResponsavelId] IS NOT NULL AND [UsuarioResponsavelId] <> [UsuarioAfetadoId] AND [SolicitacaoRecuperacaoId] IS NOT NULL) OR ([TipoAlteracao] = 4 AND [UsuarioResponsavelId] IS NULL AND [SolicitacaoRecuperacaoId] IS NOT NULL)");
                    table.CheckConstraint("CK_Usuarios_alteracoes_senha_Tipo", "[TipoAlteracao] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_Usuarios_alteracoes_senha_Usuarios_UsuarioAfetadoId",
                        column: x => x.UsuarioAfetadoId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Usuarios_alteracoes_senha_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Usuarios_alteracoes_senha_Usuarios_recuperacoes_senha_SolicitacaoRecuperacaoId",
                        column: x => x.SolicitacaoRecuperacaoId,
                        principalTable: "Usuarios_recuperacoes_senha",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2, 3)");

            migrationBuilder.Sql(TriggerImutavel);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_alteracoes_senha_afetado_data",
                table: "Usuarios_alteracoes_senha",
                columns: new[] { "UsuarioAfetadoId", "AlteradaEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_alteracoes_senha_responsavel_data",
                table: "Usuarios_alteracoes_senha",
                columns: new[] { "UsuarioResponsavelId", "AlteradaEmUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_Usuarios_alteracoes_senha_recuperacao",
                table: "Usuarios_alteracoes_senha",
                column: "SolicitacaoRecuperacaoId",
                unique: true,
                filter: "[SolicitacaoRecuperacaoId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_recuperacoes_senha_abertas",
                table: "Usuarios_recuperacoes_senha",
                columns: new[] { "Status", "SolicitadaEmUtc", "Id" },
                descending: new[] { false, true, true },
                filter: "[Status] IN (1, 2)")
                .Annotation("SqlServer:Include", new[] { "UsuarioId", "ExpiraEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_recuperacoes_senha_UsuarioAutorizadorId",
                table: "Usuarios_recuperacoes_senha",
                column: "UsuarioAutorizadorId");

            migrationBuilder.CreateIndex(
                name: "UX_Usuarios_recuperacoes_senha_aberta",
                table: "Usuarios_recuperacoes_senha",
                column: "UsuarioId",
                unique: true,
                filter: "[Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "UX_Usuarios_recuperacoes_senha_token",
                table: "Usuarios_recuperacoes_senha",
                column: "TokenHash",
                unique: true,
                filter: "[TokenHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Usuarios_alteracoes_senha");

            migrationBuilder.DropTable(
                name: "Usuarios_recuperacoes_senha");

            migrationBuilder.DropCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes");

            migrationBuilder.Sql("DELETE FROM [notificacoes] WHERE [Tipo] = 3;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_notificacoes_Tipo",
                table: "notificacoes",
                sql: "[Tipo] IN (1, 2)");
        }
    }
}
