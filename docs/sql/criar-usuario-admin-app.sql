/*
  Cria o login ADMINISTRATIVO da aplicação (ConnectionStrings:AlmiranteAdmin), para substituir o uso
  de "sa" nessa connection string.

  Por que isso importa: a API mantém essa credencial no próprio processo (variável de ambiente do
  container) porque precisa dela para as migrations e para rotacionar a senha do usuário de runtime
  (DbCredentials:AppUser). Enquanto ela for "sa", ler o ambiente do container — um RCE, um dump de
  memória, um log indevido — entrega sysadmin no servidor inteiro: outros bancos, xp_cmdshell,
  leitura direta de hash de senha e de refresh token, e a capacidade de apagar dbo.lancamentos_deletados
  (o DENY de auditoria vale para a role da aplicação, não para sysadmin). Todo o trabalho de menor
  privilégio do AppUser deixa de valer na prática. Com este login, o alcance passa a ser UM banco.

  O que ele recebe (e por quê):
    - ALTER ANY LOGIN (servidor)  -> CREATE/ALTER LOGIN do AppUser a cada rotação (DbCredentialManager)
    - db_ddladmin  (banco)        -> migrations (CREATE/ALTER TABLE, índices, triggers, constraints)
    - db_datareader/db_datawriter -> seed idempotente (DbSeeder) e as validações de startup
    - db_securityadmin (banco)    -> CREATE ROLE/ALTER ROLE e GRANT/DENY/REVOKE do provisionamento
    - ALTER ANY USER (banco)      -> CREATE USER/ALTER USER do AppUser. Concedida explicitamente em vez
                                     de via db_accessadmin, que traria junto CREATE SCHEMA e
                                     CONNECT WITH GRANT OPTION, desnecessários aqui.
  O que ele NÃO recebe: sysadmin, securityadmin, CONTROL SERVER, IMPERSONATE ANY LOGIN, acesso a
  qualquer outro banco, nem permissão de criar banco.

  Como usar (como sa/sysadmin), com a senha por variável de ambiente para não ficar em histórico de shell:
      export APP_ADMIN_PASSWORD='<senha forte, 24+ caracteres, sem aspas simples>'
      sqlcmd -S localhost,14330 -U sa -C -v DB_NAME=almirante ADMIN_USER=almirante_admin_bd -i docs/sql/criar-usuario-admin-app.sql
  Depois, no .env deste ambiente:
      SQL_ADMIN_USER=almirante_admin_bd
      SQL_ADMIN_PASSWORD=<a mesma senha>
  e, quando confirmar que a API sobe normalmente, trave a regressão com:
      SQL_ADMIN_PRIVILEGE_CHECK=Enforce

  ATENÇÃO — o banco precisa EXISTIR antes: este login não tem permissão de CREATE DATABASE (de
  propósito). Num ambiente novo, suba uma vez com "sa" para o banco ser criado e as migrations
  rodarem, e só então troque a connection string. Num ambiente já em uso, o banco já existe.

  NOTA sobre a senha: o sqlcmd substitui $(APP_ADMIN_PASSWORD) como TEXTO dentro do literal abaixo.
  Uma senha contendo aspa simples (') quebra o script. Use letras, dígitos e símbolos sem aspas.

  Idempotente: reexecutar apenas redefine a senha e reaplica as permissões.
*/
:on error exit
USE master;
GO

IF SUSER_ID(N'$(ADMIN_USER)') IS NULL
    CREATE LOGIN [$(ADMIN_USER)] WITH PASSWORD = N'$(APP_ADMIN_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DB_NAME)];
ELSE
BEGIN
    ALTER LOGIN [$(ADMIN_USER)] WITH PASSWORD = N'$(APP_ADMIN_PASSWORD)';
    ALTER LOGIN [$(ADMIN_USER)] ENABLE;
END
GO

-- Nenhum papel de servidor. Remove qualquer um herdado de uma execução anterior ou de provisionamento manual.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER SERVER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(l.name) + N'; '
FROM sys.server_role_members m
JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
JOIN sys.server_principals l ON l.principal_id = m.member_principal_id
WHERE l.name = N'$(ADMIN_USER)';
EXEC sys.sp_executesql @cmd;
GO

