using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Converte as colunas nvarchar legadas para varchar (mesmo tamanho), sem nvarchar nos triggers. O banco usa a collation
    /// padrão SQL_Latin1_General_CP1_CI_AS (página 1252), onde os acentos do português cabem; a API já recusa texto fora dela.
    /// Para nunca derrubar um deploy por causa de dado antigo, valores que não cabem na página 1252 (ex.: emoji gravado antes
    /// da trava) NÃO bloqueiam a migration: o original é guardado em _varchar_conversao_backup (varbinary, UTF-16) antes da
    /// conversão, e o Down o restaura.
    /// </summary>
    public partial class ConverterColunasLegadasParaVarchar : Migration
    {
        public const string TabelaBackup = "_varchar_conversao_backup";

        /// <summary>Tabela, coluna, tamanho (null = max) e nulabilidade de cada coluna convertida.</summary>
        public static readonly (string Tabela, string Coluna, int? Tamanho, bool Nula)[] Colunas =
        [
            ("Usuarios", "Nome", 200, false),
            ("Usuarios", "SenhaHash", null, false),
            ("AuthSessions", "RevocationReason", 100, true),
            ("Cargos", "Nome", 200, false),
            ("Cargos", "Descricao", 1000, false),
            ("Cargos", "CriadoPor", 200, false),
            ("Cargos", "Role", 20, false),
            ("Lancamentos", "Finalidade", 50, true),
            ("Lancamentos", "Descricao", 500, true),
            ("LancamentosOperacoes", "IdempotencyKey", 100, false),
            ("LancamentosOperacoes", "RequestHash", 64, false),
            ("LancamentosOperacoes", "Finalidade", 50, false),
            ("LancamentosOperacoes", "Descricao", 500, true),
            ("lancamentos_deletados", "IpResponsavel", 45, false),
            ("lancamentos_deletados", "Motivo", 255, false),
            ("lancamentos_deletados", "Finalidade", 50, true),
            ("eventos", "Local", 200, false),
            ("eventos_operacoes", "IdempotencyKey", 100, false),
            ("eventos_operacoes", "RequestHash", 64, false),
            ("eventos_operacoes", "RespostaJson", null, true),
            ("historico_eventos", "Local", 200, false),
            ("historico_eventos", "Motivo", 255, false),
            ("historico_eventos", "ParticipantesJson", null, false),
            ("_usuarios_hist", "Nome", 200, false),
            ("_usuarios_hist", "Email", 256, false),
            ("_usuarios_hist", "CargoNome", 200, false),
            ("_usuarios_hist", "UsuarioResponsavelLogin", 256, false),
        ];

        // Check constraints que referenciam colunas convertidas bloqueiam o ALTER COLUMN (os índices o EF recria sozinho).
        private static readonly (string Tabela, string Nome, string Definicao)[] Checks =
        [
            ("lancamentos_deletados", "CK_lancamentos_deletados_Motivo", "LEN(LTRIM(RTRIM([Motivo]))) BETWEEN 1 AND 255"),
            ("eventos_operacoes", "CK_eventos_operacoes_RespostaJson", "[RespostaJson] IS NULL OR ISJSON([RespostaJson]) = 1"),
            ("historico_eventos", "CK_historico_eventos_ParticipantesJson", "ISJSON([ParticipantesJson]) = 1"),
        ];

        // Variáveis nvarchar dos triggers de auditoria (o restante da definição é mantido como está no banco).
        private static readonly (string Trigger, string Nvarchar, string Varchar)[] TrechosTriggers =
        [
            ("TR_Usuarios_Historico", "@Login nvarchar(256)", "@Login varchar(256)"),
            ("TR_Lancamentos_AuditoriaExclusaoLogica", "@Motivo nvarchar(255)", "@Motivo varchar(255)"),
            ("TR_Lancamentos_AuditoriaExclusaoLogica", "AS nvarchar(255))", "AS varchar(255))"),
            ("TR_eventos_AuditoriaExclusaoLogica", "@Motivo nvarchar(255)", "@Motivo varchar(255)"),
            ("TR_eventos_AuditoriaExclusaoLogica", "AS nvarchar(255))", "AS varchar(255))"),
        ];

        private static string Tipo(string familia, int? tamanho) => $"{familia}({(tamanho is null ? "max" : tamanho.ToString())})";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (tabela, coluna, _, _) in Colunas)
            {
                migrationBuilder.Sql(GuardarNaoConvertiveis(tabela, coluna));
            }

            foreach (var (tabela, nome, _) in Checks)
            {
                migrationBuilder.DropCheckConstraint(nome, tabela);
            }

            foreach (var (tabela, coluna, tamanho, nula) in Colunas)
            {
                migrationBuilder.AlterColumn<string>(coluna, tabela, type: Tipo("varchar", tamanho), unicode: false, maxLength: tamanho,
                    nullable: nula, oldClrType: typeof(string), oldType: Tipo("nvarchar", tamanho), oldMaxLength: tamanho, oldNullable: nula);
            }

            foreach (var (tabela, nome, definicao) in Checks)
            {
                migrationBuilder.AddCheckConstraint(nome, tabela, definicao);
            }

            foreach (var trigger in TrechosTriggers.GroupBy(t => t.Trigger))
            {
                migrationBuilder.Sql(RecriarTrigger(trigger.Key, trigger.Select(t => (t.Nvarchar, t.Varchar))));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var trigger in TrechosTriggers.GroupBy(t => t.Trigger))
            {
                migrationBuilder.Sql(RecriarTrigger(trigger.Key, trigger.Select(t => (t.Varchar, t.Nvarchar))));
            }

            foreach (var (tabela, nome, _) in Checks)
            {
                migrationBuilder.DropCheckConstraint(nome, tabela);
            }

            foreach (var (tabela, coluna, tamanho, nula) in Colunas)
            {
                migrationBuilder.AlterColumn<string>(coluna, tabela, type: Tipo("nvarchar", tamanho), maxLength: tamanho, nullable: nula,
                    oldClrType: typeof(string), oldType: Tipo("varchar", tamanho), oldUnicode: false, oldMaxLength: tamanho, oldNullable: nula);
            }

            foreach (var (tabela, nome, definicao) in Checks)
            {
                migrationBuilder.AddCheckConstraint(nome, tabela, definicao);
            }

            migrationBuilder.Sql(RestaurarNaoConvertiveis());
        }

        /// <summary>
        /// Guarda (UTF-16, como varbinary) os valores que mudariam na conversão para a página 1252 da collation do banco. A
        /// comparação é binária, então só entra o que a conversão de fato alteraria (emoji, ideograma, letra "best fit").
        /// </summary>
        private static string GuardarNaoConvertiveis(string tabela, string coluna) => $"""
            IF EXISTS (SELECT 1 FROM [{tabela}] WHERE [{coluna}] IS NOT NULL
                       AND CAST(CAST([{coluna}] AS varchar(max)) AS nvarchar(max)) COLLATE Latin1_General_BIN2 <> [{coluna}] COLLATE Latin1_General_BIN2)
            BEGIN
                IF OBJECT_ID(N'[dbo].[{TabelaBackup}]', N'U') IS NULL
                    CREATE TABLE [dbo].[{TabelaBackup}] (
                        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_{TabelaBackup}] PRIMARY KEY,
                        [Tabela] varchar(128) NOT NULL,
                        [Coluna] varchar(128) NOT NULL,
                        [RegistroId] uniqueidentifier NOT NULL,
                        [ValorOriginal] varbinary(max) NOT NULL,
                        [GuardadoEmUtc] datetime2 NOT NULL);
                EXEC(N'INSERT INTO [dbo].[{TabelaBackup}] ([Tabela], [Coluna], [RegistroId], [ValorOriginal], [GuardadoEmUtc])
                       SELECT ''{tabela}'', ''{coluna}'', [Id], CAST([{coluna}] AS varbinary(max)), SYSUTCDATETIME() FROM [{tabela}]
                       WHERE [{coluna}] IS NOT NULL
                         AND CAST(CAST([{coluna}] AS varchar(max)) AS nvarchar(max)) COLLATE Latin1_General_BIN2 <> [{coluna}] COLLATE Latin1_General_BIN2;');
            END
            """;

        /// <summary>
        /// Devolve os originais guardados no Up (as colunas já voltaram a nvarchar) e remove a tabela de backup. O trigger de
        /// histórico de usuários fica desligado durante a restauração: ela não é uma alteração de cadastro e não tem contexto de
        /// auditoria.
        /// </summary>
        private static string RestaurarNaoConvertiveis()
        {
            var restauracoes = string.Join(Environment.NewLine, Colunas.Select(c => $"""
                    UPDATE t SET [{c.Coluna}] = CAST(b.[ValorOriginal] AS nvarchar(max))
                    FROM [{c.Tabela}] t JOIN [dbo].[{TabelaBackup}] b ON b.[RegistroId] = t.[Id]
                    WHERE b.[Tabela] = ''{c.Tabela}'' AND b.[Coluna] = ''{c.Coluna}'';
                """));
            return $"""
                IF OBJECT_ID(N'[dbo].[{TabelaBackup}]', N'U') IS NOT NULL
                BEGIN
                    ALTER TABLE [Usuarios] DISABLE TRIGGER [TR_Usuarios_Historico];
                    EXEC(N'{restauracoes}');
                    ALTER TABLE [Usuarios] ENABLE TRIGGER [TR_Usuarios_Historico];
                    DROP TABLE [dbo].[{TabelaBackup}];
                END
                """;
        }

        /// <summary>
        /// Recria o trigger a partir da definição atual do banco, trocando só os trechos informados (CREATE ... TRIGGER vira
        /// CREATE OR ALTER TRIGGER). Falha se algum trecho não for encontrado, para não deixar o trigger pela metade.
        /// </summary>
        private static string RecriarTrigger(string trigger, IEnumerable<(string De, string Para)> trechos)
        {
            var trocas = string.Concat(trechos.Select(t => $"""

                IF CHARINDEX(N'{t.De}', @def) = 0 THROW 50090, N'{trigger}: trecho "{t.De}" não encontrado.', 1;
                SET @def = REPLACE(@def, N'{t.De}', N'{t.Para}');
                """));
            return $"""
                DECLARE @def nvarchar(max) = (SELECT [definition] FROM sys.sql_modules WHERE [object_id] = OBJECT_ID(N'[dbo].[{trigger}]'));
                IF @def IS NULL THROW 50090, N'{trigger} não encontrado.', 1;{trocas}
                DECLARE @inicio int = CHARINDEX(N'CREATE', @def);
                SET @def = STUFF(@def, @inicio, CHARINDEX(N'TRIGGER', @def, @inicio) - @inicio, N'CREATE OR ALTER ');
                EXEC sys.sp_executesql @def;
                """;
        }
    }
}
