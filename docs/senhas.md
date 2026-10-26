# Alteração e recuperação de senha (issue #159)

Três fluxos gravam uma senha nova pela API, todos com a mesma política, a mesma transação e a mesma auditoria:

| Fluxo | Quem confirma | Rota |
|---|---|---|
| Troca própria | o titular, com a **senha atual dele** | `POST /api/Auth/alterar-senha` |
| Troca administrativa | o responsável logado, com a **senha do próprio responsável** (a do afetado nunca é pedida) | `POST /api/Usuarios/{usuarioId}/alterar-senha` |
| Recuperação assistida | o responsável emite uma autorização de uso único depois de confirmar a identidade pessoalmente; o titular conclui sem login | `POST /api/Usuarios/{usuarioId}/recuperacao-senha/autorizacao` e `POST /api/Auth/recuperacao-senha/confirmar` |

O pedido de ajuda sem login (`POST /api/Auth/recuperacao-senha/solicitar`) só coloca o titular numa fila de atendimento; ele não comprova
identidade nem altera nada. A recuperação automática por e-mail **não está disponível**: não há provedor configurado nem canal verificado.

Nenhum fluxo faz login, emite token de acesso ou envia e-mail. Depois de qualquer troca, todas as sessões do titular são revogadas e ele
precisa entrar de novo com a senha nova.

## Matriz de autorização

Lista explícita de pares, sem hierarquia inferida (`Security/PermissoesSenha.cs`). A mesma matriz vale para trocar a senha, listar
pedidos, emitir a autorização e, de novo, no momento em que o titular consome a autorização.

| Responsável logado | Senha de terceiros |
|---|---|
| ADM | SEC, TES e DS |
| DIR | SEC, TES e DS |
| SEC | DS |
| DIRA, TES, DS e demais cargos | nenhum |

- Qualquer usuário autenticado (conta, cargo e sessão ativos) troca a própria senha com a senha atual.
- ADM, DIR e DIRA nunca são alvo; ADM não altera outro ADM nem DIR, e DIR não altera ADM nem outro DIR. DIRA não entra por inferência.
- Ter acesso ao cadastro de usuários (`GestaoCadastros`, que inclui DIRA) não basta. A policy `AlteracaoSenhaTerceiros` (ADM, DIR, SEC)
  é só um filtro inicial: o par é decidido com o cargo do responsável e do alvo **relidos do banco**, nunca do token ou do corpo.
- Ordem das respostas, sem enumeração: cargo sem permissão recebe `403` antes de qualquer consulta ao alvo; quem atende alguém recebe
  `404` para alvo inexistente (esses cargos já listam os usuários em `GET /api/Usuarios`), `403` para par fora da matriz e `409` para
  alvo ou cargo inativo.
- ADM e DIR sem responsável na matriz recuperam o acesso pelo procedimento de emergência (CLI `reset-admin-password`, abaixo); a matriz
  não foi ampliada para isso.

## Contratos

Todas as rotas `POST` exigem HTTPS e `X-CSRF-TOKEN` (`GET /api/Auth/csrf`; nas rotas autenticadas, peça o CSRF **depois** do login,
porque ele fica ligado à identidade). Toda resposta, inclusive `401`, `403`, `429` e erros, sai com `Cache-Control: no-store`. Os
corpos são fechados: propriedade fora do contrato devolve `400` (`"Campo não aceito nesta operação."`); campos internos (responsável,
alvo, IP, data, trace) nunca são lidos do corpo.

### `POST /api/Auth/alterar-senha` (qualquer usuário autenticado)

```json
{ "senhaAtualSolicitante": "<senha atual do próprio usuário>", "novaSenha": "<nova>", "confirmacaoNovaSenha": "<repetição>" }
```

`204` sem corpo: senha trocada, todas as sessões do usuário revogadas e o cookie de refresh removido. `400` dados inválidos ou senha atual
incorreta; `401` sessão inválida; `409` conta alterada ao mesmo tempo; `429` excesso de tentativas.

### `POST /api/Usuarios/{usuarioId}/alterar-senha` (ADM, DIR, SEC pela matriz)

