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
# Identidades SQL válidas por padrão (o preflight as exige); os testes específicos abaixo as sobrescrevem.
SQL_OK=$'SQL_SA_PASSWORD=Sa.Senha.Do.Servidor.2026\nSQL_ADMIN_USER=almirante_admin_bd\nSQL_ADMIN_PASSWORD=Admin.Senha.Dedicada.2026-Zz9'
modo() { printf '%s\n%s\n' "$SQL_OK" "$1" > "$TMP/.env"; : > "$TMP/out"; bash "$SCRIPT" modo "$TMP/.env" "$TMP/out" > "$TMP/msg" 2>&1; }
# modo_sql: só o conteúdo dado (sem as identidades SQL padrão)
modo_sql() { printf '%s\n' "$1" > "$TMP/.env"; : > "$TMP/out"; bash "$SCRIPT" modo "$TMP/.env" "$TMP/out" > "$TMP/msg" 2>&1; }

modo $'API_HOST_PORT=8090'; if grep -q '^mode=http$' "$TMP/out" && grep -q '^files=-f compose.yaml$' "$TMP/out"; then ok "modo padrão = http"; else falha "modo padrão = http" "$(cat "$TMP/out" "$TMP/msg")"; fi
modo $'DEPLOY_MODE=http\nNGINX_CONF_FILE=nginx.tls.conf'; if [[ $? -ne 0 ]]; then ok "http + nginx.tls.conf recusado"; else falha "http + nginx.tls.conf recusado" ""; fi
modo $'DEPLOY_MODE=qualquer'; if [[ $? -ne 0 ]]; then ok "modo inválido recusado"; else falha "modo inválido recusado" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf'; if [[ $? -ne 0 ]]; then ok "tls sem TLS_PUBLIC_HOST recusado"; else falha "tls sem TLS_PUBLIC_HOST recusado" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf\nTLS_PUBLIC_HOST=api.exemplo.com.br'; if [[ $? -ne 0 ]]; then ok "tls sem certificado recusado antes do deploy"; else falha "tls sem certificado recusado antes do deploy" ""; fi
modo $'API_HOST_PORT=99999'; if [[ $? -ne 0 ]]; then ok "porta inválida recusada"; else falha "porta inválida recusada" ""; fi
modo $'DEPLOY_MODE=tls\nNGINX_CONF_FILE=nginx.tls.conf\nTLS_PUBLIC_HOST=API.exemplo.com;evil'; if [[ $? -ne 0 ]]; then ok "host com caractere inválido recusado"; else falha "host com caractere inválido recusado" ""; fi
# https: HTTPS local (compose.https.yaml) na porta API_HTTPS_HOST_PORT
modo $'DEPLOY_MODE=https\nAPI_HOST_PORT=8091\nAPI_HTTPS_HOST_PORT=8444'
if grep -q '^mode=https$' "$TMP/out" && grep -q '^files=-f compose.yaml -f compose.https.yaml$' "$TMP/out" \
  && grep -q '^port=8091$' "$TMP/out" && grep -q '^https_port=8444$' "$TMP/out"; then ok "https usa compose.https.yaml e a porta HTTPS do .env"
else falha "https usa compose.https.yaml e a porta HTTPS do .env" "$(cat "$TMP/out" "$TMP/msg")"; fi
modo $'DEPLOY_MODE=https'; if grep -q '^https_port=8443$' "$TMP/out"; then ok "https sem porta usa 8443"; else falha "https sem porta usa 8443" "$(cat "$TMP/out" "$TMP/msg")"; fi
modo $'DEPLOY_MODE=https\nNGINX_CONF_FILE=nginx.tls.conf'; if [[ $? -ne 0 ]]; then ok "https + nginx.tls.conf recusado"; else falha "https + nginx.tls.conf recusado" ""; fi
modo $'DEPLOY_MODE=https\nAPI_HTTPS_HOST_PORT=0'; if [[ $? -ne 0 ]]; then ok "porta HTTPS inválida recusada"; else falha "porta HTTPS inválida recusada" ""; fi
modo $'DEPLOY_MODE=https\nAPI_HOST_PORT=8444\nAPI_HTTPS_HOST_PORT=8444'; if [[ $? -ne 0 ]]; then ok "HTTP e HTTPS na mesma porta recusados"; else falha "HTTP e HTTPS na mesma porta recusados" ""; fi
modo $'API_HTTPS_HOST_PORT=8444'; if grep -q '^mode=http$' "$TMP/out" && grep -q '^https_port=$' "$TMP/out"; then ok "sem DEPLOY_MODE continua http"; else falha "sem DEPLOY_MODE continua http" "$(cat "$TMP/out" "$TMP/msg")"; fi

