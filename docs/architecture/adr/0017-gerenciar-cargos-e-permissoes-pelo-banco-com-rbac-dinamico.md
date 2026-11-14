# ADR-0017: Gerenciar cargos e permissões pelo banco com RBAC dinâmico e regras por alvo

## Status

Accepted. Implementado na issue #182. Substitui a [ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md).

## Contexto

A ADR-0013 separou as permissões por perfil, mas as listas ficaram no código: `Roles.cs` tinha os grupos de cargos, as policies e
`Roles.PodeGerenciar`; `PermissoesSenha.cs` tinha a matriz de senha; `AcessoAlmoxarifado.cs`, a consulta por lista de exclusão. Os cargos já
eram registros em `Cargos`, mas um cargo novo não participava de nenhum grupo. Criar um perfil como "Auxiliar da Secretaria", ou mudar o
acesso de um perfil, exigia alterar o código, compilar e publicar (issue #182).

## Decisão

- **Cargo = perfil de acesso** nesta versão, com um cargo por usuário. `Cargos` ganhou `SistemaProtegido` e `Versao` (token de
  concorrência otimista).
- **Catálogo técnico** em `Permissoes`, com chave, módulo, descrição, escrita, reservada e "exige regra por alvo". O catálogo é mantido
  por migration a partir de [`CatalogoPermissoes`](../../../src/Almirante.Api/Security/CatalogoPermissoes.cs). Cada action declara a
  própria permissão com `[RequerPermissao(...)]`. Uma chave nova cadastrada no banco não cria comportamento.
- **Concessões** (`CargoPermissoes`) e **regras por alvo** (`CargoRegrasAlvo`: cargo solicitante × permissão × cargo alvo). A revogação
  é lógica (`Ativo = 0`), com quem concedeu e quem revogou. Ausência de regra nega: não há curinga nem hierarquia implícita.
- **Autorização a cada requisição.** `PermissaoPolicyProvider` interpreta as policies `perm:<chave>`, e `PermissaoAuthorizationHandler`
  consulta o banco por meio de `PermissoesService`. As permissões são memorizadas só durante a requisição; não há cache entre requisições
  nem permissões no JWT. A designação vigente de responsável pelo almoxarifado concede `almoxarifado.consultar` e
  `almoxarifado.gerenciar`.
- **Uma permissão por action**, sem policy no controller. Policies de controller e de action se acumulam, e conceder só a leitura
  precisa liberar o GET. `PermissoesUnitTests` verifica isso por reflexão.
- **Operações sensíveis revalidam na transação.** Senhas, cadastro, exclusão e foto de terceiros leem a revisão global
  (`AutorizacaoEstado`) com trava compartilhada antes de travar usuários e cargos, e depois conferem de novo a concessão e a regra. Toda
  alteração administrativa começa incrementando essa revisão com trava exclusiva. Assim uma revogação já confirmada nunca é ignorada, e
  a ordem de travas é sempre a mesma (revisão → usuários/cargos).
- **Administração sem escalada.**
  - As permissões `autorizacao.*` são reservadas ao perfil de sistema protegido (ADM), que não é desativado e não as perde.
  - Ninguém concede uma permissão que o próprio cargo não tem.
  - Regra por alvo só para permissão que o executor tem; sobre o perfil protegido, só a regra que o próprio executor tem.
  - O último usuário ativo do perfil protegido não é excluído nem reclassificado.
  - Toda escrita exige motivo e a senha de quem está logado, e grava `Autorizacao_historico`: só inserção (trigger), com `SELECT, INSERT`
    para a identidade de runtime.
- **Migração.** A migration `AddRbacDinamico` converte os 17 cargos conhecidos para a matriz equivalente à anterior, congelada na própria
  migration e comparada com [`MatrizInicialAutorizacao`](../../../src/Almirante.Api/Security/MatrizInicialAutorizacao.cs) em teste. Ids e
  vínculos são preservados. No banco novo, o `DbSeeder` semeia a mesma matriz ao criar os cargos. Reiniciar a API não restaura concessões
  revogadas.

Fora do escopo: várias roles por usuário, deny explícito, permissões individuais por usuário, delegação da administração de autorização e
tela administrativa (issue #186, no frontend).

## Consequências

### Positivas

- Criar um perfil e combinar permissões existentes não exige deploy. A mudança vale na próxima requisição, com o mesmo token.
- Leitura e escrita ficam separadas, o que permite perfis só de consulta.
- Um cargo novo nasce sem acesso, inclusive à consulta do almoxarifado, que antes valia para qualquer cargo diferente de DS.
- O frontend consulta `GET /api/Me/Permissoes` em vez de manter listas de roles.

### Negativas / trade-offs

- Cada requisição autorizada faz até três consultas a mais (cargo do usuário, concessões e designação), sem cache entre requisições.
  Medir antes de introduzir cache.
- Toda alteração administrativa serializa com as operações sensíveis (trava na revisão global). É raro e curto, mas uma alteração de
  autorização espera as operações de senha e cadastro em andamento.
- Cargos customizados criados antes da #182 não recebem nada por aproximação, nem a consulta ao almoxarifado: precisam de concessão pela
  API.
- Voltar só o binário anterior depois de alterar perfis restaura as listas estáticas. O rollback exige congelar a administração e
  reconciliar a matriz.

## Alternativas consideradas

Avaliadas na issue #182:

- Só um CRUD de cargos: as listas estáticas continuariam decidindo o acesso.
- Listas em `appsettings`: exigiriam deploy ou restart e não teriam auditoria transacional.
- Todas as permissões no JWT: concessões obsoletas valeriam até o token expirar.
- Só `CargoPermissoes`, sem regras por alvo: as matrizes de cadastro e de senha continuariam estáticas.
- ASP.NET Core Identity ou um motor externo de policies: custo desnecessário.

## Evidências no repositório

- [`PermissoesService.cs`](../../../src/Almirante.Api/Security/PermissoesService.cs): `PermissoesService`, `PermissaoAuthorizationHandler`,
  `PermissaoPolicyProvider`, `RequerPermissaoAttribute`.
- [`AutorizacaoAdminService.cs`](../../../src/Almirante.Api/Services/AutorizacaoAdminService.cs) e
  [`CargosController.cs`](../../../src/Almirante.Api/Controllers/CargosController.cs).
- Migration `20261114125930_AddRbacDinamico` e [`AlmiranteDbContext.Autorizacao.cs`](../../../src/Almirante.Api/Data/AlmiranteDbContext.Autorizacao.cs).
- Testes: `AuthorizationTests` (matriz endpoint × perfil), `PermissoesUnitTests`, `RbacDinamicoTests` (cenário AUXSEC) e
  `AutorizacaoSqlServerTests` (migration, trigger, revogação, último administrador).

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md): sessão conferida no banco a cada requisição, preservada.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): `Autorizacao_historico` entra na lista de tabelas só de inserção.
- [ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md): substituída.
