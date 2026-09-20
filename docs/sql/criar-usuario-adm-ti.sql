/*
  Cria o login "adm-ti" para CONSULTAS internas de TI: somente leitura, restrito a views do esquema [ti]
  do banco da aplicação. NÃO é sysadmin e não concede acesso às tabelas, a outros bancos, nem a dados
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
  permissões abaixo e redefine a senha. Também revoga permissões diretas de servidor e IMPERSONATE
  no banco. Aborta em vez de declarar sucesso se encontrar privilégios que não possa normalizar
  com segurança (GRANT OPTION, propriedade, outros bancos ou classes não suportadas).
  Toda alteração faz parte da mesma transação. Após regularizar, considere a senha antiga comprometida.

  NOTA sobre a senha: o sqlcmd substitui $(ADM_TI_PASSWORD) como TEXTO dentro do literal N'...'
  abaixo. Uma senha contendo aspa simples (') quebra o script — e, como ele roda como sysadmin,
  quebraria executando o que vier depois da aspa. Use letras, dígitos e símbolos SEM aspas.
*/
:on error exit
USE master;
GO

SET XACT_ABORT ON;
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
    THROW 51000, 'Execute a regularizacao de adm-ti com uma identidade sysadmin separada.', 1;

BEGIN TRANSACTION;

-- CASCADE poderia revogar acessos de terceiros: nesses casos exigimos revisão manual.
IF EXISTS (SELECT 1 FROM sys.server_permissions
           WHERE grantee_principal_id = SUSER_ID(N'adm-ti')
             AND (class NOT IN (100, 101) OR state = 'W'))
    THROW 51000, 'adm-ti tem permissao de servidor nao normalizavel automaticamente (classe ou GRANT OPTION). Revise e revogue antes de repetir.', 1;
IF EXISTS (SELECT 1 FROM sys.databases WHERE owner_sid = SUSER_SID(N'adm-ti'))
   OR EXISTS (SELECT 1 FROM sys.server_principals WHERE owning_principal_id = SUSER_ID(N'adm-ti'))
    THROW 51000, 'adm-ti e proprietario de banco ou principal de servidor. Transfira a propriedade antes de repetir.', 1;

-- Não alteramos outros bancos. Uma identidade dedicada de consulta não deve conservar mapeamentos
-- legados neles, inclusive em master/msdb. Banco offline impede essa verificação: abortar é seguro.
DECLARE @check nvarchar(max) = N'';
SELECT @check += N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(name)
    + N'.sys.database_principals WHERE sid = SUSER_SID(N''adm-ti'') AND type <> ''R'') '
    + N'THROW 51000, ''adm-ti possui usuario em outro banco; revise esse acesso antes de repetir.'', 1; '
FROM sys.databases WHERE name <> N'$(DB_NAME)' AND database_id <> 2;
EXEC sys.sp_executesql @check;

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
-- O REVOKE de schema/role não alcança CONTROL SERVER nem IMPERSONATE ON LOGIN.
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 100 THEN N''
        WHEN 101 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'SERVER ROLE::' ELSE N'LOGIN::' END + QUOTENAME(alvo.name) END
    + N' FROM [adm-ti]; '
FROM sys.server_permissions p
LEFT JOIN sys.server_principals alvo ON p.class = 101 AND alvo.principal_id = p.major_id
WHERE p.grantee_principal_id = SUSER_ID(N'adm-ti')
  AND NOT (p.class = 100 AND p.permission_name = N'CONNECT SQL' AND p.state = 'G');
EXEC sys.sp_executesql @cmd;
GRANT CONNECT SQL TO [adm-ti];
GO

USE [$(DB_NAME)];
GO

IF SCHEMA_ID(N'ti') IS NULL EXEC(N'CREATE SCHEMA [ti] AUTHORIZATION [dbo]');
GO

IF DATABASE_PRINCIPAL_ID(N'adm-ti') IS NULL
    CREATE USER [adm-ti] FOR LOGIN [adm-ti] WITH DEFAULT_SCHEMA = [ti];
ELSE
    ALTER USER [adm-ti] WITH LOGIN = [adm-ti], DEFAULT_SCHEMA = [ti];
GO

