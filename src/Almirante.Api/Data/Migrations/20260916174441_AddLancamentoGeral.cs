using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLancamentoGeral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true (não false, como o scaffolding gerou por padrão): lançamentos
            // pré-existentes (CRUD genérico e seed demonstrativo) nunca foram excluídos e devem
            // continuar ativos — mesmo default do lado C# em Entities/Lancamento.cs.
            migrationBuilder.AddColumn<bool>(
                name: "Ativo",
                table: "Lancamentos",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperacaoId",
                table: "Lancamentos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lancamentos_deletados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LancamentoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpResponsavel = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                    ExcluidoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MembroNome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Categoria = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Valor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Moeda = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Vencimento = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OperacaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataCriacaoOriginal = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lancamentos_deletados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lancamentos_deletados_Lancamentos_LancamentoId",
                        column: x => x.LancamentoId,
                        principalTable: "Lancamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lancamentos_deletados_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LancamentosOperacoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tipo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Categoria = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Valor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Vencimento = table.Column<DateOnly>(type: "date", nullable: false),
                    UsuariosProcessados = table.Column<int>(type: "int", nullable: false),
                    LancamentosCriados = table.Column<int>(type: "int", nullable: false),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LancamentosOperacoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LancamentosOperacoes_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_Ativo",
                table: "Lancamentos",
                column: "Ativo");

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_OperacaoId",
                table: "Lancamentos",
                column: "OperacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_lancamentos_deletados_LancamentoId",
                table: "lancamentos_deletados",
                column: "LancamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_lancamentos_deletados_UsuarioResponsavelId",
                table: "lancamentos_deletados",
                column: "UsuarioResponsavelId");

            migrationBuilder.CreateIndex(
                name: "IX_LancamentosOperacoes_CriadoPorUsuarioId",
                table: "LancamentosOperacoes",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_LancamentosOperacoes_IdempotencyKey",
                table: "LancamentosOperacoes",
                column: "IdempotencyKey",
                unique: true);

            // Defesa extra em nível de banco: mesmo que uma futura alteração de código ignore a
            // validação da API (Trim + 1-255 caracteres em LancamentosGeraisController), o banco
            // nunca aceita um Motivo vazio, só espaços, ou além do tamanho da coluna.
            migrationBuilder.Sql(
                """
                ALTER TABLE [lancamentos_deletados]
                ADD CONSTRAINT [CK_lancamentos_deletados_Motivo] CHECK (LEN(LTRIM(RTRIM([Motivo]))) BETWEEN 1 AND 255);
                """);

            // Gerada pelo banco: o trigger abaixo sempre define ExcluidoEmUtc explicitamente via
            // SYSUTCDATETIME(), mas o DEFAULT fica como rede de segurança (ex.: um INSERT manual
            // de diagnóstico que omita a coluna continua recebendo um horário UTC do próprio
            // banco, nunca do relógio da aplicação).
            migrationBuilder.Sql(
                """
                ALTER TABLE [lancamentos_deletados]
                ADD CONSTRAINT [DF_lancamentos_deletados_ExcluidoEmUtc] DEFAULT (SYSUTCDATETIME()) FOR [ExcluidoEmUtc];
                """);

            // Trigger de auditoria da exclusão lógica (requisito 6/7 da issue #14): audita
            // exclusivamente a transição Ativo 1 -> 0 em Lancamentos, suporta UPDATE de múltiplas
            // linhas (uma linha de auditoria por lançamento desativado no mesmo UPDATE) e nunca
            // audita updates que não envolvam essa transição (ex.: o PUT genérico existente em
            // LancamentosController, que nunca toca a coluna Ativo).
            //
            // O contexto (usuário responsável, IP, motivo) chega via SESSION_CONTEXT, alimentado
            // pela aplicação na mesma conexão/transação do UPDATE (ver
            // LancamentosGeraisService.DeleteAsync) — nunca a partir de dados enviados livremente
            // pelo frontend, e nunca a partir do login da própria conexão SQL. Se o contexto
            // estiver ausente ou o motivo for inválido, o trigger lança erro: com XACT_ABORT ON,
            // isso desfaz a transação inteira, incluindo o UPDATE em Lancamentos — não é possível
            // desativar um lançamento sem um contexto de auditoria válido.
            //
            // CREATE TRIGGER precisa ser a única instrução do lote em que roda — mas o EF Core não
            // sabe disso sobre um Sql() bruto e, ao aplicar a migration (Database.MigrateAsync,
            // usado por DbSeeder), agrupa comandos adjacentes no mesmo lote sempre que pode
            // (confirmado gerando `dotnet ef migrations script`: sem o EXEC abaixo, este CREATE
            // TRIGGER cairia no mesmo lote dos CREATE INDEX/ALTER TABLE anteriores e falharia no
            // SQL Server real com "Msg 111: 'CREATE TRIGGER' must be the first statement in a
            // query batch"). EXEC(N'...') roda o texto como um lote dinâmico isolado, o que
            // satisfaz a exigência sem depender de GO (que não é T-SQL, só funciona em
            // sqlcmd/SSMS). Aspas simples dentro do texto do trigger são dobradas ('') porque todo
            // o corpo agora é, ele mesmo, uma string T-SQL.
            migrationBuilder.Sql(
                """
                EXEC(N'
                CREATE TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica]
                ON [Lancamentos]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM inserted i
                        JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE d.[Ativo] = 1 AND i.[Ativo] = 0
                    )
                        RETURN;

                    DECLARE @UsuarioResponsavelId uniqueidentifier = TRY_CAST(SESSION_CONTEXT(N''UsuarioResponsavelId'') AS uniqueidentifier);
                    DECLARE @IpResponsavel varchar(45) = TRY_CAST(SESSION_CONTEXT(N''IpResponsavelExclusao'') AS varchar(45));
                    DECLARE @Motivo nvarchar(255) = TRY_CAST(SESSION_CONTEXT(N''MotivoExclusao'') AS nvarchar(255));

                    IF @UsuarioResponsavelId IS NULL
                        OR @IpResponsavel IS NULL
                        OR @Motivo IS NULL
                        OR LEN(LTRIM(RTRIM(@Motivo))) = 0
                    BEGIN
                        THROW 51000, N''Contexto de auditoria ausente ou inválido para exclusão lógica de lançamento (usuário responsável, IP e motivo são obrigatórios).'', 1;
                    END

                    INSERT INTO [lancamentos_deletados]
                        ([Id], [LancamentoId], [UsuarioResponsavelId], [IpResponsavel], [ExcluidoEmUtc], [Motivo],
                         [MembroId], [MembroNome], [Tipo], [Categoria], [Valor], [Moeda], [Vencimento], [Status],
                         [OperacaoId], [DataCriacaoOriginal])
                    SELECT
                        NEWID(), d.[Id], @UsuarioResponsavelId, @IpResponsavel, SYSUTCDATETIME(), @Motivo,
                        d.[MembroId], d.[MembroNome], d.[Tipo], d.[Categoria], d.[Valor], d.[Moeda], d.[Vencimento], d.[Status],
                        d.[OperacaoId], d.[DataCriacao]
                    FROM deleted d
                    JOIN inserted i ON i.[Id] = d.[Id]
                    WHERE d.[Ativo] = 1 AND i.[Ativo] = 0;
                END
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica];");

            migrationBuilder.DropTable(
                name: "lancamentos_deletados");

            migrationBuilder.DropTable(
                name: "LancamentosOperacoes");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_Ativo",
                table: "Lancamentos");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_OperacaoId",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "Ativo",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "OperacaoId",
                table: "Lancamentos");
        }
    }
}
