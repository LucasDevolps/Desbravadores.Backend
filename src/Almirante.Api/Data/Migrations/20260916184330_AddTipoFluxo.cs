using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTipoFluxo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TipoFluxo",
                table: "LancamentosOperacoes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Entrada");

            migrationBuilder.AddColumn<string>(
                name: "TipoFluxo",
                table: "lancamentos_deletados",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Entrada");

            migrationBuilder.AddColumn<string>(
                name: "TipoFluxo",
                table: "Lancamentos",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Entrada");

            migrationBuilder.Sql(
                "ALTER TABLE [LancamentosOperacoes] ADD CONSTRAINT [CK_LancamentosOperacoes_TipoFluxo] CHECK ([TipoFluxo] IN (N'Entrada', N'Despesa'));");
            migrationBuilder.Sql(
                "ALTER TABLE [lancamentos_deletados] ADD CONSTRAINT [CK_lancamentos_deletados_TipoFluxo] CHECK ([TipoFluxo] IN (N'Entrada', N'Despesa'));");
            migrationBuilder.Sql(
                "ALTER TABLE [Lancamentos] ADD CONSTRAINT [CK_Lancamentos_TipoFluxo] CHECK ([TipoFluxo] IN (N'Entrada', N'Despesa'));");

            migrationBuilder.Sql("DROP TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica];");
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
                         [MembroId], [MembroNome], [Tipo], [Categoria], [TipoFluxo], [Valor], [Moeda], [Vencimento], [Status],
                         [OperacaoId], [DataCriacaoOriginal])
                    SELECT
                        NEWID(), d.[Id], @UsuarioResponsavelId, @IpResponsavel, SYSUTCDATETIME(), @Motivo,
                        d.[MembroId], d.[MembroNome], d.[Tipo], d.[Categoria], d.[TipoFluxo], d.[Valor], d.[Moeda], d.[Vencimento], d.[Status],
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

            migrationBuilder.DropColumn(
                name: "TipoFluxo",
                table: "LancamentosOperacoes");

            migrationBuilder.DropColumn(
                name: "TipoFluxo",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "TipoFluxo",
                table: "Lancamentos");

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
    }
}