```json
{
  "senhaAtualSolicitante": "<senha do responsável logado>",
  "novaSenha": "<nova senha do afetado>",
  "confirmacaoNovaSenha": "<repetição>",
  "metodoVerificacao": "AtendimentoPresencial"
}
```

`metodoVerificacao` é opcional (`AtendimentoPresencial` ou `ProcedimentoIndependenteEstabelecido`): se o afetado tiver um pedido de
recuperação aberto, esta troca o conclui e registra o método, o responsável, o IP e o instante. `204`: senha do afetado trocada, sessões
dele revogadas e uma notificação interna criada para ele; a sessão e o cookie do responsável continuam válidos. `400` dados inválidos,
senha do responsável incorreta ou alvo igual ao próprio usuário (use a rota própria); `403`; `404`; `409`; `429`.

### `POST /api/Auth/recuperacao-senha/solicitar` (anônimo)

```json
{ "email": "pessoa@exemplo.org" }
```

Sempre `202` com `{"mensagem": "Se for possível localizar sua conta, sua solicitação será tratada. Você também pode procurar um responsável
autorizado do clube."}`, para conta existente, inexistente, inativa, de cargo que ninguém atende ou pedido repetido. Não afirma envio de
e-mail, não devolve token nem dados e não muda senha, sessões ou bloqueio de login. `400` só para e-mail mal formado (ou campo fora do
contrato: CPF, nascimento etc. não são aceitos), `429` por IP.

Pedido de conta ativa, com cargo ativo e que a matriz permite atender (SEC, TES, DS) fica guardado: um registro aberto por usuário
(deduplicação), contagem de repetições limitada, validade de `Senhas:ValidadeSolicitacaoDias` (7 dias, renovada por pedido repetido
enquanto pendente) e nenhuma notificação para a diretoria. Os demais pedidos não deixam rastro.

### `GET /api/Usuarios/recuperacao-senha/pendentes?pageSize=20&cursor=` (ADM, DIR, SEC pela matriz)

Pedidos abertos e não expirados de contas ativas cujo cargo o responsável atende, mais recentes primeiro, com paginação por cursor
(`proximoCursor`):

```json
{
  "items": [
    {
      "id": "…", "usuarioId": "…", "nome": "…", "cargo": { "id": "…", "nome": "Desbravador", "role": "DS" },
      "origem": "SolicitacaoPublica", "status": "Pendente",
      "solicitadaEmUtc": "…Z", "ultimaSolicitacaoEmUtc": "…Z", "solicitacoes": 1, "expiraEmUtc": "…Z",
      "autorizacaoExpiraEmUtc": null
    }
  ],
  "pageSize": 20,
  "proximoCursor": null
}
```

Nunca traz token, IP ou contato.

### `POST /api/Usuarios/{usuarioId}/recuperacao-senha/autorizacao` (ADM, DIR, SEC pela matriz)

```json
{ "senhaAtualSolicitante": "<senha do responsável logado>", "metodoVerificacao": "AtendimentoPresencial" }
```

Só depois de confirmar a identidade do titular pessoalmente ou por procedimento independente já estabelecido pelo clube. **Conhecer CPF,
nascimento, telefone, endereço ou e-mail não comprova identidade**: esses dados servem para localizar o cadastro, nunca como prova, e a API
não tem perguntas de segurança. A declaração do responsável registra um atendimento humano; não é uma prova automática do backend.

`200`, com `Cache-Control: no-store`:

```json
{ "recuperacaoId": "…", "usuarioId": "…", "token": "<43 caracteres>", "expiraEmUtc": "…Z" }
```

Esta resposta é o único lugar em que o token aparece em claro. Entregue-o ao titular pessoalmente e reservadamente; ele não é guardado,
não aparece em listagens, notificações ou logs e não pode ser consultado de novo. Uma nova emissão para o mesmo titular substitui a
anterior. Usa o pedido aberto do titular ou cria um de origem `Atendimento` para quem procurou o clube sem pedido prévio.

### `POST /api/Auth/recuperacao-senha/confirmar` (anônimo)

```json
{ "token": "<token recebido>", "novaSenha": "<nova>", "confirmacaoNovaSenha": "<repetição>" }
```

