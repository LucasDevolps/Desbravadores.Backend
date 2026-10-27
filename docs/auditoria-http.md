# Auditoria HTTP (issue #158)

A API grava no SQL Server (tabela `auditoria_http`) uma linha por chamada HTTP que chega ao pipeline,
com sucesso ou erro, para análises futuras. Cada registro é sanitizado **antes** de sair da memória da
requisição. Ele não guarda o login, credenciais, CPF, e-mail, telefone, dados de responsáveis, RG/CIN,
saúde, endereço, nascimento, fotos nem arquivos. A decisão de arquitetura está no
[ADR-0016](architecture/adr/0016-auditar-chamadas-http-com-diario-em-disco-e-politica-de-campos-revisados.md).

Esta auditoria complementa o que já existe e não substitui nada: os históricos de negócio (triggers de
`lancamentos_deletados`, `historico_eventos`, `_usuarios_hist`, `formacao_historico`), a auditoria de
senhas (`Usuarios_alteracoes_senha`) e o OpenTelemetry continuam iguais. O `TraceId` do registro é o
mesmo do trace, para correlacionar as fontes.

## Desenho

```
requisição ─► UseForwardedHeaders ─► AuditoriaHttpMiddleware ─► Swagger, exception handler, status pages,
                                         │                        CORS, rate limit, autenticação, controllers,
                                         │                        health checks, hub ...
                                         │ OnCompleted (resposta final)
                                         ▼
                          sanitização (SanitizadorAuditoriaHttp)
                                         │ linha JSON já sanitizada
                                         ▼
                 diário em disco (DiarioAuditoriaHttp) ──► GravadorAuditoriaHttp (BackgroundService)
                 └─ reserva em memória se o disco falhar           │ lote idempotente, a cada 1 s
                                                                   ▼
                                                    auditoria_http (só INSERT)
```

- **Posição no pipeline.** O middleware fica logo depois de `UseForwardedHeaders` (o IP já é o do
  cliente, resolvido só a partir do proxy confiável) e antes de tudo o que pode encerrar a requisição.
  Assim registra também 404, 401, 403, 405, 429 da própria API, erros de validação, exceções, health
  checks, Swagger (quando habilitado) e o hub SignalR.
- **Um registro por chamada.** O registro é montado no `OnCompleted` da resposta, com o status, os
  cabeçalhos e o corpo finais, inclusive o Problem Details escrito pelo exception handler. A exceção
  original vem de `IExceptionHandlerFeature` (tratada) ou do próprio middleware (não tratada). Não há
  INSERT inicial seguido de UPDATE: o `Id` (GUID v7) nasce na captura e é a chave de deduplicação.
- **Unidade de trabalho própria.** O gravador cria um escopo e um `DbContext` novos a cada lote. Ele
  nunca usa o contexto da requisição, então rollback ou falha do negócio não desfaz nem contamina o log.
  Não há tarefa "fire-and-forget": o gravador é um `BackgroundService` supervisionado pelo host.
- **Nada muda para o cliente.** A sanitização trabalha numa cópia. O corpo da requisição é observado
  enquanto a API o lê (o middleware não lê nada por conta própria nem bufferiza o upload). O corpo da
  resposta segue na hora para o cliente e é copiado até o limite. O streaming e os downloads não esperam
  o fim. `SendFile` continua pelo caminho original. Uma falha da auditoria nunca altera a resposta nem
  substitui a exceção original.

### Login fora

`POST /api/Auth/login` fica totalmente fora: o middleware nem acompanha a chamada, nem lê corpo,
resposta, cabeçalhos ou exceção. A exclusão vale pelo endpoint (`[SemAuditoriaHttp]`) e pelo caminho
normalizado (caixa, barras repetidas, barra ou ponto final) em qualquer método. Portanto `GET`, `405` e
`/API//Auth/LOGIN/` também ficam de fora.

### Rotas de credenciais

