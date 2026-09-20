#!/usr/bin/env bash
# Leitura SEGURA da configuração de deploy (.env) e preflight, usada por .github/workflows/backend-deploy.yml.
#
# Regra única de interpretação (subconjunto compatível com o dotenv do Docker Compose):
#   - linhas "CHAVE=valor" (opcional "export "); linhas vazias e "# comentário" são ignoradas;
#   - sem aspas: valor até o primeiro " #" (espaço + #), sem espaços nas pontas;
#   - 'aspas simples': valor literal; "aspas duplas": valor com \" e \\ interpretados;
#   - a última ocorrência de uma chave vence (como no Compose);
#   - "$" fora de aspas simples significaria interpolação no Compose: aqui é recusado (falha fechada),
#     em vez de os dois lados divergirem em silêncio.
# O conteúdo do arquivo é sempre DADO: nunca é passado a source/eval/bash -c. Nada é impresso além de
# nomes de chave e mensagens de erro — valores (podem ser sensíveis) não vão para o log.
set -euo pipefail

erro() { echo "deploy-config: $*" >&2; exit 1; }

# dotenv_get ARQUIVO CHAVE -> imprime o valor (vazio se ausente). Retorna 2 se o valor for inválido.
dotenv_get() {
  local file=$1 key=$2 line raw value found=0 result=""
  [[ -r "$file" ]] || erro "arquivo de configuração ilegível: $file"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    line=${line#"${line%%[![:space:]]*}"}
    [[ -z "$line" || "$line" == \#* ]] && continue
    line=${line#export[[:space:]]}
    line=${line#"${line%%[![:space:]]*}"}
    [[ "$line" =~ ^([A-Za-z_][A-Za-z0-9_.-]*)[[:space:]]*=(.*)$ ]] || continue
    [[ "${BASH_REMATCH[1]}" == "$key" ]] || continue
    raw=${BASH_REMATCH[2]}
    raw=${raw#"${raw%%[![:space:]]*}"}
    if [[ "$raw" == \'* ]]; then
      [[ "${raw:1}" == *\'* ]] || erro "valor de $key com aspas simples não fechadas"
      value=${raw:1}; value=${value%%\'*}
    elif [[ "$raw" == \"* ]]; then
      local rest=${raw:1} out="" ch closed=0
      while [[ -n "$rest" ]]; do
        ch=${rest:0:1}
        if [[ "$ch" == "\\" && ${#rest} -ge 2 && ( "${rest:1:1}" == '"' || "${rest:1:1}" == "\\" ) ]]; then
          out+=${rest:1:1}; rest=${rest:2}
        elif [[ "$ch" == '"' ]]; then closed=1; break
        else out+=$ch; rest=${rest:1}; fi
      done
      (( closed )) || erro "valor de $key com aspas duplas não fechadas"
      [[ "$out" == *'$'* ]] && erro "valor de $key contém '\$' (interpolação do Compose não é suportada aqui; use aspas simples)"
      value=$out
    else
      [[ "$raw" =~ ^(.*[^[:space:]])?[[:space:]]+\#.*$ ]] && raw=${BASH_REMATCH[1]:-}
      raw=${raw%"${raw##*[![:space:]]}"}
      [[ "$raw" == *'$'* ]] && erro "valor de $key contém '\$' (interpolação do Compose não é suportada aqui; use aspas simples)"
      value=$raw
    fi
    found=1; result=$value
  done < "$file"
  (( found )) || result=""
  printf '%s' "$result"
}

valida_host() {
  local h=$1
  [[ "$h" =~ ^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?$ && "$h" != *..* ]] || erro "TLS_PUBLIC_HOST inválido (use só o nome DNS, minúsculo, sem porta/esquema)"
}

# Identidades SQL da API: o Compose exige SQL_ADMIN_USER/SQL_ADMIN_PASSWORD (não existe fallback para "sa"). Falha
# aqui, antes de qualquer alteração de serviço e com uma mensagem direta, se faltarem, forem "sa", placeholder ou
# repetirem a senha do sa/a identidade de runtime. Nunca imprime valores.
valida_identidades_sql() {
  local env_file=$1 user pw sa_pw app
  user=$(dotenv_get "$env_file" SQL_ADMIN_USER); pw=$(dotenv_get "$env_file" SQL_ADMIN_PASSWORD)
  sa_pw=$(dotenv_get "$env_file" SQL_SA_PASSWORD); app=$(dotenv_get "$env_file" SQL_APP_USER); app=${app:-almirante_user_bd}
  [[ -n "$user" ]] || erro "SQL_ADMIN_USER ausente no .env: a API exige a identidade administrativa dedicada (não há fallback para sa)."
  [[ "${user,,}" != "sa" ]] || erro "SQL_ADMIN_USER não pode ser 'sa': a API nunca usa esse login."
  [[ "${user,,}" != "${app,,}" ]] || erro "SQL_ADMIN_USER não pode ser igual a SQL_APP_USER (identidades distintas)."
  [[ -n "$pw" ]] || erro "SQL_ADMIN_PASSWORD ausente no .env."
  [[ "$pw" != DEFINA_* ]] || erro "SQL_ADMIN_PASSWORD ainda é o placeholder do .env.example."
  [[ "$pw" != "$sa_pw" ]] || erro "SQL_ADMIN_PASSWORD não pode ser igual a SQL_SA_PASSWORD."
}

cmd_get() {
  [[ $# -eq 2 ]] || erro "uso: get ARQUIVO CHAVE"
  dotenv_get "$1" "$2"
  echo
}

# gerar-host-tls SAIDA HOST: escreve o mapa de host aceito pelo nginx.tls.conf.
cmd_gerar_host_tls() {
  [[ $# -eq 2 ]] || erro "uso: gerar-host-tls SAIDA HOST"
  local out=$1 host=$2
  valida_host "$host"
  mkdir -p "$(dirname "$out")"
  {
    echo "# Gerado por scripts/deploy-config.sh a partir de TLS_PUBLIC_HOST. Não edite nem versione."
    printf 'map $host $almirante_host_ok { default 0; "%s" 1; }\n' "$host"
    printf 'map "" $almirante_canonical_host { default "%s"; }\n' "$host"
  } > "$out"
}

# modo ARQUIVO_ENV [SAIDA_GITHUB]: valida o modo de deploy ANTES de qualquer alteração de serviço e
# emite mode/files (e public_host) no formato de GITHUB_OUTPUT.
cmd_modo() {
  [[ $# -ge 1 ]] || erro "uso: modo ARQUIVO_ENV [SAIDA]"
  local env_file=$1 out=${2:-/dev/stdout}
  local mode nginx_conf host cert key port
  valida_identidades_sql "$env_file"
  mode=$(dotenv_get "$env_file" DEPLOY_MODE); mode=${mode:-http}
  nginx_conf=$(dotenv_get "$env_file" NGINX_CONF_FILE)
  port=$(dotenv_get "$env_file" API_HOST_PORT); port=${port:-8090}
  [[ "$port" =~ ^[0-9]{1,5}$ && "$port" -ge 1 && "$port" -le 65535 ]] || erro "API_HOST_PORT inválida"

  local files
  case "$mode" in
    http)
      [[ -z "$nginx_conf" || "$nginx_conf" == "nginx.conf" ]] || erro "DEPLOY_MODE=http mas NGINX_CONF_FILE não é nginx.conf (nem vazio)."
      files="-f compose.yaml"
      host=""
      # Aviso (não falha): em modo http o nginx repassa o Host do cliente e, com AllowedHosts="*",
      # a API aceita qualquer nome. Não é bloqueante porque este mesmo compose serve desenvolvimento
      # local, onde fixar a lista atrapalharia; mas num host publicado é config que falta.
      local allowed
      allowed=$(dotenv_get "$env_file" API_ALLOWED_HOSTS)
      if [[ -z "$allowed" || "$allowed" == "*" ]]; then
        echo "deploy-config: AVISO — API_ALLOWED_HOSTS não está definido (ou é '*'): a API aceitará qualquer Host header." >&2
        echo "deploy-config:          Num ambiente publicado, defina os nomes reais no .env (ex.: API_ALLOWED_HOSTS=api.exemplo.com.br;localhost;127.0.0.1)." >&2
      fi
      ;;
    tls)
      [[ "$nginx_conf" == "nginx.tls.conf" ]] || erro "DEPLOY_MODE=tls exige NGINX_CONF_FILE=nginx.tls.conf."
      host=$(dotenv_get "$env_file" TLS_PUBLIC_HOST)
      [[ -n "$host" ]] || erro "DEPLOY_MODE=tls exige TLS_PUBLIC_HOST (nome DNS do certificado)."
      valida_host "$host"
      cert=$(dotenv_get "$env_file" TLS_CERT_PATH); key=$(dotenv_get "$env_file" TLS_KEY_PATH)
      [[ -n "$cert" && -f "$cert" && -r "$cert" ]] || erro "TLS_CERT_PATH ausente ou ilegível (o certificado deve existir no host antes do deploy)."
      [[ -n "$key" && -f "$key" && -r "$key" ]] || erro "TLS_KEY_PATH ausente ou ilegível (a chave deve existir no host antes do deploy)."
      files="-f compose.yaml -f compose.tls.yaml"
      ;;
    *) erro "DEPLOY_MODE inválido (use 'http' ou 'tls')." ;;
  esac
  {
    echo "mode=$mode"
    echo "files=$files"
    echo "public_host=$host"
    echo "port=$port"
  } >> "$out"
}

main() {
  local sub=${1:-}; shift || true
  case "$sub" in
    get) cmd_get "$@" ;;
    gerar-host-tls) cmd_gerar_host_tls "$@" ;;
    modo) cmd_modo "$@" ;;
    *) erro "subcomando inválido (get | gerar-host-tls | modo)" ;;
  esac
}

main "$@"