Sem login e sem senha antiga. `204`: mesmos efeitos da troca administrativa (notificação ao titular inclusive), sem login automático.
Token inválido, expirado, já usado, substituído ou obsoleto (senha trocada depois da emissão), ou autorizador que perdeu a permissão sobre o
titular: `400` genérico `"Autorização de recuperação inválida ou expirada."`, sem alterar nada. `429` por IP. O token nunca vai em query
string.

## Política compartilhada de senha

`Security/PasswordPolicy.cs` vale para toda senha nova: troca própria, troca administrativa, conclusão da recuperação, cadastro de usuário,
seed do administrador e CLI de emergência. Segue o NIST SP 800-63B-4 para senha como único fator:

- 15 a 128 caracteres contados em **pontos de código Unicode** (um emoji conta como um caractere);
- espaços e frases-senha aceitos; sem exigência de maiúsculas, números ou símbolos e sem troca periódica;
- recusa senha formada por um ou dois caracteres, sequência simples, trecho repetido (`azul42azul42azul42`), placeholder, sem letras, raiz
  trivial (`senha`, `admin`, `almirante`…), com a parte local do e-mail do titular ou presente na lista local `SenhasComuns` (senhas
  comuns e vazadas de 15+ caracteres e termos do contexto do clube; a comparação ignora caixa, acentos, pontuação e sufixo numérico). A
  lista é revisada por PR; nenhuma senha é enviada a serviços externos;
- recusa a nova senha igual à atual do titular, conferida com `VerifyHashedPassword` (hashes nunca são comparados);
- confirmação comparada por igualdade **ordinal**: sem trim, sem diferença de caixa, sem truncamento e sem normalização Unicode.

A senha é usada exatamente como digitada, sem normalização, para continuar compatível com os hashes existentes. Senhas antigas não
precisam atender à política nova: o login e a confirmação da senha atual só verificam o hash. A política vale ao **definir** uma senha;
contas existentes não são invalidadas.

### Custo do hash

`IPasswordHasher<Usuario>` (PBKDF2-HMAC-SHA512, formato V3 do ASP.NET Core Identity) passou de 100.000 (padrão do framework) para
**210.000 iterações** (`PasswordPolicy.IteracoesHash`), o valor do OWASP Password Storage Cheat Sheet para PBKDF2-HMAC-SHA512. Hashes antigos
continuam válidos: o login devolve `SuccessRehashNeeded` e regrava o hash com o custo novo. O custo de uma verificação dobra (dezenas a
poucas centenas de milissegundos, conforme o hardware); o rate limit do login e das operações de senha limita o uso disso para negação de
serviço. O hash fictício do login para e-mail inexistente usa o mesmo custo.

## Limites de tentativas

`LimitadorTentativasSenha`, dentro das actions: o middleware de rate limit roda antes da autenticação (não conhece o usuário) e a policy
`login` tem mensagens próprias, então não é reaproveitada.

| Operação | Limite padrão | Configuração |
|---|---|---|
| Troca própria, troca administrativa e emissão de autorização | 5 por usuário **e** 20 por IP a cada 15 min | `Senhas:TentativasPorUsuario`, `Senhas:TentativasPorIp`, `Senhas:JanelaMinutos` |
| Pedido e conclusão de recuperação sem login | 10 por IP a cada 15 min | `Senhas:RecuperacoesPorIp`, `Senhas:JanelaMinutos` |

Cada requisição conta uma vez, antes de qualquer verificação; repetições internas da transação não contam de novo. O IP é o da conexão
depois dos proxies confiáveis (`ReverseProxy:TrustedNetworkCidr`); `X-Forwarded-For` de origem não confiável é ignorado. Excedido: `429`
Problem Details com `Retry-After`. Falhas do responsável **não** bloqueiam nem penalizam a conta do alvo, e o pedido público não bloqueia a
conta. Todas as chaves têm padrão; nenhuma variável de ambiente nova é obrigatória. Como o limite do login, o contador fica em memória por
instância.

## Transação e concorrência

A confirmação da senha (PBKDF2) e o cálculo do hash novo acontecem antes da transação, sobre uma fotografia das contas. Depois, numa única
transação (`SessaoAuditoria.ExecutarEmTransacaoAsync`):