O resto de `/api/Auth` (CSRF, refresh, logout, `Me`, troca e recuperação de senha) e as rotas
`[ConteudoSensivel]` fora dele (troca administrativa e autorização de recuperação em `/api/Usuarios`)
gravam só metadados: URL sanitizada, método, status, cabeçalhos permitidos, IP, usuário, trace e
duração. O corpo da requisição e o da resposta viram o marcador `[conteúdo sensível omitido]`, com o
tipo e o tamanho.

## Modelo (`auditoria_http`)

| Coluna | Tipo | Regra |
|---|---|---|
| `Id` | `uniqueidentifier`, PK não clusterizada | Gerado na captura; repetir o INSERT não duplica. |
| `Url` | `varchar(2048)` | Esquema, host, porta, path base, caminho e query sanitizados (abaixo). Nunca a URL bruta. |
| `MetodoHttp` | `varchar(16)` | Método canônico; métodos fora de `[A-Za-z-]` viram `OUTRO`. |
| `Request` | `varchar(max)`, JSON | Corpo sanitizado ou marcador: `{"semCorpo":true}` ou `{"omitido":motivo,"tipoConteudo":...,"tamanhoBytes":...}`. |
| `Response` | `varchar(max)`, JSON, nulo | Corpo sanitizado ou marcador; **nulo** sem corpo (204, 304) ou quando nada chegou a ser enviado. |
| `StatusCode` | `int`, nulo | Status efetivamente produzido; **nulo** se o cliente desconectou antes de a resposta começar (nunca o 200 padrão nem o 499 que o ASP.NET Core anota sem enviar). `101` no upgrade de WebSocket. |
| `Headers` | `varchar(max)`, JSON | `{"request":{...},"response":{...}}` só com a lista permitida; `{}` é válido. |
| `Exception` | `varchar(max)`, JSON, nulo | Tipo, `HResult`, código (`SqlException.Number`, status do `ApiProblemException`), pilha técnica e tipos das internas; mensagem sempre `[mensagem omitida]`. Nulo sem exceção. |
| `DataRequestUtc` | `datetimeoffset(7)` | Início da chamada, em UTC. Chave do índice clusterizado. |
| `DuracaoMs` | `int` | Do início ao fim do pipeline (no WebSocket, até o 101). |
| `IpUsuario` | `varchar(45)` | `Connection.RemoteIpAddress` depois de `UseForwardedHeaders` (IPv4 ou IPv6, sem escopo de interface); `INDISPONIVEL` se o servidor não informar. Nunca lido de `X-Forwarded-For` pelo middleware. |
| `IdentificacaoUsuario` | `varchar(36)` | Id interno do token validado (`sub`, conferido com a sessão no banco) ou `ANONIMO`. Nunca o e-mail, nunca algo enviado pelo cliente. |
| `TraceId` | `varchar(64)` | Trace W3C da requisição (o mesmo do OpenTelemetry) ou o identificador do ASP.NET Core. |
| `EstadoCapturaRequest` / `EstadoCapturaResponse` | `int` | 1 Completa, 2 SemCorpo, 3 OmitidaPorSeguranca, 4 OmitidaPorTipo, 5 OmitidaPorTamanho, 6 Truncada, 7 Interrompida, 8 FalhaSanitizacao, 9 NaoLida. |

Índices: clusterizado por `DataRequestUtc`; `(StatusCode, DataRequestUtc)`,
`(IdentificacaoUsuario, DataRequestUtc)` e `TraceId`. Os corpos não são indexados. CHECKs garantem JSON
válido, os estados, o formato do usuário, `DuracaoMs >= 0` e o status entre 100 e 999.

**Só inserção.** O trigger `TR_auditoria_http_Imutavel` recusa `UPDATE` e `DELETE` (erro 50158), até
para o dono do banco. A role de runtime recebe só `SELECT` e `INSERT`, com `DENY UPDATE, DELETE`
(`DbCredentialManager`). O `DbPrivilegeAuditor` acusa um `UPDATE` concedido a mais. Não há endpoint de
consulta, alteração ou exclusão, nem limpeza automática ou expiração nesta entrega.

