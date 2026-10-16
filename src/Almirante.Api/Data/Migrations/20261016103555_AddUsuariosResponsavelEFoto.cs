using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Dados do responsável (issues #112 e #131) e caminho da foto de perfil (issue #116) em Usuarios e _usuarios_hist, todos
    /// varchar e NULL nos registros existentes (nenhum valor é inventado); CHECK CK_Usuarios_FotoCaminho (formato e pasta do
    /// próprio usuário); TR_Usuarios_Historico passa a comparar e copiar as colunas novas. Interrompe (THROW, transação da
    /// migration desfeita) se a collation do banco não for da página de código 1252, em que o nome do responsável é validado.
    /// </summary>
    public partial class AddUsuariosResponsavelEFoto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Antes de qualquer alteração: ResponsavelNome é varchar (desempenho, nunca nvarchar) e a aplicação só aceita
            //    nele caracteres da página 1252 (TextoVarchar). Em outra página de código os acentos seriam trocados em silêncio.
            migrationBuilder.Sql(VerificarCollation);

            migrationBuilder.AddColumn<string>(
                name: "FotoCaminho",
                table: "Usuarios",
                type: "varchar(79)",
                unicode: false,
                maxLength: 79,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelCpf",
                table: "Usuarios",
                type: "varchar(11)",
                unicode: false,
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelEmail",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelNome",
                table: "Usuarios",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelTelefone",
                table: "Usuarios",
                type: "varchar(11)",
                unicode: false,
                maxLength: 11,
                nullable: true);

            // Histórico: mesmos tipos de Usuarios, NULL nas linhas antigas (o valor da época não existia).
            migrationBuilder.AddColumn<string>(
                name: "FotoCaminho",
                table: "_usuarios_hist",
                type: "varchar(79)",
                unicode: false,
                maxLength: 79,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelCpf",
                table: "_usuarios_hist",
                type: "varchar(11)",
                unicode: false,
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelEmail",
                table: "_usuarios_hist",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelNome",
                table: "_usuarios_hist",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelTelefone",
                table: "_usuarios_hist",
                type: "varchar(11)",
                unicode: false,
                maxLength: 11,
                nullable: true);

            // 2) Defesa em profundidade do caminho da foto (mesma constante do modelo, AlmiranteDbContext.CheckFotoCaminho).
            migrationBuilder.AddCheckConstraint(
                name: "CK_Usuarios_FotoCaminho",
                table: "Usuarios",
                sql: "[FotoCaminho] IS NULL OR (DATALENGTH([FotoCaminho]) = 79 AND [FotoCaminho] COLLATE Latin1_General_BIN2 LIKE 'usuarios/' + LOWER(REPLACE(CONVERT(char(36), [Id]), '-', '')) + '/%.webp' AND SUBSTRING([FotoCaminho], 43, 32) COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9a-f]%')");

            // 3) Trigger com as colunas novas (CREATE OR ALTER: mantém o objeto e as permissões, troca só a definição).
            migrationBuilder.Sql(TriggerComResponsavelEFoto);
        }

        /// <summary>
        /// Rollback estrutural: restaura a trigger de AddUsuariosDadosPessoais ANTES de remover as colunas que a nova
        /// definição referencia, depois remove a CHECK e as colunas. ATENÇÃO: os dados do responsável e os caminhos de foto de
        /// Usuarios E de _usuarios_hist são PERDIDOS de forma irreversível, e os arquivos de foto continuam na pasta
        /// Media:Storage:Path sem referência (apague-os à parte). Faça backup antes; não use como rollback de produção (ver
        /// docs/release-process.md, "Banco e migrations").
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TriggerAnterior);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Usuarios_FotoCaminho",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "FotoCaminho",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "ResponsavelCpf",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "ResponsavelEmail",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "ResponsavelNome",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "ResponsavelTelefone",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "FotoCaminho",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "ResponsavelCpf",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "ResponsavelEmail",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "ResponsavelNome",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "ResponsavelTelefone",
                table: "_usuarios_hist");
        }

        // Colunas novas herdam a collation do banco: só a página 1252 (Latin1) é aceita.
        private const string VerificarCollation = """
            DECLARE @Collation sysname = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'));
            IF CONVERT(int, COLLATIONPROPERTY(@Collation, 'CodePage')) <> 1252
            BEGIN
              DECLARE @Msg nvarchar(2048) = CONCAT(N'AddUsuariosResponsavelEFoto interrompida, nenhum dado alterado: a collation do banco (',
                @Collation, N') não é da página de código 1252. Os campos de texto novos são varchar e o nome do responsável é validado ',
                N'nessa página; use uma collation Latin1 (ex.: SQL_Latin1_General_CP1_CI_AS) e reaplique a migration.');
              THROW 50034, @Msg, 1;
            END
            """;

        // Igual à de AddUsuariosDadosPessoais, com os dados do responsável e FotoCaminho: copiados do estado ANTERIOR
        // (deleted) e considerados mudança cadastral (trocar ou remover a foto gera histórico). EXCEPT compara NULL com NULL
        // como iguais (o "<>" não detectaria NULL -> valor); BIN2 detecta mudança só de caixa. Continua set-based e na mesma
        // instrução/transação do UPDATE: falha no INSERT do histórico desfaz a alteração.
        private const string TriggerComResponsavelEFoto = """
            CREATE OR ALTER TRIGGER [TR_Usuarios_Historico] ON [Usuarios] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id
                             WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                                OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                                OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                                OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                                OR EXISTS (SELECT i.Cpf COLLATE Latin1_General_BIN2, i.DataNascimento, i.Telefone COLLATE Latin1_General_BIN2,
                                                  i.ResponsavelNome COLLATE Latin1_General_BIN2, i.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                                  i.ResponsavelCpf COLLATE Latin1_General_BIN2, i.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                                  i.FotoCaminho COLLATE Latin1_General_BIN2
                                           EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2,
                                                  d.ResponsavelNome COLLATE Latin1_General_BIN2, d.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                                  d.ResponsavelCpf COLLATE Latin1_General_BIN2, d.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                                  d.FotoCaminho COLLATE Latin1_General_BIN2)) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50021, 'Contexto de auditoria obrigatório.', 1;
              DECLARE @Login nvarchar(256)=COALESCE((SELECT d.Email FROM deleted d WHERE d.Id=@Usuario),
                                                    (SELECT u.Email FROM [Usuarios] u WHERE u.Id=@Usuario));
              INSERT INTO [_usuarios_hist] ([Id],[UsuarioId],[Nome],[Email],[Cpf],[DataNascimento],[Telefone],[ResponsavelNome],[ResponsavelEmail],[ResponsavelCpf],[ResponsavelTelefone],[FotoCaminho],[CargoId],[CargoNome],[Ativo],[DataCriacao],[TipoOperacao],[UsuarioResponsavelId],[UsuarioResponsavelLogin],[IpResponsavel],[AlteradoEmUtc])
              SELECT NEWID(),d.Id,d.Nome,d.Email,d.Cpf,d.DataNascimento,d.Telefone,d.ResponsavelNome,d.ResponsavelEmail,d.ResponsavelCpf,d.ResponsavelTelefone,d.FotoCaminho,d.CargoId,c.Nome,d.Ativo,d.DataCriacao,
                     CASE WHEN d.Ativo=1 AND i.Ativo=0 THEN 'DELETE' ELSE 'UPDATE' END,
                     @Usuario,@Login,@Ip,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id JOIN [Cargos] c ON c.Id=d.CargoId
              WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                 OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                 OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                 OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                 OR EXISTS (SELECT i.Cpf COLLATE Latin1_General_BIN2, i.DataNascimento, i.Telefone COLLATE Latin1_General_BIN2,
                                   i.ResponsavelNome COLLATE Latin1_General_BIN2, i.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                   i.ResponsavelCpf COLLATE Latin1_General_BIN2, i.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                   i.FotoCaminho COLLATE Latin1_General_BIN2
                            EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2,
                                   d.ResponsavelNome COLLATE Latin1_General_BIN2, d.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                   d.ResponsavelCpf COLLATE Latin1_General_BIN2, d.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                   d.FotoCaminho COLLATE Latin1_General_BIN2);
            END
            """;

        // Definição de AddUsuariosDadosPessoais, restaurada no Down (cópia literal; aquela migration não é alterada).
        private const string TriggerAnterior = """
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
    }
}
