# ADR-0015: Gerir o patrimônio e a lista de desejos com um registro por bem físico, auditoria por trigger e permissão por cargo ou designação

## Status

Accepted. Implementado na issue #126.

## Contexto

A issue #126 pede a gestão patrimonial do clube e uma lista de desejos do almoxarifado. Ela fixa estas restrições:

- cada registro de patrimônio é um bem físico identificável; o tombo é único e gerado pelo sistema, e nomes podem
  se repetir;
- os bens sem valor comercial também entram, e neles o valor estimado é dispensado;
- empréstimos simultâneos do mesmo bem são impedidos, sob concorrência e repetição;
- a baixa e a exclusão lógica são justificadas e preservam o histórico; um bem com movimentações é baixado, nunca
  apagado;
- o inventário fechado é imutável, e a votação da Comissão é registrada depois, sem que o sistema a simule;
- um desejo cadastrado não cria patrimônio nem financeiro; os atendimentos são parciais, idempotentes e vinculam os
  bens recebidos de forma explícita;
- a auditoria existente é reutilizada, e as permissões ficam no backend.

A issue deixou três pontos para o responsável pelo produto, decididos em 2026-10-23:

- **Quem gerencia**: diretoria (ADM, DIR, DIRA), secretaria (SEC) e o almoxarife gerenciam; tesouraria e os demais
  cargos só consultam; o desbravador (DS) não consulta. O cargo ALM é criado se não existir.
- **Responsável pelo almoxarifado**: designação separada do cargo.
- **Acervo antigo sem data**: marcado como data desconhecida, com justificativa obrigatória.

A "Unidade vinculada" ficou fora desta versão, porque não existe cadastro de Unidades.

Forças do código existente:

- a identidade de runtime não tem `DELETE` e só lê as tabelas de auditoria
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md));
- a formação já audita por trigger e `SESSION_CONTEXT` ([ADR-0014](0014-acompanhar-formacao-com-catalogo-fixo-e-revisoes-imutaveis.md));
- `rowversion` controla a concorrência ([ADR-0006](0006-controlar-concorrencia-com-rowversion.md));
- `Idempotency-Key` protege as criações repetíveis ([ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md));
- o JWT carrega um único cargo, conferido no banco a cada requisição ([ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md)).

## Decisão

- **Um registro por bem físico.** `patrimonio_bens` gera o tombo pelo banco: `NumeroTombo` é `IDENTITY` e `Tombo`
  (`PAT-000001`) é uma coluna calculada persistida com índice único. O número nunca é reaproveitado; podem existir
  lacunas.
- **Situação derivada das movimentações.** Empréstimo, manutenção e baixa ficam em tabelas próprias e mudam
  `Situacao` na mesma transação, com o bem travado (`UPDLOCK`).
- **Índices únicos filtrados como última barreira.** Garantem no máximo um empréstimo aberto e uma manutenção aberta
  por bem, um vínculo ativo de bem a atendimento, uma designação vigente por membro e uma `Idempotency-Key` por
  usuário. Uma violação vira `409`.
- **Regras puras testáveis.** As regras ficam em `RegrasAlmoxarifado`, sem acesso a banco. O serviço carrega e trava
  os registros e chama essas conferências.
- **Consistência garantida também por CHECKs.** O banco valida valor estimado × valor comercial, data desconhecida ×
  justificativa, situação do desejo × quantidades e campos de baixa, exclusão, encerramento e votação.
- **Auditoria reutilizada.** Triggers `TR_<tabela>_Historico` gravam em `almoxarifado_historico` o estado anterior e
  o novo, com usuário, IP, operação (`TipoMovimentacaoAlmoxarifado`) e justificativa do `SESSION_CONTEXT`. A tabela
  entra em `DbPrivilegeAuditor.AuditTables`: o runtime só tem `SELECT`.
- **Inventário como fotografia.** `patrimonio_inventario_itens` é imutável por trigger. O cabeçalho só aceita o
  registro da votação, uma vez (`TR_patrimonio_inventarios_Fechado`). O fechamento trava a tabela de bens até o commit.
- **Atendimento idempotente por usuário + `Idempotency-Key`.** A chave, o hash e o atendimento ficam na mesma linha.
  Com a necessidade travada, um reenvio simultâneo é reconhecido e devolve o atendimento original. Os bens recebidos
  são criados ou vinculados explicitamente, um por unidade. O estorno é a correção auditada.
