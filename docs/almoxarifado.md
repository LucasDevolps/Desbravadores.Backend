# Patrimônio e lista de desejos do almoxarifado (issue #126)

Gestão patrimonial do clube e lista de desejos do almoxarifado. Responde quatro perguntas:

- o que o clube possui, onde está e em que estado se encontra;
- o que está emprestado, com quem e quando deve voltar;
- o que precisa ser adquirido ou reposto, e com que urgência;
- quais alterações ocorreram e quem as fez.

Este documento é o contrato para o frontend e a referência das regras. A decisão de arquitetura está no
[ADR-0015](architecture/adr/0015-gerir-patrimonio-e-lista-de-desejos-com-auditoria-por-trigger.md).

## Fundamentação e limites

[Manual Administrativo do Clube de Desbravadores — DSA, edição 2020](https://deptos.adventistas.org/desbravadores/ManualAdministrativoDesbravadores.pdf),
seção **3.6.5 — Patrimônio** (página impressa 94): registrar nome, descrição, estado de conservação, valor estimado
e data de aquisição; incluir bens sem valor comercial, sem valor estimado; submeter o registro à Comissão da
Igreja anualmente e quando houver aquisição de bem de maior valor.

O manual não prescreve as regras técnicas abaixo, a lista de desejos nem os níveis de urgência. Elas são
requisitos deste sistema. O sistema registra e apoia os procedimentos da Comissão, mas **não substitui a votação**.

Nada neste módulo gera despesa, pagamento, reserva de dinheiro ou lançamento financeiro, nem altera o saldo do
dashboard. Também ficam fora do escopo:

- frontend;
- compras e cotações;
- aprovação financeira e pagamento;
- depreciação contábil;
- estoque de consumíveis;
- QR Code e etiquetas;
- notificações automáticas;
- votação eletrônica.

## Permissões

| Operação | ADM | DIR / DIRA | SEC | ALM | Responsável designado | TES e demais cargos | DS |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| Consultar bens, empréstimos, inventários, desejos, históricos, relatórios, CSV, enums e responsáveis | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Cadastrar, editar, excluir, emprestar, devolver, manutenção, baixa, inventário, votação, desejos e atendimentos | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| Designar e revogar o responsável pelo almoxarifado | ✓ | ✓ | ✓ | — | — | — | — |

Sem autenticação válida, a resposta é sempre `401`. Sem permissão, é `403`, com o ProblemDetails padrão. A
decisão é sempre do backend:

- **`GestaoAlmoxarifado`**: cargo ADM, DIR, DIRA, SEC ou ALM (`Roles.Almoxarifado`), **ou** designação vigente do
  usuário como responsável pelo almoxarifado, conferida no banco a cada requisição.
- **`ConsultaAlmoxarifado`**: qualquer cargo ativo exceto DS (`Roles.SemConsultaAlmoxarifado`), ou designação vigente.
  Isso vale também para cargos criados depois, pela tela de cargos.
- **`DesignacaoAlmoxarifado`**: ADM, DIR, DIRA, SEC. São os mesmos cargos que cadastram usuários e podem atribuir o
  cargo ALM.

Decisões do responsável pelo produto (2026-10-23):

- Diretoria, secretaria e almoxarife gerenciam; tesouraria e os demais cargos só consultam; o desbravador não consulta.
- O cargo **Almoxarife (ALM)** gerencia o almoxarifado do clube. A migration o cria só se ele não existir. Esta
  decisão substitui a orientação da issue de não dar gestão automática ao almoxarife de Unidade.
- O responsável pelo almoxarifado é uma **designação separada do cargo** (`/api/Almoxarifado/Responsaveis`):
  - o membro mantém o próprio cargo;
  - não há nova sigla de cargo;
  - revogar vale na requisição seguinte, sem novo login;
  - um responsável designado não designa outros.
- O acervo antigo sem data de aquisição conhecida é marcado como **data desconhecida**, com justificativa obrigatória.
- "Unidade vinculada" ficou **fora desta versão**: não há cadastro de Unidades do clube.

## Enums

Os valores numéricos são estáveis e gravados no banco. Corpos e filtros aceitam o código (nome) ou o número;
qualquer valor fora do enum recebe `400`. Cada resposta traz o código e a descrição (campos `...Descricao`).
`GET /api/Almoxarifado/Enums` devolve `{ valor, codigo, descricao }` de todos os enums.

| Enum | Valores (valor = código: descrição) |
|---|---|
| `EstadoConservacao` | 1 = `Novo`, 2 = `Bom`, 3 = `Regular`, 4 = `Ruim`, 5 = `Inservivel`: Inservível |
| `SituacaoBem` | 1 = `Disponivel`: Disponível, 2 = `Emprestado`, 3 = `EmManutencao`: Em manutenção, 4 = `Baixado` |
| `OrigemBem` | 1 = `Compra`, 2 = `Doacao`: Doação, 3 = `Outro` (exige `origemDetalhe`) |
| `MotivoBaixa` | 1 = `Perda`, 2 = `Furto`, 3 = `Descarte`, 4 = `Doacao`: Doação, 5 = `Outro` |
| `EncerramentoEmprestimo` | 1 = `Devolvido`, 2 = `Perda`, 3 = `Furto` |
| `NivelUrgenciaAlmoxarifado` | 1 = `PoucoUrgenteMasNecessario`: Pouco urgente, mas necessário; 2 = `Urgente`: Urgente; 3 = `MuitoUrgente`: Muito urgente |
| `SituacaoDesejo` | 1 = `Pendente`, 2 = `ParcialmenteAtendida`: Parcialmente atendida, 3 = `Atendida`, 4 = `Cancelada` |
| `TipoItemDesejo` | 1 = `Patrimonial`: Bem patrimonial, 2 = `Consumivel`: Material consumível |
| `OrigemAtendimento` | 1 = `Compra`, 2 = `Doacao`: Doação |
| `TipoMovimentacaoAlmoxarifado` | operação do histórico: `Cadastro`, `Alteracao`, `Exclusao`, `Emprestimo`, `Devolucao`, `PerdaOuFurto`, `EntradaManutencao`, `SaidaManutencao`, `Baixa`, `FechamentoInventario`, `VotacaoInventario`, `CadastroDesejo`, `AlteracaoDesejo`, `CancelamentoDesejo`, `ExclusaoDesejo`, `Atendimento`, `EstornoAtendimento`, `Designacao`, `RevogacaoDesignacao` |

Uso das urgências: **Pouco urgente, mas necessário** é uma necessidade real que pode ser planejada; **Urgente** é
uma falta que compromete uma atividade próxima; **Muito urgente** é uma falta que exige prioridade imediata. A
urgência nunca muda sozinha e é independente de `dataNecessidade`.

## Bens patrimoniais

Cada registro é **um bem físico identificável**: cinco barracas são cinco registros, com conservação e empréstimos
independentes.

- Nomes podem repetir. O **tombo** (`PAT-000001`) é único, estável e gerado pelo banco, a partir de um sequencial
  `IDENTITY` e de uma coluna calculada persistida. Pode haver lacunas na numeração (operação desfeita), mas um
  número nunca é reaproveitado.
- Troféus, livros de atas, álbuns e outros bens sem valor comercial também entram.
- Patrimônio não é estoque de consumíveis.

| Campo | Regra |
|---|---|
| `nome` (150), `descricao` (2000) | obrigatórios |
| `categoria` (60) | opcional, texto livre; filtro por igualdade (sem diferenciar maiúsculas) |
| `estadoConservacao` | obrigatório |
| `possuiValorComercial` | obrigatório |
| `valorEstimado` | `possuiValorComercial = true`: obrigatório e ≥ 0, até 2 casas. `false`: deve ser `null` |
| `dataAquisicao` | obrigatória e não futura, salvo `dataAquisicaoDesconhecida = true` (então deve ser `null`) |
| `dataAquisicaoDesconhecida`, `justificativaDataDesconhecida` (300) | acervo antigo: a data nunca é inventada; a justificativa é obrigatória com a flag e proibida sem ela; o CSV mostra `Desconhecida` |
| `origem`, `origemDetalhe` (300) | `origem` obrigatória; `Outro` exige `origemDetalhe` |
| `localizacao` (150) | obrigatória; filtro por parte do texto |
| `responsavelGuardaId` | opcional; membro ativo (`400` caso contrário) |
| `marca`, `modelo`, `numeroSerie` (100) | opcionais |
| `valorAquisicao` | opcional, ≥ 0, distinto da estimativa atual |
| `observacoes` (1000) | opcional |
| `situacao` | `Disponivel` no cadastro; muda só pelas movimentações |
| `criacao`, `ultimaAlteracao` | `{ por: { id, nome }, emUtc }`, definidos pelo backend |
| `versao` | rowversion (base64), exigida em `PUT` e `DELETE` |

Textos usam `varchar`: o que não cabe na página de código 1252 (emojis, por exemplo) recebe `400`. Propriedades
desconhecidas no corpo também recebem `400`, por exemplo `situacao` num cadastro.

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Almoxarifado/Bens?busca=&categoria=&conservacao=&situacao=&localizacao=&possuiValorComercial=&incluirExcluidos=&page=&pageSize=` | `200` página `{ items, total, page, pageSize, totalPages }`; `busca` procura no nome e no tombo |
| `GET` | `/api/Almoxarifado/Bens/{id}` | `200` (também baixado ou excluído) com `emprestimoAberto`, `manutencaoAberta`, `baixa`, `origemDesejo`, `exclusao`; `404` |
| `POST` | `/api/Almoxarifado/Bens` | `201`; `400` |
| `PUT` | `/api/Almoxarifado/Bens/{id}` | `200`; `400`; `404`; `409` versão desatualizada, bem baixado ou excluído |
| `DELETE` | `/api/Almoxarifado/Bens/{id}` com `{ justificativa, versao }` | `200` exclusão lógica; `409` (ver abaixo) |
| `GET` | `/api/Almoxarifado/Bens/{id}/Historico?de=&ate=&tipo=` | `200` em ordem cronológica; `404` |

A **exclusão lógica** serve só para cadastro indevido:

- exige justificativa;
- recebe `409` se o bem tem empréstimo aberto, porque não pode ocultá-lo;
- recebe `409` se o bem já teve empréstimo, manutenção ou baixa; nesse caso use a baixa, nunca a exclusão, para
  representar descarte;
- recebe `409` se o bem foi recebido num atendimento; nesse caso estorne o atendimento.

Bens excluídos não aparecem nas listas, salvo com `incluirExcluidos=true`.

```http
POST /api/Almoxarifado/Bens
{ "nome": "Livro de atas 1998-2004", "descricao": "Livro de atas da diretoria", "estadoConservacao": "Regular",
  "possuiValorComercial": false, "dataAquisicaoDesconhecida": true,
  "justificativaDataDesconhecida": "Acervo recebido da gestão anterior", "origem": "Doacao", "localizacao": "Secretaria" }

201 { "id": "…", "tombo": "PAT-000042", "situacao": "Disponivel", "situacaoDescricao": "Disponível",
      "valorEstimado": null, "dataAquisicao": null, "dataAquisicaoDesconhecida": true, "versao": "AAAAAAAAB9E=", … }
```

## Empréstimos, devoluções, manutenção e baixa

Toda movimentação é transacional e trava o bem (`UPDLOCK`). Um índice único filtrado garante no máximo um
empréstimo aberto e uma manutenção aberta por bem, inclusive sob concorrência.

As datas não podem ser futuras (fuso do clube). Uma nova movimentação (retirada, entrada em manutenção ou baixa) não
pode ser anterior à aquisição nem ao encerramento da movimentação anterior. Assim, o histórico nunca mostra
empréstimos sobrepostos.

| Método | Rota | Corpo | Regras |
|---|---|---|---|
| `POST` | `/api/Almoxarifado/Bens/{id}/Emprestimos` | `membroId`, `dataRetirada`, `previsaoDevolucao`, `conservacaoSaida`, `observacoes?` | `201`. Só bem `Disponivel` e não inservível; `conservacaoSaida` passa a ser o estado do bem e não pode ser `Inservivel`. `409`: emprestado, em manutenção, inservível, baixado ou excluído. `400`: membro inativo ou datas inválidas. Repetir a requisição recebe `409` (não duplica) |
| `POST` | `/api/Almoxarifado/Emprestimos/{id}/Devolucao` | `dataDevolucao`, `conservacaoRetorno`, `observacoes?`, `encaminharParaManutencao?`, `motivoManutencao?` | `200`. Data entre a retirada e hoje. O bem volta a `Disponivel` ou, com `encaminharParaManutencao`, segue para `EmManutencao`. Devolução repetida: `409` |
| `POST` | `/api/Almoxarifado/Emprestimos/{id}/PerdaFurto` | `data`, `motivo` (`Perda`/`Furto`), `justificativa` | `200`. Encerra o empréstimo e **baixa o bem** com o mesmo motivo |
| `GET` | `/api/Almoxarifado/Emprestimos?bemId=&membroId=&abertos=&atrasados=&page=&pageSize=` | | `200`; `atrasados=true` com `abertos=false`: `400` |
| `GET` | `/api/Almoxarifado/Emprestimos/{id}` | | `200` / `404` |
| `POST` | `/api/Almoxarifado/Bens/{id}/Manutencoes` | `dataEntrada`, `motivo`, `observacoes?` | `201`. `409`: emprestado, já em manutenção, baixado ou excluído |
| `GET` | `/api/Almoxarifado/Bens/{id}/Manutencoes` | | `200` (mais recente primeiro) / `404` |
| `POST` | `/api/Almoxarifado/Manutencoes/{id}/Saida` | `dataSaida`, `conservacaoSaida`, `observacoes?` | `200`. O bem volta a `Disponivel` com a conservação informada. `409` se já encerrada |
| `POST` | `/api/Almoxarifado/Bens/{id}/Baixa` | `dataBaixa`, `motivo`, `justificativa` | `200`. Com empréstimo aberto: `409`; encerre antes como devolução ou perda/furto. Em manutenção: a manutenção é encerrada na data da baixa. O cadastro e o histórico ficam |

O **atraso** é calculado na consulta: empréstimo aberto com `previsaoDevolucao` anterior a hoje. `atrasado` e
`diasAtraso` não dependem de atualização manual. Destinatário externo ao cadastro de membros não integra esta versão.

## Histórico e auditoria

Os triggers `TR_<tabela>_Historico` das nove tabelas do módulo gravam cada inclusão e alteração em
`almoxarifado_historico`. Cada linha registra o estado anterior e o novo, em JSON com todas as colunas, além do
usuário, IP, operação e justificativa do `SESSION_CONTEXT`. É o mesmo mecanismo de `formacao_historico`.

- Sem contexto de auditoria, a escrita é recusada (erro 50130). Toda escrita passa pela API auditada.
- `almoxarifado_historico` é tabela de auditoria: a identidade de runtime só tem `SELECT`, com `DENY` de escrita.
- O histórico de um bem inclui os empréstimos, as manutenções, os inventários e os atendimentos em que ele apareceu.
- O histórico de uma necessidade inclui os atendimentos e os vínculos de bens.

`GET .../Historico` aceita estes filtros:

- `de` e `ate` (`yyyy-MM-dd`, inclusivos, data UTC da operação);
- `tipo`, com um valor de `TipoMovimentacaoAlmoxarifado`.

Cada item traz `tipo`, `tipoDescricao`, `justificativa`, `responsavel`, `ocorridoEmUtc`, `estadoAnterior` e
`estadoNovo`.

## Relatórios

| Método | Rota | Conteúdo |
|---|---|---|
| `GET` | `/api/Almoxarifado/Relatorios/Patrimonio?<filtros dos bens>` | `totalBens`, `porSituacao` (todas as situações, inclusive zero), `bensComValorComercial`, `bensSemValorComercial` e `valorEstimadoPatrimonial` |
| `GET` | `/api/Almoxarifado/Relatorios/Patrimonio/Csv?<filtros>` | CSV: tombo, nome, descrição, categoria, conservação, localização, situação, guarda, aquisição, origem, valor de aquisição, valor comercial, estimativa, excluído |
| `GET` | `/api/Almoxarifado/Relatorios/Emprestimos?atrasados=` | empréstimos abertos (ou só os atrasados), da previsão mais antiga para a mais recente |
| `GET` | `/api/Almoxarifado/Relatorios/Emprestimos/Csv?atrasados=` | o mesmo, em CSV |
| `GET` | `/api/Almoxarifado/Relatorios/Desejos` | por urgência (da maior para a menor): necessidades abertas, quantidade pendente, `custoPendenteEstimado` e `necessidadesSemCustoEstimado` |

`valorEstimadoPatrimonial` soma só as estimativas dos bens **com valor comercial e não baixados**. É identificado
como estimativa patrimonial, **não é saldo financeiro**.

Não há limite monetário nem aprovação automática para "bem de maior valor": isso depende da direção.

Formato dos CSVs:

- separador `;`, decimal com vírgula, datas `yyyy-MM-dd`;
- UTF-8 com BOM (abre com acentos no Excel);
- campos que começam com `=`, `+`, `-`, `@` ou tabulação recebem um apóstrofo, como proteção contra injeção de
  fórmulas.

## Inventário

| Método | Rota | Resultado |
|---|---|---|
| `POST` | `/api/Almoxarifado/Inventarios` com `dataInventario`, `responsavelId`, `observacoes?` | `201` `{ inventario, itens }`: copia os bens não excluídos e não baixados, com totais e valor estimado patrimonial |
| `GET` | `/api/Almoxarifado/Inventarios?page=&pageSize=` | `200` resumo (sem itens), mais recente primeiro |
| `GET` | `/api/Almoxarifado/Inventarios/{id}` | `200` com a fotografia dos itens / `404` |
| `POST` | `/api/Almoxarifado/Inventarios/{id}/Votacao` com `dataVotacao`, `referenciaAta`, `versao` | `200`. Só uma vez (`409` depois). Data entre o inventário e hoje |

O fechamento trava a tabela de bens até o commit, para a fotografia ficar consistente. Os itens são imutáveis: o
trigger `TR_patrimonio_inventario_itens_Imutaveis` recusa `UPDATE`/`DELETE`, e alterações posteriores nos bens não
mudam o inventário.

O cabeçalho do inventário só recebe, uma única vez, a data da votação e a referência da ata. O trigger
`TR_patrimonio_inventarios_Fechado` impede mudar os totais, a data ou o responsável, e também excluir o inventário.

Todo inventário começa com `votado = false` e `votacao = null`. Ele só é apresentado como votado depois que um
usuário autorizado registra os dados da votação: o sistema não simula a aprovação pela Comissão.

## Lista de desejos

Cadastrar uma necessidade **não cria patrimônio, despesa, pagamento nem reserva de dinheiro**.

| Campo | Regra |
|---|---|
| `nome` (150), `descricao` (2000), `justificativa` (1000, uso previsto) | obrigatórios |
| `tipoItem` | obrigatório: `Patrimonial` (o recebimento cria/vincula bens físicos) ou `Consumivel` (o recebimento não gera patrimônio nem estoque) |
| `quantidadeDesejada` | inteiro de 1 a 10000; pode aumentar com histórico, nunca abaixo da atendida (`409`) |
| `quantidadeAtendida`, `quantidadePendente` | calculadas pelos atendimentos não estornados (a pendente é 0 na cancelada) |
| `urgencia` | obrigatória (`NivelUrgenciaAlmoxarifado`) |
| `categoria` (60) | opcional |
| `custoUnitarioEstimado` | opcional, ≥ 0; `null` significa custo desconhecido, não zero |
| `custoTotalEstimado`, `custoPendenteEstimado` | calculados quando há estimativa unitária |
| `dataNecessidade` | opcional |
| `linkReferencia` (500) | opcional, URL `http`/`https` absoluta |
| `observacoes` (1000) | opcional |
| `solicitante`, `criadoEmUtc`, `ultimaAlteracao` | definidos pelo backend |
| `situacao` | `Pendente` / `ParcialmenteAtendida` / `Atendida` decorrem das quantidades (CHECK no banco); `Cancelada` exige motivo |

`tipoItem` não muda depois de atendimentos (`409`). Necessidade cancelada ou excluída não é alterada nem atendida
(`409`).

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Almoxarifado/Desejos?busca=&urgencia=&situacao=&categoria=&tipoItem=&somenteAbertas=&incluirExcluidos=&page=&pageSize=` | `200`; ordem padrão: abertas primeiro, maior urgência e, no empate, a criação mais antiga |
| `GET` | `/api/Almoxarifado/Desejos/{id}` | `200` com `atendimentos` (inclusive estornados) e os bens de cada um / `404` |
| `POST` | `/api/Almoxarifado/Desejos` | `201`; `400` |
| `PUT` | `/api/Almoxarifado/Desejos/{id}` | `200`; `409` (versão, cancelada, excluída, quantidade abaixo da atendida, troca de tipo) |
| `POST` | `/api/Almoxarifado/Desejos/{id}/Cancelar` com `{ justificativa, versao }` | `200`; preserva os atendimentos; `409` se atendida, cancelada ou excluída |
| `DELETE` | `/api/Almoxarifado/Desejos/{id}` com `{ justificativa, versao }` | `200` exclusão lógica, sem perder atendimentos nem histórico |
| `GET` | `/api/Almoxarifado/Desejos/{id}/Historico?de=&ate=&tipo=` | `200` / `404` |

### Atendimentos

`POST /api/Almoxarifado/Desejos/{id}/Atendimentos` exige o header **`Idempotency-Key`** (UUID), como os cadastros de
eventos (ADR-0003).

- `quantidade`, `data` (não futura), `responsavelId` (membro ativo) e `origem` (`Compra`/`Doacao`) são obrigatórios;
  `observacoes` é opcional.
- **Item patrimonial**: `bensNovos` e/ou `bensExistentes` somam **exatamente** a quantidade (um bem físico por
  unidade).
  - `bensNovos` têm os mesmos campos físicos do cadastro de bem; origem e data de aquisição vêm do atendimento.
  - `bensExistentes` são ids de bens não excluídos, não baixados, adquiridos até a data e sem outro vínculo ativo.
  - Cada bem só é vinculado uma vez (índice único filtrado).
- **Consumível**: sem bens.
- O atendimento parcial é permitido: de cinco barracas pedidas, receber duas deixa três pendentes. A quantidade
  atendida não supera a desejada (`409`; aumente a desejada antes).
- `201`: criado, `{ desejo, atendimento, criado: true }`. `200`: reenvio com a mesma chave e os mesmos dados; devolve
  o atendimento original (`criado: false`), sem duplicar recebimentos nem bens, também em requisições simultâneas.
- `409`:
  - chave reutilizada com dados diferentes;
  - quantidade acima da pendente;
  - necessidade cancelada, excluída ou atendida;
  - bem já vinculado, excluído ou baixado.

```http
POST /api/Almoxarifado/Desejos/7f…/Atendimentos
Idempotency-Key: 3b0e0c3a-5d8c-4c8e-9a51-7f0f6d2f2a10

{ "quantidade": 2, "data": "2026-10-20", "responsavelId": "11111111-1111-4111-8111-111111111111", "origem": "Compra",
  "bensNovos": [ { "nome": "Barraca 4 pessoas #1", "descricao": "Barraca iglu", "estadoConservacao": "Novo",
                   "possuiValorComercial": true, "valorEstimado": 380.00, "localizacao": "Almoxarifado" },
                 { "nome": "Barraca 4 pessoas #2", "descricao": "Barraca iglu", "estadoConservacao": "Novo",
                   "possuiValorComercial": true, "valorEstimado": 380.00, "localizacao": "Almoxarifado" } ] }

201 { "desejo": { "situacao": "ParcialmenteAtendida", "quantidadeAtendida": 2, "quantidadePendente": 3, … },
      "atendimento": { "id": "…", "bens": [ { "tombo": "PAT-000043", "criadoPeloAtendimento": true, … }, … ] },
      "criado": true }
```

**Estorno** (`POST /api/Almoxarifado/Desejos/{id}/Atendimentos/{atendimentoId}/Estorno` com `{ justificativa }`) é a
correção auditada de um atendimento indevido. Ele:

- marca o atendimento como estornado;
- desfaz os vínculos;
- exclui logicamente os bens que o atendimento criou;
- recalcula a quantidade atendida.

Os bens existentes vinculados ficam livres. Retorna `409` se o atendimento já foi estornado ou se algum bem criado por
ele já teve empréstimo, manutenção ou baixa: nesse caso o bem continua no patrimônio.

## Responsável pelo almoxarifado

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Almoxarifado/Responsaveis?incluirRevogados=` | `200` (consulta) |
| `POST` | `/api/Almoxarifado/Responsaveis` com `membroId`, `dataInicio` (não futura), `observacoes?` | `201` (ADM, DIR, DIRA, SEC); `409` se o membro já tem designação vigente |
| `POST` | `/api/Almoxarifado/Responsaveis/{id}/Revogar` com `{ motivo, versao }` | `200`; `409` se já revogada |

## Concorrência e erros

- Escritas transacionais travam o registro afetado (`UPDLOCK`). `PUT`, `DELETE`, cancelamento e votação exigem
  `versao`: versão antiga recebe `409`.
- Os índices únicos filtrados são a última barreira contra operações simultâneas. Quando um deles é violado, a
  resposta é `409`, nunca `500`:
  - um empréstimo aberto e uma manutenção aberta por bem;
  - um vínculo ativo por bem;
  - uma designação vigente por membro;
  - uma `Idempotency-Key` por usuário.
- `400`: `ValidationProblemDetails` com `errors` por campo. `404`: ProblemDetails. `409`: ProblemDetails com `title`
  e `detail` explicando o que fazer.

## Banco de dados

A migration `AddAlmoxarifadoPatrimonio` cria as tabelas `patrimonio_bens`, `patrimonio_emprestimos`,
`patrimonio_manutencoes`, `patrimonio_inventarios`, `patrimonio_inventario_itens`, `almoxarifado_desejos`,
`almoxarifado_atendimentos`, `almoxarifado_atendimentos_bens`, `almoxarifado_responsaveis` e `almoxarifado_historico`.
Todas usam `varchar`, sem `nvarchar`. A migration também cria os CHECKs de consistência e os triggers de histórico e
de imutabilidade.

A migration não altera nenhum dado existente, com uma exceção: numa instalação já semeada que não tenha o cargo
`ALM`, ela o cria. Um `ALM` existente, ativo ou não, nunca é alterado. Num banco novo, o seeder cria todos os cargos.

O `Down` remove as tabelas e os triggers e mantém o cargo. Nenhuma variável de ambiente nova.

## Limitações conhecidas

- Não há escopo de acesso por Unidade (não existe cadastro de Unidades). Quem consulta vê todo o almoxarifado do
  clube.
- Os filtros de período do histórico usam a data UTC da operação.
- O fechamento do inventário bloqueia escritas em bens durante o snapshot (milissegundos no volume de um clube).