## Política de sanitização

Uma política única, `SanitizadorAuditoriaHttp`, vale para URL, query, parâmetros de rota, corpos JSON,
cabeçalhos e exceções, inclusive em objetos e listas aninhados.

1. **Lista explícita de campos revisados** (`SanitizadorAuditoriaHttp.Revisados`). Cada nome tem uma
   categoria que diz qual formato de valor pode ser gravado. Um campo que não está na lista, e tudo o
   que estiver dentro dele, vira `[campo não revisado omitido]`. Campo novo não aparece na auditoria
   até alguém revisá-lo ali.
2. **Credenciais e dados pessoais vencem a lista.** Senha, hash, token (access, refresh, CSRF, JWT),
   cookie, `Authorization`, chave de API, segredo e connection string viram `[credencial omitida]`. CPF,
   e-mail, telefone, celular, WhatsApp, contato, responsável, RG, CIN, documento, tipo sanguíneo,
   doenças e alergias, saúde, endereço (CEP, logradouro, bairro, UF...), nascimento, foto, imagem,
   arquivo, anexo e IP dentro do corpo viram `[dado pessoal omitido]`. O nome é comparado sem acento,
   sem caixa e sem separadores (`responsavel_cpf`, `ResponsavelCpf`, `e-mail`, `phoneNumber`,
   `bloodType`, `birthDate`...).
3. **O nome não basta: o valor também precisa ter o formato esperado.** Um id só passa se for GUID,
   inteiro de até 9 dígitos ou identificador de trace. Números passam até 9 dígitos na parte inteira
   (CPF e telefone não passam nem em campo numérico). Datas passam no formato ISO. Enums e códigos
   passam como uma palavra, sem espaço e com menos de 6 dígitos. A finalidade de lançamento só passa se
   estiver na lista fixa. O título do Problem Details passa sem `@` e com menos de 6 dígitos. Fora do
   formato, o valor vira `[valor fora do formato esperado omitido]`.
4. **Texto livre nunca é gravado.** Nome, descrição, motivo, local, observação, busca, `detail` e as
   mensagens de `errors` viram `[texto omitido: N caracteres]`. Do `errors` ficam só as chaves (os
   campos que falharam).
5. **Chaves também são verificadas.** Uma chave de objeto ou de query fora de
   `[A-Za-z_$][A-Za-z0-9_$.-[]]` (por exemplo, um e-mail usado como chave) vira `[chave omitida N]`.
6. **Só JSON é sanitizado.** Multipart, imagem, texto, binário, conteúdo comprimido e WebSocket não
   caem em gravação bruta: ficam só o tipo de mídia (sem parâmetros como `boundary`) e o tamanho.
   JSON inválido ou falha da sanitização gera `FalhaSanitizacao`, sem conteúdo.
7. **Sanitizar antes de truncar.** O corpo só é analisado inteiro: acima de `LimiteCapturaBytes`, a
   cópia é descartada e fica `OmitidaPorTamanho`. O JSON já sanitizado é cortado em
   `LimiteGravacaoCaracteres` e guardado como `{"truncado":true,"conteudoParcial":"..."}`. Listas
   guardam no máximo 200 itens, e a profundidade é limitada a 32 níveis.
8. **URL.** Os segmentos literais da rota resolvida (ou de qualquer rota da API) ficam. Os parâmetros
   da rota seguem a categoria do nome: `{id:guid}` mantém o GUID e `{cep}` vira `[omitido]`. Num
   caminho desconhecido (404), só ficam GUIDs e inteiros curtos. A query mantém a ordem, e cada
   parâmetro segue a mesma política (`access_token`, `busca` e `cpf` viram `[omitido]`). O host só passa
   se tiver formato de nome ou IP.