IF DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura') IS NULL
    CREATE ROLE [almirante_ti_leitura];
GO

IF EXISTS (SELECT 1 FROM sys.database_permissions
           WHERE grantee_principal_id IN (USER_ID(N'adm-ti'), DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura'))
             AND (class NOT IN (0, 1, 3, 4) OR state = 'W'))
    THROW 51000, 'adm-ti/role tem permissao de banco nao normalizavel automaticamente (classe ou GRANT OPTION). Revise antes de repetir.', 1;
IF EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id IN (USER_ID(N'adm-ti'), DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura')))
   OR EXISTS (SELECT 1 FROM sys.objects WHERE principal_id IN (USER_ID(N'adm-ti'), DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura')))
   OR EXISTS (SELECT 1 FROM sys.database_principals WHERE owning_principal_id IN (USER_ID(N'adm-ti'), DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura')))
    THROW 51000, 'adm-ti/role possui esquema, objeto ou principal. Transfira a propriedade antes de repetir.', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members
           WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'almirante_ti_leitura') AND member_principal_id <> USER_ID(N'adm-ti'))
    THROW 51000, 'A role almirante_ti_leitura e compartilhada. Separe os outros membros antes de regularizar.', 1;
IF (SELECT principal_id FROM sys.schemas WHERE name = N'ti') <> USER_ID(N'dbo')
    THROW 51000, 'O esquema ti deve pertencer a dbo para manter a cadeia de propriedade das views.', 1;
GO

-- Normaliza: o usuário só pode pertencer à role de leitura (sai de db_owner, db_datareader etc.) e não
-- mantém permissões diretas; a própria role também não pode herdar outra role privilegiada.
DECLARE @cmd nvarchar(max) = N'';
SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N'; '
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name IN (N'adm-ti', N'almirante_ti_leitura')
  AND NOT (u.name = N'adm-ti' AND r.name = N'almirante_ti_leitura');
SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
    + CASE p.class WHEN 0 THEN N''
        WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
            + CASE WHEN p.minor_id > 0 THEN N' (' + QUOTENAME(COL_NAME(p.major_id, p.minor_id)) + N')' ELSE N'' END
        WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
        WHEN 4 THEN N' ON ' + CASE WHEN alvo.type = 'R' THEN N'ROLE::' ELSE N'USER::' END + QUOTENAME(alvo.name) END
    + N' FROM ' + QUOTENAME(g.name) + N'; '
FROM sys.database_permissions p
JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
LEFT JOIN sys.database_principals alvo ON p.class = 4 AND alvo.principal_id = p.major_id
WHERE g.name IN (N'adm-ti', N'almirante_ti_leitura')
  AND NOT (p.class = 0 AND p.permission_name = N'CONNECT' AND p.state = 'G');
IF IS_ROLEMEMBER(N'almirante_ti_leitura', N'adm-ti') = 0
    SET @cmd += N'ALTER ROLE [almirante_ti_leitura] ADD MEMBER [adm-ti]; ';
EXEC sys.sp_executesql @cmd;
GRANT CONNECT TO [adm-ti];
GO

-- Views de consulta. TODAS listam as colunas explicitamente — nunca "SELECT *".
-- Motivo: uma view criada com "SELECT *" fixa as colunas existentes no momento da criação, mas
-- reexecutar este script depois de uma migration que adicione coluna sensível (um hash, um token, um
-- dado pessoal novo) a recriaria já incluindo essa coluna, ampliando o acesso do adm-ti em silêncio.
-- Com a lista explícita, uma coluna nova só entra aqui por decisão de quem edita este arquivo.
-- Como o esquema [ti] pertence ao mesmo dono (dbo) das tabelas, a cadeia de propriedade dispensa
-- qualquer permissão direta nas tabelas (e é por isso que o DENY abaixo não quebra as views).
CREATE OR ALTER VIEW [ti].[Usuarios] AS
    SELECT Id, Nome, Email, CargoId, DataCriacao, FalhasLoginConsecutivas, UltimaFalhaLoginUtc, LoginBloqueadoAteUtc
    FROM dbo.Usuarios;
GO
CREATE OR ALTER VIEW [ti].[Cargos] AS
    SELECT Id, Nome, Descricao, Role, Ativo, CriadoPor, CriadoEm, UltimaAtualizacao
    FROM dbo.Cargos;
GO
CREATE OR ALTER VIEW [ti].[Lancamentos] AS
    SELECT Id, MembroId, Finalidade, Descricao, Categoria, TipoFluxo, Valor, Vencimento, Status,
           Ativo, OperacaoId, DataCriacao, DataAtualizacao, AtualizadoPorUsuarioId
    FROM dbo.Lancamentos;
GO
CREATE OR ALTER VIEW [ti].[lancamentos_deletados] AS
    SELECT Id, LancamentoId, UsuarioResponsavelId, IpResponsavel, ExcluidoEmUtc, Motivo, MembroId,
           Finalidade, Categoria, TipoFluxo, Valor, Vencimento, Status, OperacaoId, DataCriacaoOriginal
    FROM dbo.lancamentos_deletados;
GO
CREATE OR ALTER VIEW [ti].[AuthSessions] AS
    SELECT Id, UsuarioId, CreatedAtUtc, LastRenewedAtUtc, AbsoluteExpiresAtUtc, RevokedAtUtc, RevocationReason
    FROM dbo.AuthSessions;
GO

GRANT SELECT ON SCHEMA::[ti] TO [almirante_ti_leitura];
-- Defesa em profundidade: nenhuma leitura direta das tabelas, mesmo que alguém conceda algo por engano.
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::[dbo] TO [almirante_ti_leitura];
GO

-- Pós-condições são bloqueantes, não apenas uma tabela para o operador interpretar.
-- Inclui GRANT_WITH_GRANT_OPTION (W) e permissões herdadas de public.
IF EXISTS (SELECT 1 FROM sys.server_role_members WHERE member_principal_id = SUSER_ID(N'adm-ti'))
   OR EXISTS (SELECT 1 FROM sys.server_permissions WHERE grantee_principal_id = SUSER_ID(N'adm-ti')
              AND NOT (class = 100 AND permission_name = N'CONNECT SQL' AND state = 'G'))
    THROW 51000, 'adm-ti ainda possui privilegios de servidor; regularizacao cancelada.', 1;

DECLARE @inseguro bit = 0;
EXECUTE AS LOGIN = N'adm-ti';
BEGIN TRY
    -- VIEW ANY DATABASE vem de public por padrão e só revela nomes, não dados de outros bancos.
    IF EXISTS (SELECT 1 FROM sys.fn_my_permissions(NULL, N'SERVER')
               WHERE permission_name NOT IN (N'CONNECT SQL', N'VIEW ANY DATABASE'))
       OR ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), -1) <> 0
        SET @inseguro = 1;
    REVERT;
END TRY
BEGIN CATCH
    REVERT;
    THROW;
END CATCH;

EXECUTE AS USER = N'adm-ti';
BEGIN TRY
    IF ISNULL(IS_MEMBER(N'db_owner'), -1) <> 0
       OR ISNULL(HAS_PERMS_BY_NAME(N'dbo.Usuarios', N'OBJECT', N'SELECT'), -1) <> 0
       OR ISNULL(HAS_PERMS_BY_NAME(N'ti.Usuarios', N'OBJECT', N'SELECT'), -1) <> 1
       OR ISNULL(HAS_PERMS_BY_NAME(N'ti.Usuarios', N'OBJECT', N'UPDATE'), -1) <> 0
       OR ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), -1) <> 0
       OR ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'USER', N'IMPERSONATE'), -1) <> 0
       OR EXISTS (SELECT 1 FROM sys.database_principals p
                  WHERE p.principal_id <> USER_ID() AND p.type IN ('S', 'U', 'G')
                    AND HAS_PERMS_BY_NAME(p.name, N'USER', N'IMPERSONATE') = 1)
        SET @inseguro = 1;
    REVERT;
END TRY
BEGIN CATCH
    REVERT;
    THROW;
END CATCH;

IF @inseguro = 1
    THROW 51000, 'adm-ti ainda possui privilegios excessivos ou nao consegue ler as views; regularizacao cancelada.', 1;

COMMIT TRANSACTION;
SELECT CAST(1 AS bit) AS menor_privilegio_validado;
GO