1. trava responsável e alvo (`UPDLOCK, HOLDLOCK`), os cargos deles (`HOLDLOCK`) e a sessão do responsável (`UPDLOCK`), e confere que
   situação, cargo e `SecurityVersion` são os da fotografia e que a sessão continua vigente; na recuperação, trava e confere também a
   autorização;
2. grava o hash novo e incrementa `SecurityVersion` (só se a versão ainda for a lida) e zera o bloqueio de login do titular;
3. revoga todas as sessões do titular (`RevocationReason = alteracao-senha`);
4. encerra a recuperação aberta do titular (concluída pela troca administrativa ou pelo token; invalidada pela troca própria);
5. insere a auditoria e, quando quem age não é o titular, a notificação com o aviso pendente; consome o token, quando houver.

Qualquer falha (inclusive ao gravar a auditoria ou a notificação) desfaz tudo: a senha, a versão e as sessões continuam como antes.

- Login, refresh, troca de cargo, desativação ou outra troca de senha concorrentes esperam o commit ou fazem a operação devolver `409`
  (na recuperação, a resposta genérica). Uma operação HTTP nunca reaproveita uma confirmação de senha feita sobre estado obsoleto.
- `Usuario.SecurityVersion` é token de concorrência e `AuthSession`/`RefreshToken` têm `RowVersion`: um login ou refresh com a credencial
  antiga que perca a corrida falha, e um que vença tem a sessão revogada pela troca. Nenhuma sessão utilizável fica com a versão antiga.
- Commit de resultado desconhecido: o Id da auditoria é gerado antes da transação. Se a execution strategy repetir a operação e a
  auditoria já existir, nada é refeito (sem segunda auditoria, notificação, revogação ou incremento). A emissão repetida regrava o mesmo
  token, sem criar outra autorização.
- O trigger de histórico de `Usuarios` só registra mudanças cadastrais: trocar hash, versão e bloqueio de login não gera linha em
  `_usuarios_hist`, que nunca recebe hash.

## Persistência

### `Usuarios_alteracoes_senha` (auditoria, somente inserção)

| Coluna | Regra |
|---|---|
| `Id` | PK, GUID |
| `UsuarioResponsavelId` | FK `Usuarios`; quem executou (própria/administrativa) ou autorizou (assistida). Nulo **somente** na futura recuperação autônoma |
| `UsuarioAfetadoId` | FK `Usuarios`, obrigatório |
| `IpResponsavel` | `varchar(45)`, IP observado de quem executou a requisição; na recuperação é o IP de quem concluiu, que não estava autenticado |
| `AlteradaEmUtc` | UTC do `TimeProvider` |
| `TipoAlteracao` | 1 própria, 2 administrativa, 3 recuperação assistida, 4 recuperação autônoma (reservado) |
| `TraceId` | `varchar(64)`, trace id W3C da requisição |
| `SolicitacaoRecuperacaoId` | FK `Usuarios_recuperacoes_senha`, única quando preenchida; obrigatória nos tipos 3 e 4 |

CHECKs: própria só com responsável = afetado; administrativa e assistida com responsável diferente; autônoma sem responsável. FKs sem
cascade (o histórico nunca some com o usuário, que é excluído logicamente). Índices por afetado/data e responsável/data. O trigger
`TR_Usuarios_alteracoes_senha_Imutavel` recusa `UPDATE` e `DELETE`. Não há rota de consulta, edição ou exclusão. A tabela não guarda senha,
hash, token nem cópia de dados pessoais; tentativas recusadas ficam só nos logs (sem valores), nunca como falso histórico de sucesso.

### `Usuarios_recuperacoes_senha` (pedidos e autorizações)

Ciclo de vida próprio: `Pendente` → `Autorizada` → `Concluida`, ou `Invalidada` (expirou, a senha mudou por outro fluxo, o autorizador
perdeu a permissão). Guarda alvo, origem, datas e contagem do pedido, IP do pedido público e, na autorização, responsável, IP, método,
instante, **hash SHA-256 do token** (com prefixo de finalidade), validade, `SecurityVersion` do titular na emissão, IP da conclusão e
motivo do encerramento. Índice único filtrado garante uma recuperação aberta por usuário; outro, um hash de token único. CHECKs
mantêm a autorização inteira ou ausente e os status coerentes.

