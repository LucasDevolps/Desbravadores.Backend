/*
  BOOTSTRAP SQL da aplicação — roda FORA da API, como administrador do servidor SQL (sysadmin), e é
  idempotente. É o único lugar em que uma credencial privilegiada toca o banco da aplicação: depois
  dele, todo SQL executado pela API usa as duas identidades abaixo, nunca "sa".

  O que ele prepara:
    1. o banco da aplicação (se ainda não existir), com CONTAINMENT = PARTIAL e TRUSTWORTHY OFF;
    2. a identidade ADMINISTRATIVA da API ($(ADMIN_USER)) — migrations, concessões à role de runtime e
       rotação da senha da identidade de runtime;
    3. a identidade de RUNTIME ($(APP_USER)) — só existe aqui como "casca" sem permissão nenhuma; quem
       concede SELECT/INSERT/UPDATE (e DELETE só em AuthSessions) e troca a senha é a API, a cada
       startup (DbCredentialManager). A senha inicial é aleatória, descartável e nunca é exibida.
    4. a role dbo-owned almirante_app_role, da qual a identidade de runtime é o único membro.

  POR QUE USUÁRIOS CONTIDOS (sem login de servidor). As duas identidades são "contained database users"
  com senha: existem só dentro do banco da aplicação. Consequências, verificadas por testes contra SQL
  Server real (backend/Almirante.Api.Tests):
    - não têm NENHUMA permissão nem papel de servidor: não são sysadmin, não têm CONTROL SERVER,
      ALTER ANY LOGIN, ALTER ANY DATABASE, CREATE ANY DATABASE, IMPERSONATE ANY LOGIN etc. — nem poderiam
      ter, porque um usuário contido não é um principal de servidor;
    - não conseguem administrar outro banco nem redefinir a senha de logins de outras aplicações (o que
      um "ALTER ANY LOGIN" — necessário para rotacionar a senha de um LOGIN — permitiria);
    - a rotação da senha da identidade de runtime é um ALTER USER ... WITH PASSWORD, restrito ao banco.
  A connection string de ambas precisa de Database=<banco> (usuário contido autentica no banco).

  O que a identidade administrativa recebe, e por quê (nada além disto):
    - CREATE TABLE/VIEW/PROCEDURE/FUNCTION (banco)    -> objetos criados pelas migrations do EF Core
    - CONTROL ON SCHEMA::dbo                         -> DDL/DML nos objetos do schema dbo (migrations, seed,
                                                        CLI reset-admin-password) e GRANT/DENY/REVOKE das
                                                        tabelas para a role de runtime. NÃO é db_owner, NÃO é
                                                        CONTROL no banco, não pode virar db_owner nem criar
                                                        módulos EXECUTE AS OWNER de dbo (testado)
    - ALTER ANY USER (banco)                         -> ALTER USER ... WITH PASSWORD da identidade de runtime
                                                        (ALTER no usuário isolado NÃO basta — testado)
  O que NÃO recebe: sysadmin/qualquer papel de servidor, CONTROL SERVER, ALTER ANY LOGIN,
  IMPERSONATE, db_owner/db_ddladmin/db_securityadmin/db_accessadmin/db_datareader/db_datawriter,
  ALTER ANY ROLE, CONTROL no banco, permissão em outro banco.

  Como usar (o docker compose já faz isto no serviço "sql-bootstrap"; ver scripts/sql-bootstrap.sh).
  Manualmente, como sysadmin, com a senha por VARIÁVEL DE AMBIENTE (não vai para a linha de comando,
  para o histórico do shell nem para o log):
      export SQLCMDPASSWORD='<senha do sysadmin>'
      export APP_ADMIN_PASSWORD='<senha forte, 16+ caracteres, só A-Za-z0-9 e _.~+@%^*!#:->'
      sqlcmd -S localhost,14330 -U sa -C -b -v DB_NAME=almirante ADMIN_USER=almirante_admin_bd \
             APP_USER=almirante_user_bd -i docs/sql/criar-usuario-admin-app.sql
  (o sqlcmd lê APP_ADMIN_PASSWORD do ambiente; ADMIN_USER/APP_USER/DB_NAME não são segredos).
  Depois, no .env do ambiente (fora do Git): SQL_ADMIN_USER e SQL_ADMIN_PASSWORD com os mesmos valores.

  Rotação da credencial administrativa: gere uma senha nova, atualize SQL_ADMIN_PASSWORD, rode este
  script de novo (ALTER USER com a senha nova; nada mais muda) e reinicie a API. Ver
  docs/authentication-security.md.

  ATENÇÃO — o sqlcmd substitui $(APP_ADMIN_PASSWORD) como TEXTO dentro do literal N'...' abaixo: uma
  senha com aspa simples quebraria o script (e, como ele roda como sysadmin, executaria o que vier
  depois da aspa). scripts/sql-bootstrap.sh valida o alfabeto antes; ao rodar à mão, respeite-o.

  MIGRAÇÃO de ambiente anterior: se as identidades existirem como LOGIN de servidor (versões que usavam
  "sa" ou o login almirante_admin_bd/almirante_user_bd), este script as converte — remove o usuário
  mapeado a login, apaga o login de mesmo nome e recria a identidade como usuário contido. A API antiga
  em execução perde a conexão: rode-o antes de subir a versão nova.
*/
:on error exit
USE master;
GO