9. **Cabeçalhos por lista permitida.** Da requisição: `Accept`, `Accept-Language`, `Content-Type`,
   `Content-Length`, `Content-Encoding`, `Transfer-Encoding`, `Upgrade`, `Sec-WebSocket-Version`,
   `X-Requested-With`, `Idempotency-Key` (só se for GUID), `Origin` e `User-Agent` (sem `@`, sem
   sequência longa de dígitos e sem CPF ou telefone com separadores). As listas enviadas pelo cliente
   (`Accept`, `Accept-Language`, `Content-Encoding`, `Transfer-Encoding`, `Upgrade`, `X-Requested-With`)
   passam só com até 5 dígitos fora dos pesos `q=`: `pt-BR,52998224725` ou `529.982.247-25` viram
   marcador, `pt-BR,pt;q=0.9,es-419;q=0.8` passa inteiro. Da resposta: `Content-Type`, `Content-Length`, `Content-Encoding`,
   `Cache-Control`, `Retry-After`, `Allow`, `WWW-Authenticate` (só o esquema e o código `error`) e
   `Location` (sanitizado como URL). `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`,
   `X-CSRF-TOKEN` e `X-Api-Key` aparecem só como presença (`[credencial omitida]`). Nenhum outro
   cabeçalho é copiado, inclusive `X-Forwarded-For` e `Referer`.
10. **Exceções.** Nunca se usa `ToString()`, `Message`, `Data` nem mensagens de SQL. Ficam o tipo, o
    código, o `HResult`, a pilha (só nomes de tipos e métodos, até 40 quadros) e os tipos das exceções
    internas.
11. **Antes de qualquer destino intermediário.** O diário em disco, a reserva em memória, as novas
    tentativas e a pasta de rejeitados só recebem o registro já sanitizado. O log do mecanismo traz
    apenas o `Id`, contagens, tipos de exceção e números de erro SQL, nunca conteúdo.
12. **Varchar.** O JSON gravado tem só ASCII e Latin-1 (o resto sai como `\uXXXX`), então cabe nas
    colunas `varchar` (code page 1252) sem trocar caractere por `?`.

### Revisar um campo novo

Quando um DTO, uma query ou uma rota ganhar um campo cujo valor deve aparecer na auditoria:

1. Inclua o nome normalizado (minúsculo, sem acento e sem separadores) na categoria certa em
   `SanitizadorAuditoriaHttp.Revisados`. Se for um objeto ou uma lista, use `Estrutura`: os filhos são
   classificados um a um.
2. Rode `AuditoriaHttpSanitizadorTests`. O teste `ListaRevisada_NenhumNomeEhCredencialOuPessoal...`
   falha se o nome cair numa regra de credencial ou de dado pessoal.
3. Nunca revise texto livre como outra categoria. Se o valor puder conter dado pessoal, deixe-o como
   `TextoLivre` ou fora da lista.

## Cobertura e limites

- **Coberto:** toda chamada que chega ao processo da API, sem amostragem e em todos os métodos (inclusive
  `OPTIONS`, `GET` sem corpo, health checks, Swagger e negociação, long polling e handshake do SignalR).
- **Fora:** o login. Também fica fora qualquer resposta produzida só pelo Nginx ou pelo IIS: o rate
  limit `limit_req` do Nginx, os 413 e 502/504 do proxy, as falhas de TLS e as requisições recusadas
  pelo Kestrel antes do pipeline (cabeçalho inválido, por exemplo). Essas respostas aparecem no access
  log do proxy, não aqui. Os frames do WebSocket também ficam fora: não são requisições HTTP.
