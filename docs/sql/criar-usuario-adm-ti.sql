/*
  Cria o login administrativo "adm-ti" (consultas internas de TI), com as mesmas permissões do "sa"
  (membro do papel de servidor sysadmin).

  Como usar:
    1. Troque <SENHA_FORTE_AQUI> por uma senha forte (16+ caracteres) definida por você. Nunca versione
       a senha real nem a deixe neste arquivo depois de executar.
    2. Execute conectado como "sa" (ou outro sysadmin), por exemplo:
         sqlcmd -S localhost,14330 -U sa -C -i docs/sql/criar-usuario-adm-ti.sql
    3. O login precisa de "Mixed Mode" (autenticação SQL) habilitado no servidor — o container do
       Compose já vem assim.

  Observações:
    - sysadmin ignora qualquer restrição de banco: o acesso vale para TODOS os bancos, inclusive "almirante".
      Por isso, use só para consultas de TI e nunca como usuário da aplicação (que é o almirante_user_bd,
      só SELECT/INSERT/UPDATE, com senha rotacionada pela própria API).
    - Script idempotente: se o login já existir, apenas redefine a senha e garante o papel sysadmin.
*/
USE master;
GO

IF SUSER_ID(N'adm-ti') IS NULL
BEGIN
    CREATE LOGIN [adm-ti]
        WITH PASSWORD = N'<SENHA_FORTE_AQUI>',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
END
ELSE
BEGIN
    ALTER LOGIN [adm-ti] WITH PASSWORD = N'<SENHA_FORTE_AQUI>';
    ALTER LOGIN [adm-ti] ENABLE;
END
GO

-- Mesmas permissões do "sa".
ALTER SERVER ROLE [sysadmin] ADD MEMBER [adm-ti];
GO

-- Conferência: deve retornar 1.
SELECT IS_SRVROLEMEMBER(N'sysadmin', N'adm-ti') AS adm_ti_e_sysadmin;
GO