-- Entradas: "sa" e identidades repetidas são recusadas aqui também (a API recusa de novo no startup).
DECLARE @admin sysname = N'$(ADMIN_USER)', @app sysname = N'$(APP_USER)', @db sysname = N'$(DB_NAME)';
IF LOWER(@admin) = N'sa' OR LOWER(@app) = N'sa'
    THROW 50001, N'ADMIN_USER/APP_USER não podem ser "sa": a API nunca usa esse login.', 1;
IF LOWER(@admin) = LOWER(@app)
    THROW 50002, N'ADMIN_USER e APP_USER precisam ser identidades diferentes.', 1;
IF @admin LIKE N'%[^A-Za-z0-9_]%' OR @app LIKE N'%[^A-Za-z0-9_]%' OR @db LIKE N'%[^A-Za-z0-9_]%'
   OR LEN(@admin) = 0 OR LEN(@app) = 0 OR LEN(@db) = 0
    THROW 50003, N'ADMIN_USER, APP_USER e DB_NAME aceitam só letras, dígitos e "_".', 1;
IF LEN(N'$(APP_ADMIN_PASSWORD)') < 16
    THROW 50004, N'APP_ADMIN_PASSWORD deve ter ao menos 16 caracteres.', 1;
GO

-- Contained database authentication é uma opção do servidor (desligada por padrão). Só liga se preciso.
IF (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'contained database authentication') = 0
BEGIN
    EXEC sys.sp_configure N'contained database authentication', 1;
    RECONFIGURE;
END
GO

IF DB_ID(N'$(DB_NAME)') IS NULL
    CREATE DATABASE [$(DB_NAME)] CONTAINMENT = PARTIAL;
