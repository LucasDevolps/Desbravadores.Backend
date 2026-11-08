# Classes, liderança e especialidades (issue #125)

Acompanhamento individual da formação dos membros: classes regulares, avançadas e de liderança e especialidades,
do início à validação final e à investidura (classe) ou entrega do distintivo (especialidade), requisito por
requisito. Este documento é o contrato para o frontend e a referência das regras. A decisão de arquitetura está no
[ADR-0014](architecture/adr/0014-acompanhar-formacao-com-catalogo-fixo-e-revisoes-imutaveis.md).

Os campos, situações e fluxos abaixo são regras **deste sistema**. Eles não são normas oficiais do Ministério. O
acompanhamento local não substitui cartões, aprovações externas, SGC nem procedimentos oficiais. Não há integração com
o SGC.

## Referências oficiais adotadas

| Referência | Uso no sistema |
|---|---|
| [Classes de Desbravadores (DSA, 06/11/2017)](https://www.adventistas.org/pt/desbravadores/classes/) | nomes, grupos, idades de referência, correspondência regular/avançada, classes agrupadas |
| [Requisitos das classes de liderança (DSA, 06/11/2017)](https://www.adventistas.org/pt/desbravadores/classes/classes-de-lideranca-requisitos/) | idades mínimas, classes anteriores, experiência, prazo e validações externas da liderança |
| [Manual Administrativo dos Desbravadores, edição 2020](https://deptos.adventistas.org/desbravadores/ManualAdministrativoDesbravadores.pdf), seções 4.1 e 4.2 | contexto geral do programa de classes e especialidades |

`GET /api/Classes` devolve essas referências em `referencias`. Quando materiais de épocas diferentes divergem, o
sistema não escolhe uma versão por conta própria. A revisão de requisitos registra a edição e a fonte usadas e, em
`observacoes`, as orientações posteriores e as divergências conhecidas.

## Permissões

| Operação | ADM | DIR / DIRA | SEC | TES, DS, instrutor e demais cargos |
|---|:-:|:-:|:-:|:-:|
| Catálogos: `GET /api/Classes…`, `GET /api/Especialidades…`, `GET /api/RevisoesRequisitos/{id}` (revisões publicadas e substituídas) | ✓ | ✓ | ✓ | ✓ |
| Própria formação: `GET /api/Me/Formacao…` | ✓ | ✓ | ✓ | ✓ (só a própria) |
| Cadastrar/editar/inativar especialidades; criar, publicar e descartar revisões; ver rascunhos | ✓ | ✓ | ✓ | — (`403`; rascunho: `404`) |
| Associar, atualizar progresso, evidências, validações externas | ✓ | ✓ | ✓ | — (`403`) |
| Concluir, investidura/entrega, cancelar, retomar, reabrir, migrar revisão | ✓ | ✓ | ✓ | — (`403`) |
| Consultar a formação de outros membros, histórico e relatórios (`/api/Formacao…`) | ✓ | ✓ | ✓ | — (`403`) |

- Policy `GestaoFormacao` (`Roles.Formacao` = ADM, DIR, DIRA, SEC). O Tesoureiro **não** escreve: ele faz parte da
  "gestão" de eventos e do painel, mas não deste módulo.
- Ser instrutor de um vínculo (`instrutorId`) não concede escrita nem leitura dos dados de outros membros.
- O participante consulta só o que é seu. O membro vem do token. Um id de vínculo de outro membro em
  `/api/Me/Formacao/...` recebe `404`, a mesma resposta de um id inexistente.
- Os demais cargos não recebem consulta aos dados de outros membros. Nenhum escopo autorizado antes desta issue dava
  esse acesso (o `GET /api/Membros` do Tesoureiro devolve só id, nome e cargo).

## Catálogo fixo de classes

Enum `ClasseDesbravador`, com valores explícitos e estáveis. Rotas e corpos aceitam o código (nome do enum) ou o id
numérico. Não há CRUD de classes.

| id | código | nome | grupo | idade de referência |
|---:|---|---|---|---:|
| 1 | `Amigo` | Amigo | Regular | 10 |
| 2 | `Companheiro` | Companheiro | Regular | 11 |
| 3 | `Pesquisador` | Pesquisador | Regular | 12 |
| 4 | `Pioneiro` | Pioneiro | Regular | 13 |
| 5 | `Excursionista` | Excursionista | Regular | 14 |
| 6 | `Guia` | Guia | Regular | 15 |
| 11 | `AmigoDaNatureza` | Amigo da Natureza | Avancada | 10 |
| 12 | `CompanheiroDeExcursionismo` | Companheiro de Excursionismo | Avancada | 11 |
| 13 | `PesquisadorDeCampoEBosque` | Pesquisador de Campo e Bosque | Avancada | 12 |
| 14 | `PioneiroDeNovasFronteiras` | Pioneiro de Novas Fronteiras | Avancada | 13 |
| 15 | `ExcursionistaNaMata` | Excursionista na Mata | Avancada | 14 |
| 16 | `GuiaDeExploracao` | Guia de Exploração | Avancada | 15 |
| 21 | `Lider` | Líder | Lideranca | — (mínimo 16 para iniciar, 18 para a investidura) |
| 22 | `LiderMaster` | Líder Máster | Lideranca | — (mínimo 18 para iniciar) |
| 23 | `LiderMasterAvancado` | Líder Máster Avançado | Lideranca | — |

- `idadeReferencia` indica a faixa etária da classe. Não é limite máximo. Nenhuma classe é atribuída nem concluída
  automaticamente pela idade.
- Cada avançada aponta para a regular correspondente (`classeRegularCorrespondente`) e vice-versa. A referência
  adotada não estabelece pré-requisito entre elas, e o sistema não inventa um.
- **Classes agrupadas** são a modalidade `Agrupada` do vínculo, não uma classe. Cada classe do cartão agrupado tem o
  próprio vínculo, com requisitos e conquistas próprios. Ela só vale para regulares e avançadas.
- Pré-requisitos de conclusão (`preRequisitosConclusao`): Líder exige as 6 regulares concluídas; Líder Máster exige
  Líder; Líder Máster Avançado exige Líder Máster.

## Especialidades

Catálogo administrável (`/api/Especialidades`), sem exclusão física.

| Campo | Regra |
|---|---|
| `nome` | obrigatório, até 150 |
| `codigoOficial` | opcional, até 20; único no catálogo, inclusive entre inativas (`409`) |
| `area` | obrigatória, até 100 (texto da área/categoria oficial) |
| `descricao` | opcional, até 2000 |
| `nivelDificuldade` | opcional, 1 a 3, quando a fonte informa |
| `modalidade` | opcional, `Basica` ou `Avancada`; independente do nível |
| `preRequisitos` | ids de especialidades ativas exigidas na conclusão; sem ciclos (`400`) |
| `preRequisitosTexto` | pré-requisitos que não são especialidades, até 1000 |
| `referenciaDistintivo` | opcional, URL `https` da imagem oficial do distintivo |
| `fonteOficial`, `edicaoRevisao` | fonte obrigatória (até 300), edição opcional (até 50) |

- O nome é único entre as **ativas** da mesma área e modalidade, sem diferenciar maiúsculas (`409`). Nomes apenas
  semelhantes, ou iguais em área ou modalidade diferente, são aceitos.
- `PUT` substitui todos os campos (um opcional omitido fica vazio) e exige `versao`.
- `DELETE /api/Especialidades/{id}` com `{ motivo, versao }` **inativa**: impede novas associações e revisões. A
  consulta, os vínculos, o progresso, as conquistas e o histórico continuam. `POST …/{id}/Reativar` com `{ versao }`
  desfaz.
- O cadastro serve para reproduzir o catálogo oficial com a fonte documentada. Ele não cria especialidade oficial.

## Revisões de requisitos

Os requisitos ficam em revisões persistidas (`/api/RevisoesRequisitos`), ligadas a uma classe **ou** a uma
especialidade.

```text
Rascunho ──Publicar──▶ Publicada ──(outra publicada)──▶ Substituida
   └──Descartar──▶ Descartada
```

- `POST /api/RevisoesRequisitos` cria o rascunho com **todos** os requisitos. Requisitos nunca são editados: o trigger
  `TR_requisitos_Imutaveis` recusa qualquer `UPDATE`. Para corrigir, descarte e crie outro rascunho.
- Publicar torna a revisão a **vigente** e marca a anterior como `Substituida`. As novas associações usam a vigente.
  Quem já usa uma revisão continua nela.
- Para levar um vínculo em andamento à revisão vigente, use `POST /api/Formacao/{Classes|Especialidades}/{id}/MigrarRevisao`
  com `{ revisaoId, justificativa, versao }`. A migração é explícita e auditada. Ela preserva o progresso da revisão
  antiga e não copia nenhuma validação: a nova revisão começa pendente. Evidências vinculadas são desativadas.
- Rascunhos e revisões descartadas aparecem só para `GestaoFormacao`.

Formato dos requisitos (árvore na ordem do cartão; até 4 níveis e 500 itens; código único na revisão):

```json
{
  "classe": "Amigo",
  "edicao": "2020",
  "fonteOficial": "Cartão de requisitos da classe Amigo (DSA)",
  "observacoes": "Orientação de 2023 sobre o item 5 ainda não incorporada.",
  "requisitos": [
    { "codigo": "1", "secao": "Gerais", "descricao": "Ter 10 anos ou estar cursando o 5º ano." },
    { "codigo": "2", "descricao": "Escolher uma das opções:", "regra": "Alternativas", "quantidadeMinima": 1,
      "subitens": [ { "codigo": "2a", "descricao": "Opção A" }, { "codigo": "2b", "descricao": "Opção B" } ] },
    { "codigo": "3", "descricao": "Cumprir todos:", "regra": "Todos",
      "subitens": [ { "codigo": "3a", "descricao": "Parte A" }, { "codigo": "3b", "descricao": "Parte B" } ] },
    { "codigo": "4", "descricao": "Completar uma especialidade de Estudos da Natureza não realizada anteriormente.",
      "exigenciaEspecialidade": { "quantidade": 1, "area": "Estudos da Natureza", "naoRealizadaAnteriormente": true } },
    { "codigo": "5", "descricao": "Item opcional do cartão.", "obrigatorio": false }
  ]
}
```

| Campo | Regra |
|---|---|
| `regra` | obrigatória com `subitens` e proibida sem eles. `Todos`: todos os subitens obrigatórios. `Alternativas`: ao menos `quantidadeMinima` subitens (escolher uma = 1; N entre as opções = N) |
| `quantidadeMinima` | só com `Alternativas`, de 1 até o número de subitens |
| `obrigatorio` | padrão `true` na raiz e em grupos `Todos`; opções de `Alternativas` não são obrigatórias (omita ou `false`). Um grupo `Todos` e a raiz precisam de ao menos um obrigatório |
| `exigenciaEspecialidade` | só em itens sem subitens: `quantidade` (1–50), `especialidadeId` e/ou `area` opcionais, `naoRealizadaAnteriormente` |
| `secao`, `referenciaOficial` | opcionais (até 100 e 300) |

## Vínculos membro–classe e membro–especialidade

Tabelas separadas (`membros_classes`, `membros_especialidades`) sobre o cadastro de usuários existente. Isso inclui
diretoria e adultos. Não há cadastro paralelo de pessoas.

| Campo | Significado |
|---|---|
| `membro`, `classe` / `especialidade` | quem e o quê |
| `revisao` | revisão de requisitos usada (`null` em registro histórico) |
| `modalidade` | classes: `Padrao` ou `Agrupada` |
| `dataInicio` | início (obrigatório no fluxo normal; não futuro nem anterior ao nascimento) |
| `situacao` | `EmAndamento`, `Concluida` ou `Cancelada` |
| `instrutor` | instrutor/responsável (usuário ativo), opcional |
| `conclusao` | validação final: `data`, `registradoPor`, `registradoEmUtc` |
| `investidura` (classe) / `entrega` (especialidade) | registro separado da conclusão |
| `cancelamento` | `motivo`, `canceladoPor`, `canceladoEmUtc` |
| `historico`, `origemHistorica` | conquista anterior à implantação |
| `observacoes`, `criadoPor`, `criadoEmUtc`, `atualizadoEmUtc`, `versao` | observações, auditoria e concorrência |

- Um membro tem no máximo **um** vínculo não cancelado por classe ou especialidade (índices únicos filtrados e
  `409`). Cancelar e retomar reaproveitam o mesmo vínculo, com o histórico, e não criam conquista duplicada.
- Associar exige membro ativo e revisão publicada (`409` sem revisão). Uma especialidade inativa recebe `409`.

### Progresso por requisito

`PUT /api/Formacao/{Classes|Especialidades}/{id}/Requisitos/{requisitoId}`:

```json
{ "situacao": "Validado", "dataCumprimento": "2026-10-10", "observacao": "Apresentou na reunião",
  "referenciaEvidencia": "Pasta da unidade, folha 3", "justificativa": null }
```

- `situacao`: `Pendente`, `EmAndamento` ou `Validado`. Sem registro, o requisito está pendente.
- `Validado` grava o usuário (`validadoPor`) e a data/hora (`validadoEmUtc`) da validação.
- Só em itens: grupos são calculados pelos subitens (`409`). Só com o vínculo em andamento: em vínculo concluído, reabra
  antes (`409`).
- `dataCumprimento` é opcional, entre o início do vínculo e hoje (`400`).
- Retirar uma validação exige `justificativa` (`400`).
- Um item com `exigenciaEspecialidade` só é validado com evidências válidas suficientes (`409`).

### Evidências de especialidade (classes)

`POST …/Classes/{id}/Requisitos/{requisitoId}/Evidencias` com `{ membroEspecialidadeId }` vincula uma especialidade do
**mesmo membro** como evidência. Uma conquista de outro membro recebe `404`. A evidência é válida quando:

- a especialidade está concluída;
- ela atende a `especialidadeId` e/ou à `area` exigidas;
- com `naoRealizadaAnteriormente`, a conclusão é conhecida e não é anterior ao início da classe, e a conquista não é
  evidência de outra classe.

Uma mesma conquista conta **uma única vez por classe** (`409` ao tentar usá-la em outro requisito da mesma classe).
`DELETE …/Evidencias/{membroEspecialidadeId}` com `{ justificativa }` remove logicamente. A remoção é recusada (`409`)
quando deixaria um requisito validado sem evidências suficientes.

### Cálculo do progresso

Critério (também em `progressoDetalhado.criterio`):

- **Item**: satisfeito quando está `Validado` e, se exigir especialidades, tem evidências válidas na quantidade exigida.
- **Grupo `Todos`**: satisfeito quando todos os subitens obrigatórios estão satisfeitos.
- **Grupo `Alternativas`**: satisfeito quando ao menos `quantidadeMinima` subitens estão satisfeitos.
- **Unidade obrigatória**: requisito obrigatório do nível raiz. `percentual = unidadesSatisfeitas × 100 / unidadesObrigatorias`, truncado.

Marcar caixas não soma percentual: um grupo "2 entre 3" com uma opção validada continua não satisfeito. O percentual não
substitui a conclusão registrada (`situacao` e `conclusao`). `requisitosCumpridos` e `requisitosPendentes` trazem os
códigos das unidades da raiz. `podeConcluir` e `bloqueios` dizem se a conclusão pode ser registrada agora.

## Conclusão, investidura/entrega e correções

| Operação | Rota (`/api/Formacao/{Classes|Especialidades}/{id}/…`) | Regras |
|---|---|---|
| Concluir | `POST Concluir` `{ dataConclusao, versao }` | só em andamento; `409` com `requisitosPendentes` e/ou `preRequisitos` não atendidos; data entre o início e hoje e não anterior ao cumprimento de um requisito validado (`400`) |
| Investidura | `POST Classes/{id}/Investidura` `{ data, versao }` | só concluída e ainda sem investidura; data entre a conclusão e hoje; liderança: validações externas e idade (abaixo) |
| Entrega | `POST Especialidades/{id}/Entrega` `{ data, versao }` | só concluída e ainda sem entrega; data entre a conclusão e hoje |
| Desfazer investidura/entrega | `DELETE …/Investidura` ou `…/Entrega` `{ justificativa, versao }` | correção auditada; depois, registre de novo |
| Cancelar | `POST Cancelar` `{ justificativa, versao }` | fluxo normal: só em andamento (concluído: reabra antes); registro histórico: também concluído |
| Retomar | `POST Retomar` `{ justificativa, versao }` | só cancelado do fluxo normal; `409` se já houver outro vínculo não cancelado |
| Reabrir | `POST Reabrir` `{ justificativa, versao }` | só concluído do fluxo normal; volta a em andamento |

- Concluir os requisitos **não** registra investidura nem entrega: são atos separados.
- Reabrir retira do vínculo a conclusão e a investidura/entrega. Os dados anteriores ficam preservados em
  `GET …/{id}/Historico` (`estadoAnterior`) para análise e correção.
- Uma conclusão não volta silenciosamente a "em andamento" quando outra conclusão depende dela. Reabrir (ou cancelar um
  registro histórico concluído) recebe `409` com a lista em `dependentes` nestes casos:
  - uma classe que é pré-requisito de outra classe concluída;
  - uma especialidade usada como evidência de uma classe concluída;
  - uma especialidade que é pré-requisito de outra especialidade concluída.
- Alterações em vínculo concluído (requisitos, evidências) recebem `409` até a reabertura.
- Toda operação de estado exige `versao` (rowversion de `GET`). Uma versão desatualizada recebe `409`.

### Pré-requisitos aplicados

| Classe | Ao associar (fluxo normal) | Ao concluir | Na investidura |
|---|---|---|---|
| Regulares e avançadas | — | — | — |
| Líder | 16 anos completos na data de início; 6 regulares concluídas **ou** algum vínculo não cancelado na modalidade agrupada | 6 regulares concluídas | 18 anos completos na data; validações externas confirmadas |
| Líder Máster | 18 anos completos; Líder concluída e investida há pelo menos 1 ano na data de início | Líder concluída; conclusão em até 3 anos do início | validações externas confirmadas |
| Líder Máster Avançado | Líder Máster concluída | Líder Máster concluída | validações externas confirmadas |
| Especialidades | — | especialidades de `preRequisitos` concluídas | — |

Regras de idade usam `dataNascimento` do cadastro. Sem a data, a regra fica pendente com o detalhe "data de nascimento
não cadastrada". Registros históricos não passam pelas regras de início (são fatos passados), mas contam como classes
concluídas nos pré-requisitos.

### Liderança: validações externas e aprovação oficial

`PUT /api/Formacao/Classes/{id}/ValidacoesExternas/{codigo}` com `{ confirmada, data, referencia, observacao, justificativa }`
registra o que depende de órgão ou documento externo:

| Classe | Códigos |
|---|---|
| Líder | `MEMBRO_BATIZADO`, `RECOMENDACAO_COMISSAO_IGREJA`, `CADASTRO_SGC`, `APROVACAO_OFICIAL` |
| Líder Máster | `MEMBRO_ATIVO_IGREJA`, `RECOMENDACAO_COMISSAO_IGREJA`, `CADASTRO_SGC`, `APROVACAO_OFICIAL` |
| Líder Máster Avançado | `RECOMENDACAO_COMISSAO_IGREJA` (formulário MDA/DSA-LMA), `APROVACAO_OFICIAL` |

- Confirmar exige `data` (não futura). Desfazer uma confirmação exige `justificativa` e, com investidura registrada, que
  a investidura seja desfeita antes (`409`).
- `aprovacaoOficialConfirmada` só é `true` com `APROVACAO_OFICIAL` confirmada. Um acompanhamento local completo, ou uma
  conclusão registrada, **não** equivale a aprovação oficial.

## Registros históricos

`POST /api/Formacao/{Classes|Especialidades}/Historicos` registra uma conquista anterior à implantação:

```json
{ "membroId": "…", "classe": "Amigo", "modalidade": "Padrao", "origemHistorica": "Cartão de 2019 arquivado na secretaria",
  "dataInicio": null, "dataConclusao": "2019-11-30", "investiduraOuEntregaRealizada": true, "dataInvestiduraOuEntrega": null }
```

- O registro entra concluído, com `historico: true`, sem revisão, sem requisitos e sem progresso. Nenhuma validação
  individual é fabricada.
- O usuário autenticado fica como responsável pelo lançamento (`criadoPor`, `conclusao.registradoPor`).
- Datas desconhecidas ficam `null`. As informadas não podem ser futuras e precisam estar em ordem cronológica.
- Um registro histórico não é reaberto. Se foi lançado por engano, cancele-o com justificativa (respeitando os
  dependentes) e registre de novo.

## Consultas e relatórios

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Formacao/Classes` | página de `VinculoResumoDto` com o resumo do progresso |
| `GET` | `/api/Formacao/Especialidades` | idem para especialidades |
| `GET` | `/api/Formacao/{Classes|Especialidades}/{id}` | detalhe (requisitos, progresso, pré-requisitos, validações externas) |
| `GET` | `/api/Formacao/{Classes|Especialidades}/{id}/Historico` | inclusões e alterações do vínculo, progresso, evidências e validações |
| `GET` | `/api/Formacao/Membros/{membroId}` | histórico individual: todas as classes e especialidades do membro |
| `GET` | `/api/Formacao/Relatorios/Participantes?tipo=Classe|Especialidade` | contagem por classe/especialidade e situação |
| `GET` | `/api/Me/Formacao`, `/api/Me/Formacao/Classes/{id}`, `/api/Me/Formacao/Especialidades/{id}` | a própria formação |

Filtros das listas:

| Filtro | Lista | Valores |
|---|---|---|
| `membroId` | ambas | id do usuário |
| `classe`, `grupo` | classes | código/id; `Regular`, `Avancada`, `Lideranca` |
| `especialidadeId`, `area` | especialidades | id; área exata, sem diferenciar maiúsculas |
| `situacao` | ambas | `EmAndamento`, `Concluida`, `Cancelada` |
| `historico` | ambas | `true` / `false` |
| `aguardandoInvestiduraOuEntrega` | ambas | `true`: concluídos sem investidura/entrega |
| `periodo` + `de`/`ate` | ambas | `Inicio`, `Conclusao` ou `InvestiduraOuEntrega` define **qual data** `de` e `ate` (inclusivas) filtram; um exige o outro |
| `ordenarPor` | ambas | `Membro` (padrão), `Inicio`, `Conclusao`, `InvestiduraOuEntrega`; desempate estável por nome do membro, classe/especialidade e id |
| `page`, `pageSize` | ambas | `page ≥ 1`; `pageSize` de 1 a 100 (padrão 20) |

Relatórios pedidos na issue:

| Relatório | Onde |
|---|---|
| Histórico individual de classes e especialidades | `GET /api/Formacao/Membros/{membroId}` |
| Requisitos cumpridos e pendentes por participante | `progresso.requisitosCumpridos` e `progresso.requisitosPendentes` de cada item das listas (filtre por classe/especialidade); o detalhe tem a árvore completa |
| Participantes por classe/especialidade e situação | `GET /api/Formacao/Relatorios/Participantes` (contagens) e as listas com `situacao` (nomes) |
| Concluídos aguardando investidura ou entrega | listas com `aguardandoInvestiduraOuEntrega=true` (`ordenarPor=Conclusao`) e a coluna `concluidasAguardandoInvestiduraOuEntrega` do relatório |

Não há motor de PDF/Excel: as consultas devolvem os dados para o frontend montar os relatórios.

## Auditoria

- Toda escrita roda numa transação que trava o registro (`UPDLOCK`). O `SESSION_CONTEXT` leva o responsável, o IP de
  origem, a ação (ex.: `CONCLUIR`, `REABRIR`, `VALIDACAO_EXTERNA`) e a justificativa.
- Os triggers `TR_<tabela>_Historico` gravam cada inclusão e cada alteração em `formacao_historico`: estado anterior
  (`null` na inclusão) e novo, em JSON com todas as colunas. As tabelas cobertas são `especialidades`,
  `especialidades_prerequisitos`, `requisitos_revisoes`, `membros_classes`, `membros_especialidades`,
  `progresso_requisitos`, `evidencias_especialidades` e `validacoes_externas`.
- Uma escrita sem esse contexto é recusada pelo próprio banco (erro 50125).
- `formacao_historico` é tabela de auditoria: a identidade de runtime só tem `SELECT` (com `DENY` de escrita), como
  `_usuarios_hist`. Só os triggers gravam nela.
- Nada é excluído fisicamente (a identidade de runtime não tem `DELETE`). Remoções são lógicas: inativação, evidência
  `Ativo = 0`, pré-requisito desativado, revisão descartada e vínculo cancelado.

## Limitações conhecidas

- **Sem carga inicial de requisitos nem de especialidades.** As páginas oficiais consultadas não trazem os cartões em
  formato estruturado e conferível. Por isso o sistema não carrega uma versão parcial como se fosse o catálogo oficial.
  O clube cadastra as especialidades e as revisões dos cartões a partir da fonte, registrando edição e fonte. Até haver
  revisão publicada, a associação daquela classe/especialidade recebe `409`. O catálogo das 15 classes é fixo e já vem
  pronto.
- As validações externas da liderança são registros de conferência local. Elas não consultam o SGC nem a
  Associação/União.
- Áreas de especialidade são texto livre: digite-as de forma padronizada para os filtros por área funcionarem.
- `referenciaDistintivo` é uma URL. Não há upload de imagem do distintivo nesta entrega.
- Instrutores não têm visão dos membros que acompanham. Isso exigiria um escopo de consulta novo, fora do que a issue
  autoriza.
- A experiência mínima do Líder Máster é conferida pela data da investidura de Líder registrada no sistema. Um registro
  histórico sem essa data não sustenta o início de Líder Máster até ser corrigido (cancelado e registrado de novo com a
  data).
- A comparação de datas com "hoje" usa a data civil do clube (`Clube:FusoHorario`).
