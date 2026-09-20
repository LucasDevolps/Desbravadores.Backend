/*
  Regulariza o usuário SQL da aplicação (DbCredentials:AppUser) de um banco JÁ provisionado por uma
  versão anterior — que pode ter ficado como db_owner, com permissões no esquema inteiro ou com papéis
  de servidor. Deixa o usuário exatamente com as permissões que a API concede a si mesma no startup
  (DbCredentialManager.BuildProvisionSql), inclusive sem escrita direta em lancamentos_deletados.

  A API já normaliza papéis/permissões DE BANCO sozinha a cada startup; este script existe para o que
  ela NÃO corrige (papéis de SERVIDOR, que fazem o startup recusar) e para quem quer regularizar antes de
  subir a versão nova. Não altera senha nem cria o login (isso continua sendo da API).

  Como usar (como sa/sysadmin):
      sqlcmd -S localhost,14330 -U sa -C -v DB_NAME=almirante APP_USER=almirante_user_bd -i docs/sql/corrigir-privilegios-usuario-app.sql
  Idempotente. Para a identidade Windows do IIS, use o mesmo roteiro trocando APP_USER por "DOMINIO\conta"
  e prefira habilitar DbCredentials:AppUser (a conta Windows passa a ser só a conexão administrativa).
*/
:on error exit
USE master;
GO
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER SERVER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(l.name) + N'; '
FROM sys.server_role_members m
JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
JOIN sys.server_principals l ON l.principal_id = m.member_principal_id
WHERE l.name = N'$(APP_USER)';
EXEC sys.sp_executesql @cmd;
GO

USE [$(DB_NAME)];
GO
IF DATABASE_PRINCIPAL_ID(N'almirante_app_role') IS NULL CREATE ROLE [almirante_app_role];
GO
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N'; '
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name = N'$(APP_USER)' AND r.name <> N'almirante_app_role';
-- Classe 4 (DATABASE_PRINCIPAL) incluída: um GRANT IMPERSONATE ON USER::dbo sobreviveria a toda a
-- normalização de papéis acima e daria EXECUTE AS USER = 'dbo', ou seja, db_owner efetivo.
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
        WHEN 4 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'ROLE::' ELSE N'USER::' END + QUOTENAME(alvo.name) END
    + N' FROM ' + QUOTENAME(g.name) + N'; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
LEFT JOIN sys.database_principals alvo ON p.class = 4 AND alvo.principal_id = p.major_id
WHERE g.name IN (N'$(APP_USER)', N'almirante_app_role') AND p.permission_name <> N'CONNECT' AND p.class IN (0, 1, 3, 4) AND p.minor_id = 0
  AND (p.class <> 4 OR alvo.principal_id IS NOT NULL);
IF IS_ROLEMEMBER(N'almirante_app_role', N'$(APP_USER)') = 0
    SET @cmd += N'ALTER ROLE [almirante_app_role] ADD MEMBER ' + QUOTENAME(N'$(APP_USER)') + N'; ';
SELECT @cmd += N'GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [almirante_app_role]; '
FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name NOT IN (N'lancamentos_deletados', N'__EFMigrationsHistory');
SELECT @cmd += N'GRANT SELECT ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [almirante_app_role]; '
FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name IN (N'lancamentos_deletados', N'__EFMigrationsHistory');
IF OBJECT_ID(N'dbo.AuthSessions', N'U') IS NOT NULL
    SET @cmd += N'GRANT DELETE ON OBJECT::dbo.AuthSessions TO [almirante_app_role]; ';
IF OBJECT_ID(N'dbo.lancamentos_deletados', N'U') IS NOT NULL
    SET @cmd += N'DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.lancamentos_deletados TO [almirante_app_role]; ';
EXEC sys.sp_executesql @cmd;
GO

-- Conferência, parte 1 — escopo de SERVIDOR (consultado direto: IS_SRVROLEMEMBER devolve NULL sob
-- EXECUTE AS USER, o que faria a verificação relatar 0 mesmo para um login com papel de servidor).
SELECT (SELECT COUNT(*) FROM sys.server_role_members m
         JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
         JOIN sys.server_principals l ON l.principal_id = m.member_principal_id
        WHERE l.name = N'$(APP_USER)') AS papeis_de_servidor; -- deve ser 0
GO

-- Conferência, parte 2 — escopo de BANCO: todas as colunas devem ser 0.
EXECUTE AS USER = N'$(APP_USER)';
SELECT
    ISNULL(IS_MEMBER(N'db_owner'), 0)                                                 AS db_owner,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)             AS ddl,
    ISNULL(HAS_PERMS_BY_NAME(N'dbo.Lancamentos', N'OBJECT', N'DELETE'), 0)            AS delete_lancamentos,
    ISNULL(HAS_PERMS_BY_NAME(N'dbo.lancamentos_deletados', N'OBJECT', N'INSERT'), 0)  AS escreve_auditoria,
    ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'USER', N'IMPERSONATE'), 0)                     AS impersonar_dbo;
REVERT;
GO