# identidades SQL (sem fallback para sa)
modo_sql $'SQL_SA_PASSWORD=Sa.Senha.2026'; if [[ $? -ne 0 ]] && grep -q 'SQL_ADMIN_USER ausente' "$TMP/msg"; then ok "sem SQL_ADMIN_USER recusado (sem fallback para sa)"; else falha "sem SQL_ADMIN_USER recusado" "$(cat "$TMP/msg")"; fi
modo_sql $'SQL_ADMIN_USER=almirante_admin_bd'; if [[ $? -ne 0 ]] && grep -q 'SQL_ADMIN_PASSWORD ausente' "$TMP/msg"; then ok "sem SQL_ADMIN_PASSWORD recusado"; else falha "sem SQL_ADMIN_PASSWORD recusado" "$(cat "$TMP/msg")"; fi
modo_sql $'SQL_ADMIN_USER=SA
SQL_ADMIN_PASSWORD=Admin.Senha.Dedicada.2026-Zz9'; if [[ $? -ne 0 ]] && grep -q "não pode ser 'sa'" "$TMP/msg"; then ok "SQL_ADMIN_USER=sa recusado"; else falha "SQL_ADMIN_USER=sa recusado" "$(cat "$TMP/msg")"; fi
modo_sql $'SQL_ADMIN_USER=almirante_admin_bd
SQL_ADMIN_PASSWORD=DEFINA_UMA_SENHA_FORTE_FORA_DO_GIT'; if [[ $? -ne 0 ]] && grep -q 'placeholder' "$TMP/msg"; then ok "placeholder de SQL_ADMIN_PASSWORD recusado"; else falha "placeholder recusado" "$(cat "$TMP/msg")"; fi
modo_sql $'SQL_SA_PASSWORD=Mesma.Senha.Dos.Dois.2026
SQL_ADMIN_USER=almirante_admin_bd
SQL_ADMIN_PASSWORD=Mesma.Senha.Dos.Dois.2026'; if [[ $? -ne 0 ]] && grep -q 'igual a SQL_SA_PASSWORD' "$TMP/msg"; then ok "senha administrativa igual à do sa recusada"; else falha "senha igual à do sa recusada" "$(cat "$TMP/msg")"; fi
modo_sql $'SQL_ADMIN_USER=Almirante_User_BD
SQL_ADMIN_PASSWORD=Admin.Senha.Dedicada.2026-Zz9'; if [[ $? -ne 0 ]] && grep -q 'igual a SQL_APP_USER' "$TMP/msg"; then ok "administrador igual ao runtime recusado"; else falha "administrador igual ao runtime recusado" "$(cat "$TMP/msg")"; fi
if grep -rqF 'Admin.Senha.Dedicada' "$TMP/msg"; then falha "mensagens não imprimem valores" "vazou"; else ok "mensagens não imprimem valores"; fi

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

# completar-env: chaves novas do .env.example entram no .env da máquina sem mudar o comportamento atual
CE="$TMP/ce"; mkdir -p "$CE"
cat > "$CE/compose.yaml" <<'EOF'
services:
  api:
    environment:
      ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}
      Sql__Trust: ${SQL_TRUST_SERVER_CERTIFICATE:-False}
      Sa: ${SQL_SA_PASSWORD:?defina}
      Admin: ${SQL_ADMIN_PASSWORD:?defina}
      Seed: ${SEED_ADMIN_SENHA:?defina}
      Usuario: ${SQL_ADMIN_USER:?defina}
      Vazia: ${JWT_KEY_V1:-}
