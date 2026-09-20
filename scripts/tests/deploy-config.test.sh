#!/usr/bin/env bash
# Testes de scripts/deploy-config.sh (leitura de .env como DADO, preflight do modo de deploy e host TLS).
# Uso: bash scripts/tests/deploy-config.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SCRIPT="$ROOT/scripts/deploy-config.sh"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
falhas=0
total=0

ok() { total=$((total + 1)); echo "ok   - $1"; }
falha() { total=$((total + 1)); falhas=$((falhas + 1)); echo "FAIL - $1 :: $2"; }

# get_igual DESCRICAO CONTEUDO_ENV CHAVE VALOR_ESPERADO
get_igual() {
  printf '%s\n' "$2" > "$TMP/.env"
  local obtido
  obtido=$(bash "$SCRIPT" get "$TMP/.env" "$3" 2>&1)
  if [[ "$obtido" == "$4" ]]; then ok "$1"; else falha "$1" "esperado [$4] obtido [$obtido]"; fi
}

# get_recusa DESCRICAO CONTEUDO_ENV CHAVE
get_recusa() {
  printf '%s\n' "$2" > "$TMP/.env"
  if bash "$SCRIPT" get "$TMP/.env" "$3" > /dev/null 2>&1; then falha "$1" "deveria falhar"; else ok "$1"; fi
}

get_igual "valor simples" 'A=valor' A 'valor'
get_igual "espaços nas pontas" 'A=   valor com espaco   ' A 'valor com espaco'
get_igual "comentário inline só após espaço" 'A=abc#def # comentario' A 'abc#def'
get_igual "ponto e vírgula e & literais" 'A=a;b&c' A 'a;b&c'
get_igual "aspas simples preservam \$ e #" "A='p\$ss # w&rd;x'" A 'p$ss # w&rd;x'
get_igual "aspas duplas com espaços e ;" 'A="um valor; com & aspas \" e \\ barra"' A 'um valor; com & aspas " e \ barra'
get_igual "última ocorrência vence" $'A=um\nA=dois' A 'dois'
get_igual "prefixo export" 'export A=x' A 'x'
get_igual "CRLF" $'A=x\r' A 'x'
get_igual "chave ausente" 'B=1' A ''
get_igual "comentário de linha inteira" $'# A=nao\nA=sim' A 'sim'
get_recusa "interpolação sem aspas é recusada" 'A=$HOME' A
get_recusa "interpolação em aspas duplas é recusada" 'A="${HOME}"' A
get_recusa "aspas simples não fechadas" "A='abc" A
get_recusa "aspas duplas não fechadas" 'A="abc' A

# Conteúdo NUNCA é executado: o payload abaixo criaria um arquivo se houvesse source/eval.
PWN="$TMP/executou"
printf 'DEPLOY_MODE=http; touch %s\nB=$(touch %s)\n' "$PWN" "$PWN" > "$TMP/.env"
bash "$SCRIPT" modo "$TMP/.env" > /dev/null 2>&1 || true
bash "$SCRIPT" get "$TMP/.env" B > /dev/null 2>&1 || true
if [[ -e "$PWN" ]]; then falha "conteúdo do .env nunca é executado" "payload executado"; else ok "conteúdo do .env nunca é executado"; fi

# modo
modo() { printf '%s\n' "$1" > "$TMP/.env"; : > "$TMP/out"; bash "$SCRIPT" modo "$TMP/.env" "$TMP/out" > "$TMP/msg" 2>&1; }

modo $'API_HOST_PORT=8090'; if grep -q '^mode=http$' "$TMP/out" && grep -q '^files=-f compose.yaml$' "$TMP/out"; then ok "modo padrão = http"; else falha "modo padrão = http" "$(cat "$TMP/out" "$TMP/msg")"; fi
modo $'DEPLOY_MODE=http\nNGINX_CONF_FILE=nginx.tls.conf'; if [[ $? -ne 0 ]]; then ok "http + nginx.tls.conf recusado"; else falha "http + nginx.tls.conf recusado" ""; fi
modo $'DEPLOY_MODE=qualquer'; if [[ $? -ne 0 ]]; then ok "modo inválido recusado"; else falha "modo inválido recusado" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf'; if [[ $? -ne 0 ]]; then ok "tls sem TLS_PUBLIC_HOST recusado"; else falha "tls sem TLS_PUBLIC_HOST recusado" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf\nTLS_PUBLIC_HOST=api.exemplo.com.br'; if [[ $? -ne 0 ]]; then ok "tls sem certificado recusado antes do deploy"; else falha "tls sem certificado recusado antes do deploy" ""; fi
modo $'API_HOST_PORT=99999'; if [[ $? -ne 0 ]]; then ok "porta inválida recusada"; else falha "porta inválida recusada" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf\nTLS_PUBLIC_HOST=API.exemplo.com;evil'; if [[ $? -ne 0 ]]; then ok "host com caractere inválido recusado"; else falha "host com caractere inválido recusado" ""; fi

echo cert > "$TMP/fullchain.pem"; echo key > "$TMP/privkey.pem"
modo "DEPLOY_MODE=tls
NGINX_CONF_FILE=nginx.tls.conf
TLS_PUBLIC_HOST=api.exemplo.com.br
TLS_CERT_PATH=$TMP/fullchain.pem
TLS_KEY_PATH=$TMP/privkey.pem"
if grep -q '^mode=tls$' "$TMP/out" && grep -q '^files=-f compose.yaml -f compose.tls.yaml$' "$TMP/out" && grep -q '^public_host=api.exemplo.com.br$' "$TMP/out"; then ok "tls completo aceito"; else falha "tls completo aceito" "$(cat "$TMP/out" "$TMP/msg")"; fi

# gerar-host-tls
bash "$SCRIPT" gerar-host-tls "$TMP/gen/host.conf" api.exemplo.com.br
if grep -q 'default 0; "api.exemplo.com.br" 1;' "$TMP/gen/host.conf" && grep -q 'default "api.exemplo.com.br";' "$TMP/gen/host.conf"; then ok "gera mapa de host"; else falha "gera mapa de host" "$(cat "$TMP/gen/host.conf")"; fi
if bash "$SCRIPT" gerar-host-tls "$TMP/gen/x.conf" 'a";evil' > /dev/null 2>&1; then falha "host malicioso não gera arquivo" ""; else ok "host malicioso não gera arquivo"; fi

echo "----"
echo "$((total - falhas))/$total passaram"
exit $(( falhas > 0 ? 1 : 0 ))
