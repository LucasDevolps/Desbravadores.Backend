using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// EhCin (bit NOT NULL DEFAULT 0: cadastros existentes ficam false), DataExpiracao (date NULL: nenhuma data é inventada) e o endereço
    /// estruturado (15 colunas varchar NULL, nenhum endereço é inventado) em Usuarios e _usuarios_hist; em _usuarios_hist EhCin é NULL nas
    /// linhas anteriores (o campo não existia). CHECK CK_Usuarios_Endereco (sem CEP nenhum campo de endereço; com CEP, 8 dígitos, cidade
    /// e UF). TR_Usuarios_Historico passa a comparar e copiar as colunas novas. Nenhuma linha existente é alterada.
    /// </summary>
    public partial class AddUsuariosCinExpiracaoEndereco : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DataExpiracao",
                table: "Usuarios",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EhCin",
                table: "Usuarios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoBairro",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoCep",
                table: "Usuarios",
                type: "varchar(8)",
                unicode: false,
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoComplemento",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoComplementoCep",
                table: "Usuarios",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoDdd",
                table: "Usuarios",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoEstado",
                table: "Usuarios",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoGia",
                table: "Usuarios",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoIbge",
                table: "Usuarios",
                type: "varchar(7)",
                unicode: false,
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoLocalidade",
                table: "Usuarios",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoLogradouro",
                table: "Usuarios",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoNumero",
                table: "Usuarios",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoRegiao",
                table: "Usuarios",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoSiafi",
                table: "Usuarios",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoUf",
                table: "Usuarios",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoUnidade",
                table: "Usuarios",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DataExpiracao",
                table: "_usuarios_hist",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EhCin",
                table: "_usuarios_hist",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoBairro",
                table: "_usuarios_hist",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoCep",
                table: "_usuarios_hist",
                type: "varchar(8)",
                unicode: false,
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoComplemento",
                table: "_usuarios_hist",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoComplementoCep",
                table: "_usuarios_hist",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoDdd",
                table: "_usuarios_hist",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoEstado",
                table: "_usuarios_hist",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoGia",
                table: "_usuarios_hist",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoIbge",
                table: "_usuarios_hist",
                type: "varchar(7)",
                unicode: false,
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoLocalidade",
                table: "_usuarios_hist",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoLogradouro",
                table: "_usuarios_hist",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoNumero",
                table: "_usuarios_hist",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoRegiao",
                table: "_usuarios_hist",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoSiafi",
                table: "_usuarios_hist",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoUf",
                table: "_usuarios_hist",
                type: "varchar(2)",
                unicode: false,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnderecoUnidade",
                table: "_usuarios_hist",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Usuarios_Endereco",
                table: "Usuarios",
                sql: "([EnderecoCep] IS NULL AND [EnderecoLogradouro] IS NULL AND [EnderecoNumero] IS NULL AND [EnderecoComplemento] IS NULL AND [EnderecoComplementoCep] IS NULL AND [EnderecoUnidade] IS NULL AND [EnderecoBairro] IS NULL AND [EnderecoLocalidade] IS NULL AND [EnderecoUf] IS NULL AND [EnderecoEstado] IS NULL AND [EnderecoRegiao] IS NULL AND [EnderecoIbge] IS NULL AND [EnderecoGia] IS NULL AND [EnderecoDdd] IS NULL AND [EnderecoSiafi] IS NULL) OR ([EnderecoCep] IS NOT NULL AND DATALENGTH([EnderecoCep]) = 8 AND [EnderecoCep] COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9]%' AND [EnderecoLocalidade] IS NOT NULL AND [EnderecoUf] IS NOT NULL AND [EnderecoUf] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z]')");

            migrationBuilder.Sql(TriggerComCinExpiracaoEndereco);
        }

        /// <summary>
        /// Rollback estrutural: restaura a trigger de AddUsuariosRgTipoSanguineoDoencas ANTES de remover as colunas que a nova definição
        /// referencia, depois remove a CHECK e as colunas. ATENÇÃO: EhCin, DataExpiracao e o endereço de Usuarios E de _usuarios_hist são
        /// PERDIDOS de forma irreversível. Faça backup antes; não use como rollback de produção (ver docs/release-process.md).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TriggerAnterior);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Usuarios_Endereco",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "DataExpiracao",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EhCin",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoBairro",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoCep",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoComplemento",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoComplementoCep",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoDdd",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoEstado",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoGia",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoIbge",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoLocalidade",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoLogradouro",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoNumero",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoRegiao",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoSiafi",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoUf",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "EnderecoUnidade",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "DataExpiracao",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EhCin",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoBairro",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoCep",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoComplemento",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoComplementoCep",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoDdd",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoEstado",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoGia",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoIbge",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoLocalidade",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoLogradouro",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoNumero",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoRegiao",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoSiafi",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoUf",
                table: "_usuarios_hist");

            migrationBuilder.DropColumn(
                name: "EnderecoUnidade",
                table: "_usuarios_hist");
        }

        private const string ComparadasInserted = """
            i.Cpf COLLATE Latin1_General_BIN2, i.DataNascimento, i.Telefone COLLATE Latin1_General_BIN2,
                                   i.ResponsavelNome COLLATE Latin1_General_BIN2, i.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                   i.ResponsavelCpf COLLATE Latin1_General_BIN2, i.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                   i.FotoCaminho COLLATE Latin1_General_BIN2, i.Rg COLLATE Latin1_General_BIN2,
                                   i.TipoSanguineo COLLATE Latin1_General_BIN2, i.DoencasAlergias COLLATE Latin1_General_BIN2,
                                   i.EhCin, i.DataExpiracao,
                                   i.EnderecoCep COLLATE Latin1_General_BIN2, i.EnderecoLogradouro COLLATE Latin1_General_BIN2,
                                   i.EnderecoNumero COLLATE Latin1_General_BIN2, i.EnderecoComplemento COLLATE Latin1_General_BIN2,
                                   i.EnderecoComplementoCep COLLATE Latin1_General_BIN2, i.EnderecoUnidade COLLATE Latin1_General_BIN2,
                                   i.EnderecoBairro COLLATE Latin1_General_BIN2, i.EnderecoLocalidade COLLATE Latin1_General_BIN2,
                                   i.EnderecoUf COLLATE Latin1_General_BIN2, i.EnderecoEstado COLLATE Latin1_General_BIN2,
                                   i.EnderecoRegiao COLLATE Latin1_General_BIN2, i.EnderecoIbge COLLATE Latin1_General_BIN2,
                                   i.EnderecoGia COLLATE Latin1_General_BIN2, i.EnderecoDdd COLLATE Latin1_General_BIN2,
                                   i.EnderecoSiafi COLLATE Latin1_General_BIN2
            """;

        private const string ComparadasDeleted = """
            d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2,
                                   d.ResponsavelNome COLLATE Latin1_General_BIN2, d.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                   d.ResponsavelCpf COLLATE Latin1_General_BIN2, d.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                   d.FotoCaminho COLLATE Latin1_General_BIN2, d.Rg COLLATE Latin1_General_BIN2,
                                   d.TipoSanguineo COLLATE Latin1_General_BIN2, d.DoencasAlergias COLLATE Latin1_General_BIN2,
                                   d.EhCin, d.DataExpiracao,
                                   d.EnderecoCep COLLATE Latin1_General_BIN2, d.EnderecoLogradouro COLLATE Latin1_General_BIN2,
                                   d.EnderecoNumero COLLATE Latin1_General_BIN2, d.EnderecoComplemento COLLATE Latin1_General_BIN2,
                                   d.EnderecoComplementoCep COLLATE Latin1_General_BIN2, d.EnderecoUnidade COLLATE Latin1_General_BIN2,
                                   d.EnderecoBairro COLLATE Latin1_General_BIN2, d.EnderecoLocalidade COLLATE Latin1_General_BIN2,
                                   d.EnderecoUf COLLATE Latin1_General_BIN2, d.EnderecoEstado COLLATE Latin1_General_BIN2,
                                   d.EnderecoRegiao COLLATE Latin1_General_BIN2, d.EnderecoIbge COLLATE Latin1_General_BIN2,
                                   d.EnderecoGia COLLATE Latin1_General_BIN2, d.EnderecoDdd COLLATE Latin1_General_BIN2,
                                   d.EnderecoSiafi COLLATE Latin1_General_BIN2
            """;

        private const string TriggerComCinExpiracaoEndereco = $"""
            CREATE OR ALTER TRIGGER [TR_Usuarios_Historico] ON [Usuarios] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id
                             WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                                OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                                OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                                OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                                OR EXISTS (SELECT {ComparadasInserted}
                                           EXCEPT SELECT {ComparadasDeleted})) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50021, 'Contexto de auditoria obrigatório.', 1;
              DECLARE @Login nvarchar(256)=COALESCE((SELECT d.Email FROM deleted d WHERE d.Id=@Usuario),
                                                    (SELECT u.Email FROM [Usuarios] u WHERE u.Id=@Usuario));
              INSERT INTO [_usuarios_hist] ([Id],[UsuarioId],[Nome],[Email],[Cpf],[DataNascimento],[Telefone],[ResponsavelNome],[ResponsavelEmail],[ResponsavelCpf],[ResponsavelTelefone],[FotoCaminho],[Rg],[TipoSanguineo],[DoencasAlergias],
                                            [EhCin],[DataExpiracao],[EnderecoCep],[EnderecoLogradouro],[EnderecoNumero],[EnderecoComplemento],[EnderecoComplementoCep],[EnderecoUnidade],[EnderecoBairro],[EnderecoLocalidade],[EnderecoUf],[EnderecoEstado],[EnderecoRegiao],[EnderecoIbge],[EnderecoGia],[EnderecoDdd],[EnderecoSiafi],
                                            [CargoId],[CargoNome],[Ativo],[DataCriacao],[TipoOperacao],[UsuarioResponsavelId],[UsuarioResponsavelLogin],[IpResponsavel],[AlteradoEmUtc])
              SELECT NEWID(),d.Id,d.Nome,d.Email,d.Cpf,d.DataNascimento,d.Telefone,d.ResponsavelNome,d.ResponsavelEmail,d.ResponsavelCpf,d.ResponsavelTelefone,d.FotoCaminho,d.Rg,d.TipoSanguineo,d.DoencasAlergias,
                     d.EhCin,d.DataExpiracao,d.EnderecoCep,d.EnderecoLogradouro,d.EnderecoNumero,d.EnderecoComplemento,d.EnderecoComplementoCep,d.EnderecoUnidade,d.EnderecoBairro,d.EnderecoLocalidade,d.EnderecoUf,d.EnderecoEstado,d.EnderecoRegiao,d.EnderecoIbge,d.EnderecoGia,d.EnderecoDdd,d.EnderecoSiafi,
                     d.CargoId,c.Nome,d.Ativo,d.DataCriacao,
                     CASE WHEN d.Ativo=1 AND i.Ativo=0 THEN 'DELETE' ELSE 'UPDATE' END,
                     @Usuario,@Login,@Ip,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id JOIN [Cargos] c ON c.Id=d.CargoId
              WHERE i.Nome COLLATE Latin1_General_BIN2 <> d.Nome COLLATE Latin1_General_BIN2
                 OR i.Email COLLATE Latin1_General_BIN2 <> d.Email COLLATE Latin1_General_BIN2
                 OR i.EmailNormalizado COLLATE Latin1_General_BIN2 <> d.EmailNormalizado COLLATE Latin1_General_BIN2
                 OR i.CargoId <> d.CargoId OR i.Ativo <> d.Ativo
                 OR EXISTS (SELECT {ComparadasInserted}
                            EXCEPT SELECT {ComparadasDeleted});
            END
            """;

        private const string TriggerAnterior = """
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
                                                  i.FotoCaminho COLLATE Latin1_General_BIN2, i.Rg COLLATE Latin1_General_BIN2,
                                                  i.TipoSanguineo COLLATE Latin1_General_BIN2, i.DoencasAlergias COLLATE Latin1_General_BIN2
                                           EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2,
                                                  d.ResponsavelNome COLLATE Latin1_General_BIN2, d.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                                  d.ResponsavelCpf COLLATE Latin1_General_BIN2, d.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                                  d.FotoCaminho COLLATE Latin1_General_BIN2, d.Rg COLLATE Latin1_General_BIN2,
                                                  d.TipoSanguineo COLLATE Latin1_General_BIN2, d.DoencasAlergias COLLATE Latin1_General_BIN2)) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50021, 'Contexto de auditoria obrigatório.', 1;
              DECLARE @Login nvarchar(256)=COALESCE((SELECT d.Email FROM deleted d WHERE d.Id=@Usuario),
                                                    (SELECT u.Email FROM [Usuarios] u WHERE u.Id=@Usuario));
              INSERT INTO [_usuarios_hist] ([Id],[UsuarioId],[Nome],[Email],[Cpf],[DataNascimento],[Telefone],[ResponsavelNome],[ResponsavelEmail],[ResponsavelCpf],[ResponsavelTelefone],[FotoCaminho],[Rg],[TipoSanguineo],[DoencasAlergias],[CargoId],[CargoNome],[Ativo],[DataCriacao],[TipoOperacao],[UsuarioResponsavelId],[UsuarioResponsavelLogin],[IpResponsavel],[AlteradoEmUtc])
              SELECT NEWID(),d.Id,d.Nome,d.Email,d.Cpf,d.DataNascimento,d.Telefone,d.ResponsavelNome,d.ResponsavelEmail,d.ResponsavelCpf,d.ResponsavelTelefone,d.FotoCaminho,d.Rg,d.TipoSanguineo,d.DoencasAlergias,d.CargoId,c.Nome,d.Ativo,d.DataCriacao,
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
                                   i.FotoCaminho COLLATE Latin1_General_BIN2, i.Rg COLLATE Latin1_General_BIN2,
                                   i.TipoSanguineo COLLATE Latin1_General_BIN2, i.DoencasAlergias COLLATE Latin1_General_BIN2
                            EXCEPT SELECT d.Cpf COLLATE Latin1_General_BIN2, d.DataNascimento, d.Telefone COLLATE Latin1_General_BIN2,
                                   d.ResponsavelNome COLLATE Latin1_General_BIN2, d.ResponsavelEmail COLLATE Latin1_General_BIN2,
                                   d.ResponsavelCpf COLLATE Latin1_General_BIN2, d.ResponsavelTelefone COLLATE Latin1_General_BIN2,
                                   d.FotoCaminho COLLATE Latin1_General_BIN2, d.Rg COLLATE Latin1_General_BIN2,
                                   d.TipoSanguineo COLLATE Latin1_General_BIN2, d.DoencasAlergias COLLATE Latin1_General_BIN2);
            END
            """;
    }
}