EOF
cat > "$CE/exemplo" <<'EOF'
# comentário
SQL_SA_PASSWORD=DEFINA_A_SENHA_DO_SA
SQL_ADMIN_USER=almirante_admin_bd
SQL_ADMIN_PASSWORD=DEFINA_UMA_SENHA_FORTE_FORA_DO_GIT
ASPNETCORE_ENVIRONMENT=Development
SQL_TRUST_SERVER_CERTIFICATE=True
JWT_KEY_V1=CHAVE_DO_EXEMPLO
SEED_ADMIN_SENHA=DEFINA_A_SENHA_DO_ADMIN
CHAVE_NOVA_SEM_PADRAO=valor do exemplo
#OPCIONAL_COMENTADA=nao_entra
EOF
printf 'SQL_SA_PASSWORD=Sa.Senha.Atual.2026\r\nSQL_ADMIN_PASSWORD=Admin.Senha.Atual.2026-Zz9' > "$CE/.env"   # CRLF e sem \n final
chmod 640 "$CE/.env"
bash "$SCRIPT" completar-env "$CE/exemplo" "$CE/.env" "$CE/compose.yaml" > "$CE/msg" 2>&1; rc=$?
v() { bash "$SCRIPT" get "$CE/.env" "$1"; }
[[ $rc -eq 0 ]] && ok "completar-env roda" || falha "completar-env roda" "$(cat "$CE/msg")"
[[ "$(v ASPNETCORE_ENVIRONMENT)" == "Production" ]] && ok "chave com padrão do Compose recebe o padrão (não o Development do exemplo)" || falha "padrão do Compose" "$(v ASPNETCORE_ENVIRONMENT)"
[[ "$(v SQL_TRUST_SERVER_CERTIFICATE)" == "False" ]] && ok "SQL_TRUST_SERVER_CERTIFICATE mantém False" || falha "trust mantém False" "$(v SQL_TRUST_SERVER_CERTIFICATE)"
[[ -z "$(v JWT_KEY_V1)" ]] && grep -q '^JWT_KEY_V1=$' "$CE/.env" && ok "padrão vazio do Compose vira chave vazia (não o valor do exemplo)" || falha "padrão vazio" "$(v JWT_KEY_V1)"
seed=$(v SEED_ADMIN_SENHA)
[[ ${#seed} -eq 40 && "$seed" != DEFINA_* && "$seed" =~ [A-Z] && "$seed" =~ [a-z] && "$seed" =~ [0-9] ]] && ok "segredo sem padrão é gerado (40, sem placeholder)" || falha "segredo gerado" "tamanho ${#seed}"
[[ "$(v SQL_ADMIN_USER)" == "almirante_admin_bd" ]] && ok "obrigatória sem padrão e não secreta vem do exemplo" || falha "valor do exemplo" "$(v SQL_ADMIN_USER)"
[[ "$(v CHAVE_NOVA_SEM_PADRAO)" == "valor do exemplo" ]] && ok "valor com espaço é escrito entre aspas e lido igual" || falha "valor com espaço" "$(v CHAVE_NOVA_SEM_PADRAO)"
[[ "$(v SQL_SA_PASSWORD)" == "Sa.Senha.Atual.2026" && "$(v SQL_ADMIN_PASSWORD)" == "Admin.Senha.Atual.2026-Zz9" ]] && ok "chaves existentes não mudam" || falha "existentes não mudam" ""
grep -q 'OPCIONAL_COMENTADA' "$CE/.env" && falha "chave comentada não entra" "" || ok "chave comentada do exemplo não entra"
grep -qE 'Atual|DEFINA|Production|almirante_admin' "$CE/msg" && falha "log sem valores" "$(cat "$CE/msg")" || ok "log mostra só nomes de chave"
ls "$CE"/.env.bak-* > /dev/null 2>&1 && [[ "$(stat -c %a "$CE"/.env.bak-* | head -1)" == "600" ]] && ok "backup do .env anterior com modo 600" || falha "backup 600" "$(ls -l "$CE")"
[[ "$(stat -c %a "$CE/.env")" == "640" ]] && ok "permissão do .env preservada" || falha "permissão preservada" "$(stat -c %a "$CE/.env")"
antes=$(md5sum < "$CE/.env"); bash "$SCRIPT" completar-env "$CE/exemplo" "$CE/.env" "$CE/compose.yaml" > "$CE/msg2" 2>&1
[[ "$(md5sum < "$CE/.env")" == "$antes" ]] && grep -q 'já tem todas as chaves' "$CE/msg2" && [[ $(ls "$CE"/.env.bak-* | wc -l) -eq 1 ]] && ok "segunda execução não muda nada nem cria backup" || falha "idempotente" "$(cat "$CE/msg2")"
printf 'SQL_ADMIN_PASSWORD=Admin.Senha.Atual.2026-Zz9\n' > "$CE/sem-sa.env"; antes=$(md5sum < "$CE/sem-sa.env")
if bash "$SCRIPT" completar-env "$CE/exemplo" "$CE/sem-sa.env" "$CE/compose.yaml" > "$CE/msg3" 2>&1; then falha "sem SQL_SA_PASSWORD para antes de mudar" ""
elif [[ "$(md5sum < "$CE/sem-sa.env")" == "$antes" ]] && grep -q 'SQL_SA_PASSWORD ausente' "$CE/msg3"; then ok "SQL_SA_PASSWORD ausente nunca é gerada: falha sem alterar o .env"
else falha "sem SQL_SA_PASSWORD para antes de mudar" "$(cat "$CE/msg3")"; fi

# O .env.example real completa um .env vazio de chaves opcionais sem trocar o comportamento do compose.yaml real.
printf 'SQL_SA_PASSWORD=Sa.Senha.Atual.2026\n' > "$CE/real.env"
bash "$SCRIPT" completar-env "$ROOT/.env.example" "$CE/real.env" "$ROOT/compose.yaml" "$ROOT/compose.https.yaml" "$ROOT/compose.tls.yaml" > "$CE/msg4" 2>&1
r() { bash "$SCRIPT" get "$CE/real.env" "$1"; }
if [[ "$(r ASPNETCORE_ENVIRONMENT)" == "Production" && "$(r SQL_TRUST_SERVER_CERTIFICATE)" == "False" && -n "$(r SQL_ADMIN_PASSWORD)" && "$(r SQL_ADMIN_PASSWORD)" != DEFINA_* ]] \
  && bash "$SCRIPT" modo "$CE/real.env" /dev/null > /dev/null 2>&1; then
  ok ".env.example real: Production, trust False, senha gerada e preflight do modo passa"
else falha ".env.example real" "$(cat "$CE/msg4")"; fi

echo "----"
echo "$((total - falhas))/$total passaram"
exit $(( falhas > 0 ? 1 : 0 ))
