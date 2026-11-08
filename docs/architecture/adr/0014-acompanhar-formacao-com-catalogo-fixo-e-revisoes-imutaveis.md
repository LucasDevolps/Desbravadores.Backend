# ADR-0014: Acompanhar a formação com catálogo fixo de classes, revisões imutáveis de requisitos e histórico por trigger

## Status

Accepted. Implementado na issue #125.

## Contexto

A issue #125 pede o acompanhamento individual de classes (regulares, avançadas e de liderança) e especialidades,
requisito por requisito, até a validação final e a investidura/entrega. Ela também fixa estas restrições:

- as 15 classes são um catálogo fixo identificado por enum, sem CRUD; especialidades e requisitos são dados;
- a DSA pode revisar o programa, e uma revisão não pode sobrescrever os requisitos que um participante já usa nem
  apagar aprovações anteriores;
- os requisitos precisam representar "cumprir todos", "escolher uma" e "N entre as opções", sem calcular a conclusão
  por quantidade de caixas marcadas;
- só administrador, diretor, diretor associado e secretário escrevem; os demais só consultam, sem acesso automático aos
  dados de todos os membros;
- correções, reaberturas e inativações preservam o histórico.

Forças do código existente:

- A identidade SQL de runtime não tem `DELETE` (exceto em `AuthSessions`) e só lê as tabelas de auditoria
  ([`DbCredentialManager.BuildProvisionSql`](../../../src/Almirante.Api/Infrastructure/DbCredentialManager.cs),
  [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)).
- A auditoria segue o padrão de trigger e `SESSION_CONTEXT`
  ([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md), `_usuarios_hist`).
- A concorrência usa `rowversion` ([ADR-0006](0006-controlar-concorrencia-com-rowversion.md)), e as permissões ficam
  centralizadas em [`Roles.cs`](../../../src/Almirante.Api/Security/Roles.cs)
  ([ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md)).

## Decisão

- **Classes** como enum `ClasseDesbravador`, com valores explícitos (1–6, 11–16, 21–23). Os dados fixos (grupo, ordem,
  correspondência regular/avançada, idades, pré-requisitos e validações externas) ficam em
  [`CatalogoClasses`](../../../src/Almirante.Api/Services/CatalogoClasses.cs), tirados das páginas oficiais da DSA.
  Classes agrupadas são a modalidade `Agrupada` do vínculo de cada classe.
- **Especialidades** em tabela própria, com unicidade do código oficial (inclusive entre inativas) e de
  nome + área + modalidade entre as ativas. Inativar é lógico.
- **Requisitos em revisões** (`requisitos_revisoes` + `requisitos`): a revisão nasce em rascunho com a árvore completa e
  é publicada ou descartada. Os requisitos são imutáveis (trigger `TR_requisitos_Imutaveis`). A publicação substitui a
  vigente sem mexer nos vínculos. A migração de um vínculo para outra revisão é uma operação explícita, justificada e
  que não copia validações.
- **Regras de satisfação** nos nós da árvore: item validado (com evidências de especialidade, quando exigidas), grupo
  `Todos` ou `Alternativas` com `quantidadeMinima`. O progresso conta unidades obrigatórias da raiz
  ([`ProgressoFormacao`](../../../src/Almirante.Api/Services/ProgressoFormacao.cs)).
- **Vínculos separados** (`membros_classes`, `membros_especialidades`) com base comum `VinculoFormacao`. Há um índice
  único filtrado por vínculo não cancelado. A conclusão é separada da investidura/entrega. Reabrir e cancelar são
  recusados quando outra conclusão depende do vínculo.
- **Histórico por trigger**: `formacao_historico` recebe cada inclusão e alteração das tabelas de formação, com
  responsável, IP, ação e justificativa do `SESSION_CONTEXT`. Ela entra em `DbPrivilegeAuditor.AuditTables` (runtime
  só com `SELECT`). Sem contexto, a escrita é recusada.
- **Permissões**: policy `GestaoFormacao` (ADM, DIR, DIRA, SEC) para escrita e consulta de terceiros. Catálogos e a
  própria formação (`/api/Me/Formacao`) ficam liberados para qualquer cargo ativo.
- **Sem carga inicial** de especialidades e cartões: nenhuma fonte estruturada e conferível foi identificada, e uma
  carga parcial seria apresentada como oficial.

## Consequências

### Positivas

- O conjunto de requisitos usado por um participante nunca muda por baixo dele, e revisões novas não apagam aprovações.
- Conclusão, investidura/entrega e aprovação oficial da liderança são estados distintos e explícitos.
- Toda mudança fica reconstituível pelo histórico, que a própria API não consegue adulterar.
- Escrita e consulta de terceiros ficam restritas no backend, com testes endpoint × perfil.

### Negativas / trade-offs

- Corrigir um requisito cadastrado errado exige uma nova revisão (descartar o rascunho ou publicar outra e migrar).
- Até o clube cadastrar e publicar os cartões, as classes não podem ser associadas pelo fluxo normal (só registros
  históricos).
- Os triggers gravam o estado completo em JSON a cada escrita; o volume é baixo para o porte de um clube.
- Áreas de especialidade em texto livre dependem de digitação padronizada.

## Alternativas consideradas

- Tabela de classes com CRUD: descartada pela issue (catálogo fixo identificado por enum).
- Uma tabela única de vínculos com tipo: descartada, porque a issue pede vínculos membro–classe e membro–especialidade
  separados.
- Editar requisitos de um rascunho no lugar: exigiria `DELETE` ou atualizações em massa sem trilha. A revisão nova é
  mais simples e auditável.
- Histórico gravado pela API: a tabela ficaria sujeita a `UPDATE` pela identidade de runtime, ao contrário das demais
  auditorias.

## Evidências no repositório

- Código: [`CatalogoClasses`](../../../src/Almirante.Api/Services/CatalogoClasses.cs),
  [`FormacaoService`](../../../src/Almirante.Api/Services/FormacaoService.cs),
  [`CatalogoFormacaoService`](../../../src/Almirante.Api/Services/CatalogoFormacaoService.cs),
  [`AlmiranteDbContext.Formacao`](../../../src/Almirante.Api/Data/AlmiranteDbContext.Formacao.cs), migration
  `AddFormacaoClassesEspecialidades`.
- Testes: [`FormacaoUnitTests`](../../../src/Almirante.Api.Tests/FormacaoUnitTests.cs),
  [`FormacaoSqlServerTests`](../../../src/Almirante.Api.Tests/FormacaoSqlServerTests.cs),
  [`AuthorizationTests`](../../../src/Almirante.Api.Tests/AuthorizationTests.cs),
  [`SqlIdentityModelTests`](../../../src/Almirante.Api.Tests/SqlIdentityModelTests.cs).
- Documentação: [`docs/formacao.md`](../../formacao.md).

## Decisões relacionadas

- [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md): trigger e `SESSION_CONTEXT`.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): tabela de auditoria só com `SELECT` para o runtime.
- [ADR-0006](0006-controlar-concorrencia-com-rowversion.md): `versao` nas operações de estado.
- [ADR-0013](0013-separar-permissoes-por-perfil-com-hierarquia-de-cargos.md): perfis e autoatendimento.
