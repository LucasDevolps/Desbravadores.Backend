#!/usr/bin/env bash
# Testes de scripts/sql-bootstrap.sh SEM SQL Server: um "sqlcmd" falso registra como seria chamado.
# Provam que o bootstrap (a) recusa entradas perigosas ANTES de falar com o servidor, (b) nunca põe a senha do sa
# nem a administrativa na linha de comando e (c) chama o sqlcmd com o script e as variáveis não secretas.
# Uso: bash scripts/tests/sql-bootstrap.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SCRIPT="$ROOT/scripts/sql-bootstrap.sh"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
falhas=0

ok() { echo "ok   - $1"; }
falha() { falhas=$((falhas + 1)); echo "FAIL - $1 :: $2"; }

CALLS="$TMP/calls"
cat > "$TMP/sqlcmd" <<'EOS'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CALLS"
case " $* " in *" -i "*) exit "${FAKE_SCRIPT_EXIT:-0}";; esac
exit 0
EOS
chmod +x "$TMP/sqlcmd"
: > "$TMP/x.sql"

SA_PW='Sa-Senha-Do-Servidor-9x7q2Zk!'
ADMIN_PW='Admin.Senha.Dedicada.2026-Zz9'

# roda [VAR=valor ...] -> executa o bootstrap com o ambiente-base + sobrescritas; guarda saída em $TMP/out
roda() {
  : > "$CALLS"
  env -i PATH="$PATH" CALLS="$CALLS" SQLCMD="$TMP/sqlcmd" SQL_BOOTSTRAP_ATTEMPTS=1 SQL_BOOTSTRAP_SCRIPT="$TMP/x.sql" \
    SQLCMDPASSWORD="$SA_PW" DB_NAME=almirante ADMIN_USER=almirante_admin_bd APP_USER=almirante_user_bd APP_ADMIN_PASSWORD="$ADMIN_PW" \
    "$@" bash "$SCRIPT" > "$TMP/out" 2>&1
}

recusa() { # DESCRICAO [VAR=valor ...]
  local desc=$1; shift
  if roda "$@"; then falha "$desc" "deveria falhar"; return; fi
  if [[ -s "$CALLS" ]]; then falha "$desc" "falou com o servidor antes de recusar: $(cat "$CALLS")"; return; fi
  if grep -qF "$SA_PW" "$TMP/out" || grep -qF "$ADMIN_PW" "$TMP/out"; then falha "$desc" "a saída vazou uma senha"; return; fi
  ok "$desc"
}

recusa "recusa ADMIN_USER=sa" ADMIN_USER=sa
recusa "recusa ADMIN_USER=SA (maiúsculo)" ADMIN_USER=SA
recusa "recusa APP_USER=sa" APP_USER=sa
recusa "recusa identidades iguais (sem diferenciar caixa)" APP_USER=Almirante_Admin_BD
recusa "recusa identificador com aspa/ponto e vírgula" "ADMIN_USER=x'; DROP LOGIN sa;--"
recusa "recusa nome de banco inválido" "DB_NAME=alm-irante"
recusa "recusa identificador começando por dígito" "APP_USER=1abc"
recusa "recusa senha administrativa curta" APP_ADMIN_PASSWORD=Curta.1
recusa "recusa senha administrativa com aspa simples" "APP_ADMIN_PASSWORD=Senha'Com'Aspas-1234567890"
recusa "recusa senha administrativa com cifrão (interpolação)" 'APP_ADMIN_PASSWORD=Senha$Com$Cifrao-1234567890'
recusa "recusa senha administrativa com espaço" 'APP_ADMIN_PASSWORD=Senha com espaco 1234567890'
recusa "recusa o placeholder do .env.example" APP_ADMIN_PASSWORD=DEFINA_UMA_SENHA_FORTE_FORA_DO_GIT
recusa "recusa senha administrativa igual à do sa" "APP_ADMIN_PASSWORD=$SA_PW" "SQLCMDPASSWORD=$SA_PW"
for var in SQLCMDPASSWORD DB_NAME ADMIN_USER APP_USER APP_ADMIN_PASSWORD; do
  recusa "recusa variável ausente ($var)" "$var="
done

# Caminho feliz: chama o sqlcmd 2x (espera + script), com o script e variáveis não secretas — e NENHUMA senha em argv.
if roda; then
  chamadas=$(cat "$CALLS")
  if [[ $(wc -l < "$CALLS") -eq 2 ]]; then ok "caminho feliz: espera o servidor e aplica o script"; else falha "caminho feliz" "esperava 2 chamadas: $chamadas"; fi
  if grep -qF -- "-i $TMP/x.sql" "$CALLS" && grep -qF -- "DB_NAME=almirante" "$CALLS" && grep -qF -- "ADMIN_USER=almirante_admin_bd" "$CALLS" \
     && grep -qF -- "APP_USER=almirante_user_bd" "$CALLS"; then ok "passa script e variáveis não secretas ao sqlcmd"; else falha "argumentos" "$chamadas"; fi
  if grep -qF -- "-U sa" "$CALLS"; then ok "conecta como sa só dentro do bootstrap"; else falha "usuário do bootstrap" "$chamadas"; fi
  if grep -qF "$SA_PW" "$CALLS" || grep -qF "$ADMIN_PW" "$CALLS" || grep -q -- " -P " "$CALLS"; then
    falha "senha na linha de comando" "$chamadas"
  else ok "nenhuma senha (sa ou administrativa) na linha de comando"; fi
  if grep -qF "$SA_PW" "$TMP/out" || grep -qF "$ADMIN_PW" "$TMP/out"; then falha "saída" "vazou senha"; else ok "a saída não contém senha"; fi
else
  falha "caminho feliz" "falhou: $(cat "$TMP/out")"
fi

# Falha do sqlcmd (ex.: conferência final do script) derruba o bootstrap — e o "up" do Compose.
if roda FAKE_SCRIPT_EXIT=1; then falha "propaga falha do script" "deveria falhar"; else ok "propaga falha do script SQL (a API não sobe sem bootstrap)"; fi

echo
echo "$falhas falha(s)."
((falhas == 0))
