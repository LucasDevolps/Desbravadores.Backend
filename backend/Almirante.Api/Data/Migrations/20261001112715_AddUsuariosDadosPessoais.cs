using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Dados pessoais de usuários: Cpf varchar(20), DataNascimento date e Telefone varchar(20), todos NULL nos registros
    /// existentes (nenhum valor é inventado), em Usuarios e em _usuarios_hist; e-mail reduzido para varchar(100); índice
    /// único filtrado UX_Usuarios_Cpf; TR_Usuarios_Historico passa a copiar e comparar os novos campos.
    /// Interrompe (THROW, transação da migration desfeita) se houver dado incompatível, sem truncar nem corrigir nada.
    /// </summary>
    public partial class AddUsuariosDadosPessoais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Antes de qualquer alteração: e-mails que não cabem em varchar(100) ASCII. O ALTER COLUMN recusaria os
            //    longos, mas converteria em silêncio os caracteres fora da página de código; e a aplicação passa a só
            //    aceitar (e só encontrar no login) e-mail em ASCII imprimível. Só os Ids saem na mensagem (sem e-mails).
            migrationBuilder.Sql(VerificarEmails);

            migrationBuilder.AlterColumn<string>(
                name: "EmailNormalizado",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "Cpf",
                table: "Usuarios",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DataNascimento",
                table: "Usuarios",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Telefone",
                table: "Usuarios",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            // Histórico: mesmos tipos de Usuarios, NULL nas linhas antigas (o valor da época não existia). _usuarios_hist.Email
            // permanece nvarchar(256), que comporta qualquer varchar(100) e não arrisca as linhas já gravadas.
            migrationBuilder.AddColumn<string>(
                name: "Cpf",
                table: "_usuarios_hist",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DataNascimento",
                table: "_usuarios_hist",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Telefone",
                table: "_usuarios_hist",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            // 2) Antes do índice único: CPF fora da forma normalizada ou repetido DEPOIS da normalização. Pelo fluxo normal a
            //    coluna acabou de ser criada (toda NULL) e a verificação passa; ela existe para nunca criar o índice sobre
            //    dados que a aplicação consideraria duplicados ou que ela própria não produziria.
            migrationBuilder.Sql(VerificarCpfs);

            migrationBuilder.CreateIndex(
                name: "UX_Usuarios_Cpf",
                table: "Usuarios",
                column: "Cpf",
                unique: true,
                filter: "[Cpf] IS NOT NULL");

            // 3) Trigger com os novos campos (CREATE OR ALTER: mantém o objeto e permissões, troca só a definição).
            migrationBuilder.Sql(TriggerComDadosPessoais);
        }

        /// <summary>
        /// Rollback estrutural: restaura a trigger da migration AddUsuariosAtivoEHistorico ANTES de remover as colunas que a
        /// nova definição referencia, remove o índice e as colunas e devolve o e-mail a nvarchar(256) (sem perda).
        /// ATENÇÃO: CPF, data de nascimento e telefone de Usuarios E de _usuarios_hist são PERDIDOS de forma irreversível.
        /// Faça backup antes; não use como rollback de produção (ver docs/release-process.md, "Banco e migrations").
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TriggerAnterior);

            migrationBuilder.DropIndex(
                name: "UX_Usuarios_Cpf",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "Cpf",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "DataNascimento",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "Telefone",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "Cpf",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "DataNascimento",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "Telefone",
                table: "_usuarios_hist");

            migrationBuilder.AlterColumn<string>(
                name: "EmailNormalizado",
                table: "Usuarios",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldUnicode: false,
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Usuarios",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldUnicode: false,
                oldMaxLength: 100);
        }

        // DATALENGTH/2 conta também espaços finais (LEN os ignora). [^ -~] em BIN2 = qualquer caractere fora de 0x20-0x7E.
        private const string VerificarEmails = """
            DECLARE @Qtd int, @Ids nvarchar(max);
            SELECT @Qtd = COUNT(*) FROM [Usuarios]
            WHERE DATALENGTH([Email]) / 2 > 100 OR DATALENGTH([EmailNormalizado]) / 2 > 100
               OR PATINDEX(N'%[^ -~]%', [Email] COLLATE Latin1_General_BIN2) > 0
               OR PATINDEX(N'%[^ -~]%', [EmailNormalizado] COLLATE Latin1_General_BIN2) > 0;
            IF @Qtd > 0
            BEGIN
              SELECT @Ids = STRING_AGG(CONVERT(nvarchar(36), x.[Id]), N', ') FROM (
                SELECT TOP (20) [Id] FROM [Usuarios]
                WHERE DATALENGTH([Email]) / 2 > 100 OR DATALENGTH([EmailNormalizado]) / 2 > 100
                   OR PATINDEX(N'%[^ -~]%', [Email] COLLATE Latin1_General_BIN2) > 0
                   OR PATINDEX(N'%[^ -~]%', [EmailNormalizado] COLLATE Latin1_General_BIN2) > 0
                ORDER BY [Id]) x;
              DECLARE @Msg nvarchar(2048) = CONCAT(N'AddUsuariosDadosPessoais interrompida, nenhum dado alterado: ', @Qtd,
                N' usuario(s) com Email/EmailNormalizado acima de 100 caracteres ou com caractere fora do ASCII imprimivel ',
                N'(Usuarios.Id, ate 20: ', @Ids, N'). Corrija esses cadastros (com contexto de auditoria) e reaplique a migration.');
              THROW 50031, @Msg, 1;
            END
            """;

        // Mesma normalização de UsuariosService.NormalizarCpf: sem '.', '-' e espaços externos; vazio = NULL.
        private const string VerificarCpfs = """
            DECLARE @Msg nvarchar(2048);
            IF EXISTS (SELECT 1 FROM [Usuarios] WHERE [Cpf] IS NOT NULL
                       AND (LTRIM(RTRIM(REPLACE(REPLACE([Cpf], '.', ''), '-', ''))) = ''
                            OR DATALENGTH([Cpf]) <> DATALENGTH(LTRIM(RTRIM(REPLACE(REPLACE([Cpf], '.', ''), '-', ''))))))
            BEGIN
              SET @Msg = N'AddUsuariosDadosPessoais interrompida, nenhum dado alterado: ha Usuarios.Cpf fora da forma normalizada (com pontos, hifen, espacos externos ou vazio). Normalize-os explicitamente e reaplique a migration.';
              THROW 50032, @Msg, 1;
            END
            IF EXISTS (SELECT 1 FROM [Usuarios] WHERE [Cpf] IS NOT NULL
                       GROUP BY LTRIM(RTRIM(REPLACE(REPLACE([Cpf], '.', ''), '-', ''))) HAVING COUNT(*) > 1)
            BEGIN
              SET @Msg = N'AddUsuariosDadosPessoais interrompida, nenhum dado alterado: ha CPFs repetidos apos a normalizacao; o indice unico UX_Usuarios_Cpf nao pode ser criado. Resolva os conflitos e reaplique a migration.';
              THROW 50033, @Msg, 1;
            END
            """;

        // Igual à anterior, com Cpf, DataNascimento e Telefone: copiados do estado ANTERIOR (deleted) e considerados mudança
        // cadastral. EXCEPT compara NULL com NULL como iguais (o "<>" não detectaria NULL -> valor); BIN2 detecta mudança só
        // de caixa. Continua set-based (UPDATE de várias linhas gera uma linha de histórico por usuário alterado) e roda na
        // mesma instrução/transação do UPDATE: falha no INSERT do histórico desfaz a alteração.
        private const string TriggerComDadosPessoais = """
            CREATE OR ALTER TRIGGER [TR_Usuarios_Historico] ON [Usuarios] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id
                             WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                                OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                                OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                                OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                                OR EXISTS (SELECT i.Cpf COLLATE Latin1_General_BIN2, i.DataNascimento, i.Telefone COLLATE Latin1_General_BIN2
                                           EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2)) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50021, 'Contexto de auditoria obrigatório.', 1;
              DECLARE @Login nvarchar(256)=COALESCE((SELECT d.Email FROM deleted d WHERE d.Id=@Usuario),
                                                    (SELECT u.Email FROM [Usuarios] u WHERE u.Id=@Usuario));
              INSERT INTO [_usuarios_hist] ([Id],[UsuarioId],[Nome],[Email],[Cpf],[DataNascimento],[Telefone],[CargoId],[CargoNome],[Ativo],[DataCriacao],[TipoOperacao],[UsuarioResponsavelId],[UsuarioResponsavelLogin],[IpResponsavel],[AlteradoEmUtc])
              SELECT NEWID(),d.Id,d.Nome,d.Email,d.Cpf,d.DataNascimento,d.Telefone,d.CargoId,c.Nome,d.Ativo,d.DataCriacao,
                     CASE WHEN d.Ativo=1 AND i.Ativo=0 THEN 'DELETE' ELSE 'UPDATE' END,
                     @Usuario,@Login,@Ip,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id JOIN [Cargos] c ON c.Id=d.CargoId
              WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                 OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                 OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                 OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                 OR EXISTS (SELECT i.Cpf COLLATE Latin1_General_BIN2, i.DataNascimento, i.Telefone COLLATE Latin1_General_BIN2
                            EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2);
            END
            """;

        // Definição de AddUsuariosAtivoEHistorico, restaurada no Down (cópia literal; aquela migration não é alterada).
        private const string TriggerAnterior = """
            CREATE OR ALTER TRIGGER [TR_Usuarios_Historico] ON [Usuarios] AFTER UPDATE AS
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