Token: 32 bytes do gerador criptográfico em Base64Url (43 caracteres), validade de 15 minutos, uso único, ligado ao titular e à
`SecurityVersion` dele. O consumo trava a linha e só conclui se ela ainda estiver `Autorizada` com o mesmo hash, dentro da validade e com a
mesma versão: conclusões paralelas resultam em uma única troca.

## Notificação interna

Troca administrativa e recuperação assistida criam, na mesma transação, uma notificação não lida `SenhaAlterada` (tipo 3) só para o
titular, com `ChaveOrigem = senha:{id da auditoria}` (única por destinatário) e o aviso pendente do outbox existente; o publicador é
sinalizado depois do commit. Texto: "Sua senha de acesso foi alterada por um usuário autorizado da administração. Se você não reconhece esta
ação, entre em contato com a administração." (ou a variante da recuperação), sem senha, token, e-mail ou IP. Falha de entrega em tempo
real não desfaz nada; como as sessões foram revogadas, o titular a lê depois do novo login (`GET /api/Notificacoes/Pendentes` e
`POST /api/Notificacoes/{id}/Leitura`). A troca própria não gera notificação. A constraint `CK_notificacoes_Tipo` passou a aceitar 3.

Conexões abertas do hub do titular não caem no instante da troca: `MonitorSessoesNotificacoes` revalida as sessões a cada
`Notificacoes:VerificacaoSessoesMs` (30 s por padrão) e encerra as revogadas; requisições HTTP com os tokens antigos já recebem `401`
imediatamente.

## E-mail: preparado e desabilitado

- `IAvisoSenhaAlteradaEmail.EnviarAvisoSenhaAlteradaAsync` e `ModeloAvisoSenhaAlterada` (assunto e corpo, sem senha) estão prontos. A
  chamada fica **comentada** em `AlteracaoSenhaService`, depois do commit, com `TODO(#159)`: falha de entrega nunca desfará a alteração.
- `IEntregaTokenRecuperacaoSenha` é o contrato futuro da recuperação autônoma por canal verificado.
- Nenhuma das duas tem implementação registrada: não há provedor, credencial, envio, simulação, saída no console nem fila de avisos
  antigos. Habilitar exige provedor configurado e canal (e-mail) do titular previamente verificado; e-mail cadastrado sem verificação não
  é prova.

## Proteção de credenciais em logs e auditoria HTTP

- As cinco rotas com senha ou token levam `[ConteudoSensivel]` e `[HttpLogging(RequestProperties | ResponseStatusCode)]`: se o
  HttpLogging do ASP.NET Core for habilitado, só método, rota e status são registrados. A auditoria de requisições da issue #158 deve
  gravar apenas metadados e o marcador `[conteúdo sensível omitido]` nessas rotas, antes de qualquer buffer, retry ou log auxiliar.
- Os DTOs redigem `ToString()` (o log de argumentos do MVC em `Trace` não expõe o conteúdo); mensagens de validação são fixas e nunca
  incluem o valor recebido; erros de JSON passam pela sanitização existente. Os logs da aplicação registram só ids, tipo e motivo.
- Testes executam os fluxos com log em `Trace` e verificam que nenhuma senha ou token aparece nos logs nem nas respostas de erro.

## CLI de emergência

`dotnet run --project src/Almirante.Api -- reset-admin-password <email>` continua como antes (hash, `SecurityVersion` e revogação das
sessões), agora com a política de 15+ caracteres e encerrando a recuperação aberta da conta. Ele roda sem usuário autenticado, então **não**
grava em `Usuarios_alteracoes_senha` e não inventa responsável: o registro dessa operação é o acesso administrativo ao servidor e ao banco.

## Dependências e limitações

- Recuperação autônoma por e-mail: depende de provedor e de canal verificado (fora do escopo; o tipo 4 da auditoria está reservado).
- O pedido público grava no banco só para contas elegíveis; a resposta é idêntica, mas o tempo de resposta pode variar alguns
  milissegundos entre os casos.
- Limites de tentativas em memória por instância (como o do login).
- Encerramento das conexões do hub em até 30 s, não instantâneo.
- Frontend, MFA e histórico de senhas anteriores estão fora do escopo.