- **Momento do registro:**
  - chamada comum: quando a resposta termina (`OnCompleted`);
  - upgrade de WebSocket: no `101`, sem esperar a conexão fechar (`Response` com o marcador "frames
    fora do escopo");
  - long polling e downloads: no fim, com o tamanho enviado.
- **Cancelamento e desconexão:** o estado da resposta é `Interrompida`. O status só é gravado se os
  cabeçalhos saíram antes da desconexão; senão fica nulo. Uma requisição cujo corpo a API não leu (401
  ou 403 antes do model binding, por exemplo) fica com `Request` em `NaoLida`, com tipo e tamanho.

## Garantia de entrega

Garantia implementada: **toda chamada auditada que chegou ao diário em disco é inserida na tabela uma
única vez**, mesmo com banco indisponível, reinício ou queda do processo.

| Situação | Comportamento |
|---|---|
| Normal | A linha sanitizada é anexada ao segmento ativo do processo, com flush para o sistema operacional, no fim da resposta. A cada `IntervaloGravacaoMs` (1 s), o gravador fecha o segmento (com fsync), insere os registros em lotes de `TamanhoLote` e só então apaga o arquivo. |
| Banco indisponível | Nada é apagado. O gravador tenta de novo com espera dobrando até `EsperaMaximaSegundos` (60 s). Depois de 1 minuto em falha, o health check fica `Degraded`. |
| Nova tentativa / duplicidade | O INSERT pula os `Id` que já existem. Repetir um segmento, um lote ou a reserva não duplica nada. |
| Registro recusado pelo banco (constraint, tamanho) | É isolado do lote, guardado em `<diretório>/rejeitados/AAAAMMDD.jsonl` (já sanitizado) e logado com o `Id`. Os demais registros seguem. |
| Reinício ou queda do processo | No início, o diário soma os segmentos existentes. O gravador retoma os segmentos `.pendente` e os `.ativo` órfãos (de um processo que caiu). Uma linha cortada pela queda é ignorada e contada no log. |
| Duas instâncias na mesma pasta (reciclagem sobreposta do IIS) | Cada processo escreve no próprio segmento. Um processo só processa um arquivo que consegue abrir com exclusividade; o segmento ativo do outro fica intocado. |
| Disco indisponível ou diário acima de `LimiteDiarioMb` | Os registros vão para uma reserva em memória (`LimiteReservaMemoria`), gravada no banco pelo mesmo gravador. Erro no log e health check `Degraded`. |
| Reserva esgotada | Os registros são **descartados**: contados, com erro no log (o primeiro `Id` e um resumo periódico) e health check `Degraded`. |
| Encerramento normal | O gravador tenta uma última gravação de até 5 s. O que sobrar fica no disco para o próximo início. |

**Limites reais:**

- Os registros só em memória (reserva) se perdem se o processo cair.
- Uma queda de energia pode perder as linhas ainda não sincronizadas do segmento ativo (até um
  intervalo de gravação), porque o flush por requisição vai para o sistema operacional e o fsync
  acontece na rotação.
- Uma queda do processo entre o fim da resposta e o `OnCompleted` perde aquela chamada.
- Banco, disco e memória esgotados ao mesmo tempo significam descarte, sempre sinalizado.

Não há promessa de "zero perda".

## Configuração

Seção `AuditoriaHttp` (appsettings ou variáveis `AuditoriaHttp__*`). Todos os valores têm padrão:
nenhuma variável de ambiente nova é obrigatória.

| Chave | Padrão | Uso |
|---|---|---|
| `Habilitada` | `true` | Desliga o middleware inteiro. |
| `DiretorioPendentes` | pasta temporária + hash do servidor/banco | Diário em disco. No Compose: `/var/lib/almirante/auditoria-http`, volume `almirante-auditoria-http`. |
| `LimiteCapturaBytes` | 65536 | Corpo JSON analisado por direção; acima disso, `OmitidaPorTamanho`. |
| `LimiteGravacaoCaracteres` | 16384 | Conteúdo sanitizado gravado por corpo; acima disso, `Truncada`. |
| `LimiteDiarioMb` | 256 | Espaço máximo dos segmentos pendentes. |
| `LimiteReservaMemoria` | 5000 | Registros guardados em memória quando o disco falha ou enche. |
| `IntervaloGravacaoMs` | 1000 | Ciclo do gravador (latência até o banco). |
| `TamanhoLote` | 100 | Registros por INSERT. |
| `EsperaMaximaSegundos` | 60 | Teto da espera entre tentativas com o banco fora. |

**Docker Compose:** `compose.yaml` define a pasta e o volume nomeado; o `Dockerfile` cria a pasta com o
dono `app` e a permissão 700. **IIS:** sem configuração, o diário fica na pasta temporária do perfil do
app pool. Para que ele sobreviva a limpezas do sistema, defina `AuditoriaHttp__DiretorioPendentes` nas
variáveis do app pool, apontando para uma pasta fora do site com a mesma ACL da pasta de fotos (só o pool
lê, grava e apaga). Se a pasta não aceitar escrita, a API sobe mesmo assim: usa a reserva em memória,
loga o erro e marca o health check como `Degraded`.

## Custos

- **Por requisição:** uma cópia de até `LimiteCapturaBytes` por direção (só para JSON), a análise e a
  sanitização desse JSON e uma escrita sequencial com flush no diário (uma linha de alguns KB). O `Id`,
  a URL e o IP são calculados no início; o resto, no `OnCompleted`, depois que a resposta já saiu.
- **Banco:** um INSERT em lote por ciclo. As colunas de corpo são limitadas a ~16 mil caracteres cada,
  e uma chamada típica gera menos de 2 KB. A tabela cresce sem limpeza nesta entrega: planeje o expurgo
  (fora do escopo; exige retirar o trigger em uma migration própria) a partir do volume real.

## Consultas

O runtime e a identidade `adm-ti` (view `ti.auditoria_http`, criada por
[`docs/sql/criar-usuario-adm-ti.sql`](sql/criar-usuario-adm-ti.sql)) podem consultar:

```sql
-- Erros das últimas 24 h
SELECT DataRequestUtc, MetodoHttp, Url, StatusCode, DuracaoMs, IdentificacaoUsuario, TraceId
FROM ti.auditoria_http
WHERE StatusCode >= 400 AND DataRequestUtc >= DATEADD(day, -1, SYSUTCDATETIME())
ORDER BY DataRequestUtc DESC;

-- Uma chamada a partir do trace do OpenTelemetry
SELECT * FROM ti.auditoria_http WHERE TraceId = '0af7651916cd43dd8448eb211c80319c';

-- Rotas mais lentas
SELECT TOP 20 MetodoHttp, Url, AVG(DuracaoMs) AS MediaMs, COUNT(*) AS Chamadas
FROM ti.auditoria_http GROUP BY MetodoHttp, Url ORDER BY MediaMs DESC;
```

## Operação

- **Saúde:** `/health` inclui a verificação `auditoria-http`. Em `Degraded`, a resposta continua 200 e o
  deploy não é afetado.
- **Logs:** `DiarioAuditoriaHttp` e `GravadorAuditoriaHttp` registram falhas de disco e de banco, a volta
  ao normal, linhas incompletas, registros rejeitados e descartes, sempre sem conteúdo.
- **Pasta do diário:** `*.ativo` é o segmento sendo escrito por um processo vivo; `*.pendente` aguarda o
  banco; `rejeitados/` guarda o que o banco recusou de forma permanente, para análise.
- **Testes:**
  - `AuditoriaHttpSanitizadorTests`: política, unitário.
  - `AuditoriaHttpTests`: pipeline completo com banco em memória.
  - `AuditoriaHttpDiarioTests`: durabilidade, saturação e health check.
  - `AuditoriaHttpSqlServerTests`: SQL Server real (persistência, nulabilidade, rollback,
    imutabilidade, deduplicação, rejeição e permissões).
