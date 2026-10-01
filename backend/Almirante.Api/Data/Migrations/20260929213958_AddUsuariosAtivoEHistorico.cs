using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsuariosAtivoEHistorico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Usuários existentes continuam ativos (mesma abordagem de Lancamentos.Ativo em AddLancamentoGeral).
            migrationBuilder.AddColumn<bool>(
                name: "Ativo",
                table: "Usuarios",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "_usuarios_hist",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CargoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoNome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    DataCriacao = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TipoOperacao = table.Column<string>(type: "varchar(6)", nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioResponsavelLogin = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IpResponsavel = table.Column<string>(type: "varchar(45)", nullable: false),
                    AlteradoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__usuarios_hist", x => x.Id);
                    table.CheckConstraint("CK__usuarios_hist_TipoOperacao", "[TipoOperacao] IN ('UPDATE', 'DELETE')");
                    table.ForeignKey(
                        name: "FK__usuarios_hist_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK__usuarios_hist_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX__usuarios_hist_UsuarioId",
                table: "_usuarios_hist",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX__usuarios_hist_UsuarioResponsavelId",
                table: "_usuarios_hist",
                column: "UsuarioResponsavelId");

            migrationBuilder.Sql(UsuariosTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Usuarios_Historico];");

            migrationBuilder.DropTable(
                name: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "Ativo",
                table: "Usuarios");
        }

        // Histórico de usuários: dispara em todo UPDATE de Usuarios, mas só age quando um campo CADASTRAL muda (Nome,
        // Email, EmailNormalizado, CargoId, Ativo; comparação binária para pegar mudança só de maiúsculas/minúsculas).
        // Login, bloqueio por falhas e reset de senha (SenhaHash, SecurityVersion, Falhas*) passam direto, sem exigir
        // contexto. Para mudanças cadastrais, exige o SESSION_CONTEXT definido pela aplicação (mesmas chaves dos
        // triggers de eventos/lançamentos) e grava o estado ANTERIOR (deleted) em _usuarios_hist, na mesma transação.
        // TipoOperacao é derivado dos dados (Ativo 1 -> 0 = DELETE), nunca informado pelo cliente. O login do
        // responsável é o e-mail dele antes desta instrução (vale também quando ele altera o próprio cadastro).
        private const string UsuariosTrigger = """
            CREATE TRIGGER [TR_Usuarios_Historico] ON [Usuarios] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id
                             WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                                OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                                OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                                OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50021, 'Contexto de auditoria obrigatório.', 1;
              DECLARE @Login nvarchar(256)=COALESCE((SELECT d.Email FROM deleted d WHERE d.Id=@Usuario),
                                                    (SELECT u.Email FROM [Usuarios] u WHERE u.Id=@Usuario));
              INSERT INTO [_usuarios_hist] ([Id],[UsuarioId],[Nome],[Email],[CargoId],[CargoNome],[Ativo],[DataCriacao],[TipoOperacao],[UsuarioResponsavelId],[UsuarioResponsavelLogin],[IpResponsavel],[AlteradoEmUtc])
              SELECT NEWID(),d.Id,d.Nome,d.Email,d.CargoId,c.Nome,d.Ativo,d.DataCriacao,
                     CASE WHEN d.Ativo=1 AND i.Ativo=0 THEN 'DELETE' ELSE 'UPDATE' END,
                     @Usuario,@Login,@Ip,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id JOIN [Cargos] c ON c.Id=d.CargoId
              WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                 OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                 OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                 OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo;
            END
            """;
    }
}
