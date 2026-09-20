#!/usr/bin/env bash
# Bootstrap SQL da aplicação, executado pelo serviço "sql-bootstrap" do Compose (uma vez por "up", depois sai) —
# NUNCA pela API. É o único lugar, além do próprio serviço "sqlserver", que conhece o segredo do "sa":
#   - cria/atualiza o banco da aplicação e as duas identidades da API (administrativa dedicada e de runtime),
#     sem privilégio de servidor, com docs/sql/criar-usuario-admin-app.sql;
#   - é idempotente: reexecutar redefine só a senha administrativa (rotação operacional) e reaplica as permissões;
#   - não põe segredo em linha de comando: o "sa" vai por SQLCMDPASSWORD e a senha administrativa por
#     APP_ADMIN_PASSWORD (variáveis de ambiente lidas pelo sqlcmd) — nada disso aparece em "ps"/argv;
#   - só imprime o que o sqlcmd imprime (conferências sem segredo); nunca ecoa variável de ambiente.
# Depois dele, todo SQL da API usa as identidades dedicadas; o "sa" não existe para a API.
set -euo pipefail

erro() { echo "sql-bootstrap: $*" >&2; exit 1; }

SQLCMD=${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}
SCRIPT=${SQL_BOOTSTRAP_SCRIPT:-/bootstrap/criar-usuario-admin-app.sql}
HOST=${SQL_BOOTSTRAP_HOST:-sqlserver}
ATTEMPTS=${SQL_BOOTSTRAP_ATTEMPTS:-60}

for var in SQLCMDPASSWORD DB_NAME ADMIN_USER APP_USER APP_ADMIN_PASSWORD; do
  [[ -n "${!var:-}" ]] || erro "variável $var não definida."
done
[[ -r "$SCRIPT" ]] || erro "script SQL ilegível: $SCRIPT"

# Os valores entram no script como TEXTO ($(VAR) do sqlcmd): valida o alfabeto ANTES, para que nenhum deles
# possa fechar um literal SQL. Falha fechada — nada é enviado ao servidor se algo estiver fora do padrão.
ident='^[A-Za-z_][A-Za-z0-9_]{0,62}$'
for var in DB_NAME ADMIN_USER APP_USER; do
  [[ "${!var}" =~ $ident ]] || erro "$var deve conter só letras, dígitos e '_' (até 63, sem começar por dígito)."
done
[[ "${ADMIN_USER,,}" != "sa" && "${APP_USER,,}" != "sa" ]] || erro "ADMIN_USER/APP_USER não podem ser 'sa': a API nunca usa esse login."
[[ "${ADMIN_USER,,}" != "${APP_USER,,}" ]] || erro "ADMIN_USER e APP_USER precisam ser identidades diferentes."
[[ "$APP_ADMIN_PASSWORD" != DEFINA_* ]] || erro "SQL_ADMIN_PASSWORD ainda é o placeholder do .env.example: defina uma senha própria."
[[ "$APP_ADMIN_PASSWORD" =~ ^[A-Za-z0-9_.~+@%^*!#:-]{16,128}$ ]] \
  || erro "SQL_ADMIN_PASSWORD deve ter 16 a 128 caracteres de A-Za-z0-9 e _.~+@%^*!#:- (sem aspas, espaço, \$, ; ou =)."
[[ "$APP_ADMIN_PASSWORD" != "$SQLCMDPASSWORD" ]] || erro "SQL_ADMIN_PASSWORD não pode ser igual à senha do sa (credenciais distintas)."

# O serviço "sqlserver" já só fica saudável com o SQL Server aceitando logins, mas o bootstrap confere com o
# próprio "sa" (que ele tem por definição) antes de rodar o script.
for ((i = 1; i <= ATTEMPTS; i++)); do
  if "$SQLCMD" -S "$HOST" -U sa -C -b -l 5 -Q "SELECT 1" > /dev/null 2>&1; then break; fi
  ((i < ATTEMPTS)) || erro "SQL Server em $HOST não aceitou conexão a tempo."
  sleep 2
done

# -C: o SQL Server do Compose usa certificado autoassinado e a conexão é interna à rede do Compose.
# -b: qualquer erro do script (inclusive a conferência final) encerra com status diferente de 0.
"$SQLCMD" -S "$HOST" -U sa -C -b \
  -v DB_NAME="$DB_NAME" ADMIN_USER="$ADMIN_USER" APP_USER="$APP_USER" \
  -i "$SCRIPT" \
  || erro "falhou ao aplicar o bootstrap (nenhuma senha é impressa)."
echo "sql-bootstrap: banco '$DB_NAME' e identidades da aplicação prontos."
