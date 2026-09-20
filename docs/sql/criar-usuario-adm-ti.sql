/*
  Cria o login "adm-ti" para CONSULTAS internas de TI: somente leitura, restrito a views do esquema [ti]
  do banco da aplicação. NÃO é sysadmin e não tem acesso às tabelas, a outros bancos, nem a dados
  sensíveis (hash de senha, tokens de refresh). Escrita, DDL e administração ficam de fora de propósito:
  para manutenção de emergência use uma identidade administrativa SEPARADA (ex.: o próprio sa guardado
  em cofre), nunca esta.

  Como usar (como sa/sysadmin), passando a senha por variável de ambiente para ela não aparecer na
  linha de comando nem em histórico de shell:
      export ADM_TI_PASSWORD='<senha forte, 16+ caracteres>'
      sqlcmd -S localhost,14330 -U sa -C -v DB_NAME=almirante -i docs/sql/criar-usuario-adm-ti.sql
  O login precisa de "Mixed Mode" (autenticação SQL) habilitado no servidor — o container do Compose
  já vem assim. Nunca versione a senha real.

  Idempotente e regularizador: se "adm-ti" já existir (criado pela versão anterior deste script, que o
  fazia sysadmin), a execução remove o papel sysadmin e demais papéis privilegiados, reaplica as
  permissões abaixo e redefine a senha. Após regularizar, considere a senha antiga comprometida.
*/
:on error exit
USE master;
GO

IF SUSER_ID(N'adm-ti') IS NULL
    CREATE LOGIN [adm-ti] WITH PASSWORD = N'$(ADM_TI_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DB_NAME)];
ELSE
BEGIN
    ALTER LOGIN [adm-ti] WITH PASSWORD = N'$(ADM_TI_PASSWORD)';
    ALTER LOGIN [adm-ti] ENABLE;
END
GO

-- Remove qualquer papel de servidor (a versão anterior deste script concedia sysadmin).
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER SERVER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER [adm-ti]; '
FROM sys.server_role_members m
JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
JOIN sys.server_principals l ON l.principal_id = m.member_principal_id
WHERE l.name = N'adm-ti';
EXEC sys.sp_executesql @cmd;
GO

USE [$(DB_NAME)];
GO

IF SCHEMA_ID(N'ti') IS NULL EXEC(N'CREATE SCHEMA [ti] AUTHORIZATION [dbo]');
GO

IF DATABASE_PRINCIPAL_ID(N'adm-ti') IS NULL
    CREATE USER [adm-ti] FOR LOGIN [adm-ti] WITH DEFAULT_SCHEMA = [ti];
GO

IF DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura') IS NULL
    CREATE ROLE [almirante_ti_leitura];
GO

-- Normaliza: o usuário só pode pertencer à role de leitura (sai de db_owner, db_datareader etc.) e não
-- mantém permissões diretas; tudo passa a vir da role.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER [adm-ti]; '
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name = N'adm-ti' AND r.name <> N'almirante_ti_leitura';
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id)) END
    + N' FROM ' + QUOTENAME(g.name) + N'; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
WHERE g.name IN (N'adm-ti', N'almirante_ti_leitura') AND p.permission_name <> N'CONNECT' AND p.class IN (0, 1, 3) AND p.minor_id = 0;
IF IS_ROLEMEMBER(N'almirante_ti_leitura', N'adm-ti') = 0
    SET @cmd += N'ALTER ROLE [almirante_ti_leitura] ADD MEMBER [adm-ti]; ';
EXEC sys.sp_executesql @cmd;
GO

-- Views de consulta. Cada uma lista as colunas permitidas (nunca SenhaHash nem tokens); como o esquema
-- [ti] pertence ao mesmo dono (dbo) das tabelas, a cadeia de propriedade dispensa qualquer permissão
-- direta nas tabelas.
CREATE OR ALTER VIEW [ti].[Usuarios] AS
    SELECT Id, Nome, Email, CargoId, DataCriacao, FalhasLoginConsecutivas, UltimaFalhaLoginUtc, LoginBloqueadoAteUtc
    FROM dbo.Usuarios;
GO
CREATE OR ALTER VIEW [ti].[Cargos] AS SELECT * FROM dbo.Cargos;
GO
CREATE OR ALTER VIEW [ti].[Lancamentos] AS SELECT * FROM dbo.Lancamentos;
GO
CREATE OR ALTER VIEW [ti].[lancamentos_deletados] AS SELECT * FROM dbo.lancamentos_deletados;
GO
CREATE OR ALTER VIEW [ti].[AuthSessions] AS
    SELECT Id, UsuarioId, CreatedAtUtc, LastRenewedAtUtc, AbsoluteExpiresAtUtc, RevokedAtUtc, RevocationReason
    FROM dbo.AuthSessions;
GO

GRANT SELECT ON SCHEMA::[ti] TO [almirante_ti_leitura];
-- Defesa em profundidade: nenhuma leitura direta das tabelas, mesmo que alguém conceda algo por engano.
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::[dbo] TO [almirante_ti_leitura];
GO

-- Conferência: deve retornar 0 em todas as colunas, exceto ler_view = 1.
EXECUTE AS USER = N'adm-ti';
SELECT
    ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)                                   AS sysadmin,
    ISNULL(IS_MEMBER(N'db_owner'), 0)                                          AS db_owner,
    ISNULL(HAS_PERMS_BY_NAME(N'dbo.Usuarios', N'OBJECT', N'SELECT'), 0)        AS ler_tabela_usuarios,
    ISNULL(HAS_PERMS_BY_NAME(N'ti.Usuarios', N'OBJECT', N'SELECT'), 0)         AS ler_view,
    ISNULL(HAS_PERMS_BY_NAME(N'ti.Usuarios', N'OBJECT', N'UPDATE'), 0)         AS escrever_view,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)      AS ddl;
REVERT;
GO