-- Única permissão de servidor: trocar a senha do login da aplicação (rotação do DbCredentials:AppUser).
-- ALTER ANY LOGIN não permite criar/alterar papéis de servidor nem se tornar sysadmin.
GRANT ALTER ANY LOGIN TO [$(ADMIN_USER)];
-- Não deve enxergar a existência de outros bancos do servidor.
DENY VIEW ANY DATABASE TO [$(ADMIN_USER)];
GO

USE [$(DB_NAME)];
GO

IF DATABASE_PRINCIPAL_ID(N'$(ADMIN_USER)') IS NULL
    CREATE USER [$(ADMIN_USER)] FOR LOGIN [$(ADMIN_USER)] WITH DEFAULT_SCHEMA = [dbo];
GO

-- Normaliza: sai de db_owner e de qualquer papel que não seja o conjunto mínimo abaixo.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N'; '
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name = N'$(ADMIN_USER)'
  AND r.name NOT IN (N'db_ddladmin', N'db_datareader', N'db_datawriter', N'db_securityadmin');
EXEC sys.sp_executesql @cmd;
GO

-- Nenhuma permissão direta herdada de provisionamento manual anterior, exceto CONNECT e o
-- ALTER ANY USER concedido logo abaixo.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
        WHEN 4 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'ROLE::' ELSE N'USER::' END + QUOTENAME(alvo.name) END
    + N' FROM ' + QUOTENAME(g.name) + N'; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
LEFT JOIN sys.database_principals alvo ON p.class = 4 AND alvo.principal_id = p.major_id
WHERE g.name = N'$(ADMIN_USER)' AND p.permission_name NOT IN (N'CONNECT', N'ALTER ANY USER')
  AND p.class IN (0, 1, 3, 4) AND p.minor_id = 0
  AND (p.class <> 4 OR alvo.principal_id IS NOT NULL);
EXEC sys.sp_executesql @cmd;
GO

ALTER ROLE [db_ddladmin]      ADD MEMBER [$(ADMIN_USER)];
ALTER ROLE [db_datareader]    ADD MEMBER [$(ADMIN_USER)];
ALTER ROLE [db_datawriter]    ADD MEMBER [$(ADMIN_USER)];
ALTER ROLE [db_securityadmin] ADD MEMBER [$(ADMIN_USER)];
-- CREATE USER/ALTER USER do login da aplicação (DbCredentialManager.BuildProvisionSql). Sem isto o
-- provisionamento falha com "O usuário não tem permissão para executar esta ação" (erro 15247).
GRANT ALTER ANY USER TO [$(ADMIN_USER)];
GO

-- Conferência.
-- Escopo de SERVIDOR: consultado direto em sys.server_role_members/sys.server_permissions. NÃO use
-- EXECUTE AS USER aqui: sob impersonação de escopo de banco, IS_SRVROLEMEMBER devolve NULL e um
-- ISNULL(...,0) faria a verificação relatar "0" mesmo para um login que É sysadmin.
SELECT
    (SELECT COUNT(*) FROM sys.server_role_members m
      JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
      JOIN sys.server_principals l ON l.principal_id = m.member_principal_id
     WHERE l.name = N'$(ADMIN_USER)')                                                      AS papeis_de_servidor,  -- deve ser 0
    (SELECT COUNT(*) FROM sys.server_permissions p
      JOIN sys.server_principals l ON l.principal_id = p.grantee_principal_id
     WHERE l.name = N'$(ADMIN_USER)' AND p.state_desc = 'GRANT'
       AND p.permission_name IN ('CONTROL SERVER', 'IMPERSONATE ANY LOGIN', 'ALTER ANY SERVER ROLE')) AS permissoes_perigosas, -- 0
    (SELECT COUNT(*) FROM sys.server_permissions p
      JOIN sys.server_principals l ON l.principal_id = p.grantee_principal_id
     WHERE l.name = N'$(ADMIN_USER)' AND p.state_desc = 'GRANT' AND p.permission_name = 'ALTER ANY LOGIN') AS pode_rotacionar_senha; -- 1
GO

-- Escopo de BANCO: aqui a impersonação é adequada (as funções consultadas são de banco).
EXECUTE AS USER = N'$(ADMIN_USER)';
SELECT
    ISNULL(IS_MEMBER(N'db_owner'), 0)                                          AS db_owner,   -- 0
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)      AS ddl,        -- 1
    ISNULL(HAS_PERMS_BY_NAME(N'dbo.Usuarios', N'OBJECT', N'SELECT'), 0)        AS le_dados;   -- 1
REVERT;
GO