- **Permissões.** São três policies: `GestaoAlmoxarifado`, `ConsultaAlmoxarifado` e `DesignacaoAlmoxarifado`.
  - As duas primeiras usam o requisito `AcessoAlmoxarifadoRequirement`: aceitam pelo cargo do token ou pela
    designação vigente em `almoxarifado_responsaveis`, lida do banco a cada requisição.
  - A designação é feita por quem já atribui cargos (ADM, DIR, DIRA, SEC). Um responsável designado não designa outros.
- Fora do escopo: estoque de consumíveis, depreciação, financeiro, escopo de acesso por Unidade e votação eletrônica.

## Consequências

### Positivas

- Duas retiradas do mesmo bem, dois vínculos do mesmo bem e atendimentos repetidos não são possíveis, nem sob
  concorrência.
- O histórico vem do banco e não depende de o código lembrar de gravá-lo. Escrita sem contexto de auditoria é
  recusada.
- O responsável designado ganha e perde acesso sem novo login e sem trocar de cargo.

### Negativas / trade-offs

- Cada requisição de quem não tem cargo de gestão (ou de consulta) faz uma consulta extra à tabela de designações.
- O fechamento do inventário bloqueia escritas em bens durante o snapshot.
- O tombo pode ter lacunas.
- A identidade do JWT continua com um único cargo: a designação é um segundo caminho de autorização, documentado nas
  policies.

## Alternativas consideradas

- **Novo cargo "Almoxarife do Clube"**: descartado pelo responsável pelo produto, porque o membro perderia o cargo atual (um
  cargo por usuário).
- **Exigir data de aquisição sempre** ou **aceitar só o ano**: descartados pelo responsável pelo produto, em favor da data
  desconhecida com justificativa.
- **Cadastro de Unidades**: fora desta versão (decisão do responsável pelo produto).

## Evidências no repositório

- [`Entities/Almoxarifado.cs`](../../../src/Almirante.Api/Entities/Almoxarifado.cs),
  [`Data/AlmiranteDbContext.Almoxarifado.cs`](../../../src/Almirante.Api/Data/AlmiranteDbContext.Almoxarifado.cs).
- Migration [`AddAlmoxarifadoPatrimonio`](../../../src/Almirante.Api/Data/Migrations/20261023182429_AddAlmoxarifadoPatrimonio.cs):
  tabelas, `TriggerHistorico`, `TriggerItensInventarioImutaveis`, `TriggerInventarioFechado`, `CargoAlmoxarife`.
- [`Services/RegrasAlmoxarifado.cs`](../../../src/Almirante.Api/Services/RegrasAlmoxarifado.cs),
  [`PatrimonioService`](../../../src/Almirante.Api/Services/PatrimonioService.cs),
  [`InventarioService`](../../../src/Almirante.Api/Services/InventarioService.cs),
  [`DesejosAlmoxarifadoService`](../../../src/Almirante.Api/Services/DesejosAlmoxarifadoService.cs),
  [`ResponsaveisAlmoxarifadoService`](../../../src/Almirante.Api/Services/ResponsaveisAlmoxarifadoService.cs).
- [`Security/AcessoAlmoxarifado.cs`](../../../src/Almirante.Api/Security/AcessoAlmoxarifado.cs) e
  [`Security/Roles.cs`](../../../src/Almirante.Api/Security/Roles.cs).
- Testes: [`AlmoxarifadoUnitTests.cs`](../../../src/Almirante.Api.Tests/AlmoxarifadoUnitTests.cs),
  [`AlmoxarifadoSqlServerTests.cs`](../../../src/Almirante.Api.Tests/AlmoxarifadoSqlServerTests.cs),
  [`AlmoxarifadoMigrationTests.cs`](../../../src/Almirante.Api.Tests/AlmoxarifadoMigrationTests.cs),
  [`AuthorizationTests.cs`](../../../src/Almirante.Api.Tests/AuthorizationTests.cs).
- Contrato: [`docs/almoxarifado.md`](../../almoxarifado.md).

## Decisões relacionadas

- [ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md), [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md),
  [ADR-0006](0006-controlar-concorrencia-com-rowversion.md), [ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md),
  [ADR-0014](0014-acompanhar-formacao-com-catalogo-fixo-e-revisoes-imutaveis.md).