ELSE IF (SELECT containment FROM sys.databases WHERE name = N'$(DB_NAME)') = 0
BEGIN
    -- Mudar o containment exige acesso exclusivo (WITH ROLLBACK IMMEDIATE não vale para esta opção). Numa migração
    -- de ambiente que ainda tem a API antiga conectada, as sessões dela são encerradas — ela perderia o login
    -- legado logo em seguida de qualquer forma. O TRY/CATCH garante que o banco não fica em SINGLE_USER se falhar.
    ALTER DATABASE [$(DB_NAME)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    BEGIN TRY
        ALTER DATABASE [$(DB_NAME)] SET CONTAINMENT = PARTIAL;
    END TRY
    BEGIN CATCH
        ALTER DATABASE [$(DB_NAME)] SET MULTI_USER;
        THROW;
    END CATCH;
    ALTER DATABASE [$(DB_NAME)] SET MULTI_USER;
END
-- TRUSTWORTHY ligado + dono sysadmin deixaria um módulo do banco escalar para o servidor.
ALTER DATABASE [$(DB_NAME)] SET TRUSTWORTHY OFF;
GO

-- Identidades herdadas como LOGIN de servidor: convertidas (ver "MIGRAÇÃO" acima).
DECLARE @nomes TABLE (nome sysname PRIMARY KEY);
INSERT @nomes VALUES (N'$(ADMIN_USER)'), (N'$(APP_USER)');
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'DROP LOGIN ' + QUOTENAME(l.name) + N'; '
FROM sys.server_principals l JOIN @nomes n ON n.nome = l.name
WHERE l.type IN ('S', 'U', 'G');
EXEC sys.sp_executesql @cmd;
GO

USE [$(DB_NAME)];
GO

-- Usuário mapeado a login (authentication_type = 1) sobrou do modelo anterior: sai antes da recriação.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'DROP USER ' + QUOTENAME(p.name) + N'; '
FROM sys.database_principals p
WHERE p.name IN (N'$(ADMIN_USER)', N'$(APP_USER)') AND p.authentication_type <> 2;   -- 2 = DATABASE (usuário contido)
EXEC sys.sp_executesql @cmd;
GO

IF DATABASE_PRINCIPAL_ID(N'almirante_app_role') IS NULL
    CREATE ROLE [almirante_app_role] AUTHORIZATION [dbo];
GO

-- Administrativo: senha vinda do ambiente; reexecutar redefine a senha (rotação operacional).
IF DATABASE_PRINCIPAL_ID(N'$(ADMIN_USER)') IS NULL
    CREATE USER [$(ADMIN_USER)] WITH PASSWORD = N'$(APP_ADMIN_PASSWORD)', DEFAULT_SCHEMA = [dbo];
ELSE
    ALTER USER [$(ADMIN_USER)] WITH PASSWORD = N'$(APP_ADMIN_PASSWORD)';
GO

-- Runtime: senha inicial aleatória e descartável (CRYPT_GEN_RANDOM), nunca exibida; a API a troca já no
-- primeiro startup. Se a identidade já existe, a senha (rotacionada pela API) NÃO é tocada.
IF DATABASE_PRINCIPAL_ID(N'$(APP_USER)') IS NULL
BEGIN
    DECLARE @senha nvarchar(128) = CONVERT(nvarchar(64), CRYPT_GEN_RANDOM(24), 2) + N'aA1';
    DECLARE @sql nvarchar(max) = N'CREATE USER [$(APP_USER)] WITH PASSWORD = N''' + @senha + N''', DEFAULT_SCHEMA = [dbo];';
    EXEC sys.sp_executesql @sql;
END
GO

-- Normaliza as duas identidades: fora de qualquer papel de banco que não seja o previsto e sem
-- permissão direta herdada (classes 0/1/3/4 — a 4 cobre IMPERSONATE, que sobreviveria a um GRANT antigo).
-- Só sobra CONNECT; o que cada uma deve ter é reaplicado logo abaixo.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N'; '
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name IN (N'$(ADMIN_USER)', N'$(APP_USER)')
  AND NOT (u.name = N'$(APP_USER)' AND r.name = N'almirante_app_role');
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
        WHEN 4 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'ROLE::' ELSE N'USER::' END + QUOTENAME(alvo.name) END
    + N' FROM ' + QUOTENAME(g.name) + N' CASCADE; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
LEFT JOIN sys.database_principals alvo ON p.class = 4 AND alvo.principal_id = p.major_id
WHERE g.name IN (N'$(ADMIN_USER)', N'$(APP_USER)') AND p.permission_name <> N'CONNECT'
  AND p.class IN (0, 1, 3, 4) AND p.minor_id = 0
  AND (p.class <> 4 OR alvo.principal_id IS NOT NULL);
EXEC sys.sp_executesql @cmd;
GO

IF IS_ROLEMEMBER(N'almirante_app_role', N'$(APP_USER)') = 0
    ALTER ROLE [almirante_app_role] ADD MEMBER [$(APP_USER)];
GO

-- Conjunto mínimo da identidade administrativa (justificativa no cabeçalho).
GRANT CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, ALTER ANY USER TO [$(ADMIN_USER)];
GRANT CONTROL ON SCHEMA::dbo TO [$(ADMIN_USER)];
GO

-- A role de runtime começa sem nenhuma permissão de objeto: a API concede tabela a tabela.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
        WHEN 4 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'ROLE::' ELSE N'USER::' END + QUOTENAME(alvo.name) END
    + N' FROM [almirante_app_role] CASCADE; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
LEFT JOIN sys.database_principals alvo ON p.class = 4 AND alvo.principal_id = p.major_id
WHERE g.name = N'almirante_app_role' AND p.permission_name <> N'CONNECT' AND p.class IN (0, 3, 4) AND p.minor_id = 0
  AND (p.class <> 4 OR alvo.principal_id IS NOT NULL);
EXEC sys.sp_executesql @cmd;
GO

-- Conferência. Falha o script (e, no compose, o serviço sql-bootstrap → a API não sobe) se o resultado
-- não for o previsto. Nada aqui imprime segredo.
-- Escopo de SERVIDOR consultado direto no catálogo (IS_SRVROLEMEMBER devolve NULL sob EXECUTE AS USER).
DECLARE @falhas nvarchar(max) = N'';
IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name IN (N'$(ADMIN_USER)', N'$(APP_USER)'))
    SET @falhas += N'existe login de servidor com o nome de uma identidade da aplicação; ';
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(ADMIN_USER)' AND authentication_type = 2)
    SET @falhas += N'a identidade administrativa não é um usuário contido; ';
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(APP_USER)' AND authentication_type = 2)
    SET @falhas += N'a identidade de runtime não é um usuário contido; ';
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = DB_NAME() AND (is_trustworthy_on = 1 OR containment = 0))
    SET @falhas += N'o banco está TRUSTWORTHY ou sem containment; ';
IF EXISTS (SELECT 1 FROM sys.database_role_members m
             JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
             JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            WHERE u.name IN (N'$(ADMIN_USER)', N'$(APP_USER)')
              AND NOT (u.name = N'$(APP_USER)' AND r.name = N'almirante_app_role'))
    SET @falhas += N'uma identidade pertence a papel de banco não previsto; ';
IF LEN(@falhas) > 0
BEGIN
    DECLARE @msg nvarchar(2048) = N'Bootstrap incompleto: ' + @falhas;
    THROW 50010, @msg, 1;
END
GO

-- Escopo de BANCO, sob impersonação (adequado: as funções consultadas são de banco). Todas as colunas
-- devem sair como indicado.
EXECUTE AS USER = N'$(ADMIN_USER)';
SELECT
    ISNULL(IS_MEMBER(N'db_owner'), 0)                                           AS admin_db_owner,       -- 0
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0)            AS admin_control_banco,  -- 0
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)       AS admin_ddl,            -- 1
    ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'CONTROL'), 0)                 AS admin_controle_dbo;   -- 1
REVERT;
GO
EXECUTE AS USER = N'$(APP_USER)';
SELECT
    ISNULL(IS_MEMBER(N'db_owner'), 0)                                           AS app_db_owner,         -- 0
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)       AS app_ddl,              -- 0
    ISNULL(IS_MEMBER(N'almirante_app_role'), 0)                                 AS app_role;             -- 1
REVERT;
GO
