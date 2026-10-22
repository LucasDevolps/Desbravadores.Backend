#!/usr/bin/env bash
# Valida nginx.tls.conf/nginx.conf/nginx.windows.conf com containers Docker descartáveis e nomes únicos.
# Requer docker + openssl + curl. Uso: bash scripts/tests/nginx.test.sh
set -u
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
W=$(mktemp -d)
P=almirante-nginx-test-$(basename "$W" | tr '[:upper:].' '[:lower:]-')
mkdir -p "$W/certs" "$W/gen" "$W/stub"
cleanup() { docker rm -f ${P}-api ${P}-tls ${P}-plain >/dev/null 2>&1 || true; docker network rm ${P}-net >/dev/null 2>&1 || true; rm -rf "$W"; }
trap cleanup EXIT
docker network create ${P}-net >/dev/null

openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=api.exemplo.test" -addext "subjectAltName=DNS:api.exemplo.test" \
  -keyout "$W/certs/privkey.pem" -out "$W/certs/fullchain.pem" >/dev/null 2>&1
bash "$REPO/scripts/deploy-config.sh" gerar-host-tls "$W/gen/tls-host.conf" api.exemplo.test

cat > "$W/stub/default.conf" <<'EOF'
server {
  listen 8080;
  client_max_body_size 20m;
  location = /api/Auth/login   { default_type application/json; return 401 '{"s":401}'; }
  location = /api/Auth/refresh { default_type application/json; return 401 '{"s":401}'; }
  location ~* ^/api/(usuarios/[^/]+|me)/foto$ { default_type application/json; return 200 '{"foto":"recebida"}'; }
  location = /health { return 200 "ok"; }
  location ~* ^/hubs/notificacoes {
    add_header X-Seen-Upgrade "$http_upgrade" always; add_header X-Seen-Connection "$http_connection" always;
    add_header X-Seen-Host $host always; add_header X-Seen-Proto "$http_x_forwarded_proto" always; return 200 "hub";
  }
  location / { add_header X-Seen-Host $host always; return 404; }
}
EOF
# Corpos para o limite da foto (issue #116): 10,5 MB passa (11m na rota da foto), 12 MB não; 2 MB em outra rota não
# passa (padrão de 1 MB do nginx continua valendo).
head -c 10500000 /dev/zero > "$W/foto-ok.bin"
head -c 12000000 /dev/zero > "$W/foto-grande.bin"
head -c 2000000 /dev/zero > "$W/outro-2m.bin"
FOTO=/api/Usuarios/11111111-1111-4111-8111-111111111111/foto
FOTO_PROPRIA=/api/Me/foto
docker run -d --name ${P}-api --network ${P}-net --network-alias api -v "$W/stub/default.conf:/etc/nginx/conf.d/default.conf:ro" nginx:1.27-alpine >/dev/null

docker run -d --name ${P}-tls --network ${P}-net -p 127.0.0.1::443 -p 127.0.0.1::80 \
  -v "$REPO/nginx/nginx.tls.conf:/etc/nginx/conf.d/default.conf:ro" \
  -v "$W/gen/tls-host.conf:/etc/nginx/almirante-host/host.conf:ro" \
  -v "$W/certs/fullchain.pem:/etc/nginx/tls-certs/fullchain.pem:ro" \
  -v "$W/certs/privkey.pem:/etc/nginx/tls-certs/privkey.pem:ro" nginx:1.27-alpine >/dev/null
sleep 3
HTTPS_PORT=$(docker port ${P}-tls 443 | head -1 | sed "s/.*://")
HTTP_PORT=$(docker port ${P}-tls 80 | head -1 | sed "s/.*://")
docker logs ${P}-tls 2>&1 | grep -iE "emerg|error" | head

fail=0
check() { if [ "$2" = "$3" ]; then echo "ok   - $1"; else echo "FAIL - $1 :: esperado [$3] obtido [$2]"; fail=1; fi; }
H() { curl -s -o /dev/null -D - "$@"; }
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
R="--resolve api.exemplo.test:$HTTPS_PORT:127.0.0.1 --cacert $W/certs/fullchain.pem"

# Hub SignalR (issue #130): upgrade WebSocket repassado, headers de proxy repetidos no location do hub e o access_token
# da query fora dos logs do nginx. Uso: hub_checks <rótulo> <container> <proto esperado> <url base> [opções do curl].
hub_checks() {
  local rotulo=$1 container=$2 proto=$3 base=$4 hdr
  shift 4
  hdr=$(curl -s -o /dev/null -D - "$@" -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    "$base/hubs/notificacoes?id=conexao-teste&access_token=SEGREDO-HUB-NAO-LOGAR" | tr -d '\r')
  echo "$hdr" | grep -qi '^x-seen-upgrade: websocket' && echo "ok   - $rotulo hub repassa Upgrade" || { echo "FAIL - $rotulo hub sem Upgrade"; fail=1; }
  echo "$hdr" | grep -qi '^x-seen-connection: upgrade' && echo "ok   - $rotulo hub repassa Connection: upgrade" || { echo "FAIL - $rotulo hub sem Connection: upgrade"; fail=1; }
  echo "$hdr" | grep -qi "^x-seen-proto: $proto" && echo "ok   - $rotulo hub mantém X-Forwarded-Proto" || { echo "FAIL - $rotulo hub sem X-Forwarded-Proto"; fail=1; }
  hdr=$(curl -s -o /dev/null -D - "$@" -X POST "$base/hubs/notificacoes/negotiate?negotiateVersion=1" | tr -d '\r')
  if echo "$hdr" | grep -qi '^x-seen-connection: .'; then echo "FAIL - $rotulo negotiate recebeu Connection"; fail=1; else echo "ok   - $rotulo negotiate sem Connection (keepalive)"; fi
  sleep 1
  check "$rotulo token do hub fora dos logs" "$(docker logs "$container" 2>&1 | grep -c 'SEGREDO-HUB-NAO-LOGAR')" 0
  check "$rotulo access log do hub grava só o caminho" \
    "$(docker logs "$container" 2>&1 | grep -q '"GET /hubs/notificacoes HTTP/1.1" 200' && echo sim || echo nao)" sim
}

echo "== nginx.tls.conf"
check "HTTP host permitido -> 308" "$(code -H 'Host: api.exemplo.test' "http://127.0.0.1:$HTTP_PORT/x?q=1")" 308
loc=$(H -H 'Host: api.exemplo.test' "http://127.0.0.1:$HTTP_PORT/a/b?q=abc&z=1" | grep -i '^location:' | tr -d '\r')
check "redirect canônico preserva caminho e query" "$loc" "Location: https://api.exemplo.test/a/b?q=abc&z=1"
check "HTTP host malicioso -> 421" "$(code -H 'Host: attacker.example' "http://127.0.0.1:$HTTP_PORT/x")" 421
loc2=$(H -H 'Host: attacker.example' "http://127.0.0.1:$HTTP_PORT/x" | grep -ci '^location:.*attacker')
check "host malicioso não é refletido" "$loc2" 0
check "/internal-health de fora (loopback do host, via bridge != 127.0.0.1)" "$(code "http://127.0.0.1:$HTTP_PORT/internal-health")" 403
check "/internal-health dentro do container (127.0.0.1)" "$(docker exec ${P}-tls wget -q -O /dev/null --spider http://127.0.0.1/internal-health && echo 200 || echo fail)" 200
check "HTTPS host permitido -> API 404 (stub)" "$(code $R https://api.exemplo.test:$HTTPS_PORT/qualquer)" 404
check "HTTPS host malicioso -> 421" "$(code $R -H 'Host: attacker.example' https://api.exemplo.test:$HTTPS_PORT/qualquer)" 421
check "HTTPS /internal-health -> 404" "$(code $R https://api.exemplo.test:$HTTPS_PORT/internal-health)" 404
seen=$(curl -s -o /dev/null -D - $R https://api.exemplo.test:$HTTPS_PORT/qualquer | grep -i '^x-seen-host' | tr -d '\r')
check "API recebe Host canônico" "$seen" "X-Seen-Host: api.exemplo.test"
check "HTTPS login 401 passa pela API" "$(code $R -X POST https://api.exemplo.test:$HTTPS_PORT/api/Auth/login)" 401

# Política TLS declarada em nginx.tls.conf (ssl_protocols TLSv1.2 TLSv1.3): não basta "443 aberta".
# Protocolos obsoletos precisam falhar o handshake, e não só "provavelmente estar desligados por
# default da imagem". O openssl do sistema pode ter sido compilado sem TLS 1.0/1.1 — nesse caso a
# própria chamada falha, o que também satisfaz a expectativa (handshake não estabelecido).
tls_handshake() {
  openssl s_client -connect "127.0.0.1:$HTTPS_PORT" -servername api.exemplo.test "$1" \
    </dev/null >/dev/null 2>&1 && echo aceito || echo recusado
}
for proto in -tls1 -tls1_1; do
  check "handshake ${proto#-} recusado" "$(tls_handshake $proto)" recusado
done
for proto in -tls1_2 -tls1_3; do
  check "handshake ${proto#-} aceito" "$(tls_handshake $proto)" aceito
done
check "server_tokens off (sem versão no header Server)" \
  "$(H $R https://api.exemplo.test:$HTTPS_PORT/qualquer | grep -ci '^server: nginx/[0-9]')" 0

# Limite de corpo: só a rota da foto aceita até 11m; o 413 do nginx sai como problem+json com os cabeçalhos de segurança.
check "foto de 10,5 MB chega à API" "$(code $R -X PUT --data-binary @"$W/foto-ok.bin" https://api.exemplo.test:$HTTPS_PORT$FOTO)" 200
check "rota da foto sem diferenciar caixa" "$(code $R -X PUT --data-binary @"$W/foto-ok.bin" https://api.exemplo.test:$HTTPS_PORT/api/usuarios/11111111-1111-4111-8111-111111111111/FOTO)" 200
out=$(curl -s -o "$W/body" -D "$W/hdr" -w '%{http_code}' $R -X PUT --data-binary @"$W/foto-grande.bin" https://api.exemplo.test:$HTTPS_PORT$FOTO)
check "foto de 12 MB -> 413 no nginx" "$out" 413
for h in 'content-type: application/problem+json' 'x-content-type-options: nosniff' 'x-frame-options: deny' 'cache-control: no-store' 'strict-transport-security: max-age=31536000' "content-security-policy: default-src 'none'"; do
  grep -qi "^$h" "$W/hdr" && echo "ok   - foto 413 tem $h" || { echo "FAIL - foto 413 sem $h"; fail=1; }
done
grep -q '"status":413' "$W/body" && echo "ok   - foto 413 corpo problem+json" || { echo "FAIL - foto 413 corpo"; fail=1; }
check "2 MB fora da rota da foto -> 413 (padrão 1 MB)" "$(code $R -X POST --data-binary @"$W/outro-2m.bin" https://api.exemplo.test:$HTTPS_PORT/api/Usuarios)" 413
check "própria foto (/api/Me/foto) de 10,5 MB chega à API" "$(code $R -X PUT --data-binary @"$W/foto-ok.bin" https://api.exemplo.test:$HTTPS_PORT$FOTO_PROPRIA)" 200
check "própria foto de 12 MB -> 413 no nginx" "$(code $R -X PUT --data-binary @"$W/foto-grande.bin" https://api.exemplo.test:$HTTPS_PORT$FOTO_PROPRIA)" 413
check "2 MB em /api/Me (sem foto) -> 413 (padrão 1 MB)" "$(code $R -X PUT --data-binary @"$W/outro-2m.bin" https://api.exemplo.test:$HTTPS_PORT/api/Me)" 413

hub_checks "tls" ${P}-tls https "https://api.exemplo.test:$HTTPS_PORT" $R
seen=$(curl -s -o /dev/null -D - $R https://api.exemplo.test:$HTTPS_PORT/hubs/notificacoes | grep -i '^x-seen-host' | tr -d '\r')
check "hub recebe Host canônico" "$seen" "X-Seen-Host: api.exemplo.test"

for path in login refresh; do
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    out=$(curl -s -o "$W/body" -D "$W/hdr" -w '%{http_code}' $R -X POST https://api.exemplo.test:$HTTPS_PORT/api/Auth/$path)
    [ "$out" = "429" ] && break
  done
  check "$path chega ao 429" "$out" 429
  for h in 'retry-after: 60' 'content-type: application/problem+json' 'x-content-type-options: nosniff' 'x-frame-options: deny' 'cache-control: no-store' 'strict-transport-security: max-age=31536000' "content-security-policy: default-src 'none'"; do
    grep -qi "^$h" "$W/hdr" && echo "ok   - $path 429 tem $h" || { echo "FAIL - $path 429 sem $h"; fail=1; }
  done
  grep -q '"status":429' "$W/body" && echo "ok   - $path 429 corpo problem+json" || { echo "FAIL - $path corpo"; fail=1; }
done

echo "== nginx.conf / nginx.windows.conf (sintaxe + 429 do refresh)"
sed 's/__IIS_PORT__/8080/; s/127.0.0.1:8090/80/; s#logs/access.log#/var/log/nginx/access.log#; s#logs/error.log#/var/log/nginx/error.log#' "$REPO/nginx/nginx.windows.conf" | sed 's/server 127.0.0.1:8080/server api:8080/' > "$W/win.conf"
for conf in "$REPO/nginx/nginx.conf" "$W/win.conf"; do
  docker rm -f ${P}-plain >/dev/null 2>&1
  docker run -d --name ${P}-plain --network ${P}-net -p 127.0.0.1::80 -v "$conf:/etc/nginx/conf.d/default.conf:ro" nginx:1.27-alpine >/dev/null
  sleep 2
  PLAIN_PORT=$(docker port ${P}-plain 80 | head -1 | sed "s/.*://")
  docker logs ${P}-plain 2>&1 | tail -3
  for i in $(seq 1 15); do out=$(curl -s -o "$W/body" -D "$W/hdr" -w '%{http_code}' -X POST http://127.0.0.1:$PLAIN_PORT/api/Auth/refresh); [ "$out" = "429" ] && break; done
  check "$(basename $conf) refresh 429" "$out" 429
  for h in 'retry-after: 60' 'content-type: application/problem+json' 'x-content-type-options: nosniff' 'x-frame-options: deny' 'cache-control: no-store'; do
    grep -qi "^$h" "$W/hdr" && echo "ok   - $(basename $conf) refresh 429 tem $h" || { echo "FAIL - $(basename $conf) refresh sem $h"; fail=1; }
  done
  check "$(basename $conf) foto de 10,5 MB chega à API" "$(code -X PUT --data-binary @"$W/foto-ok.bin" http://127.0.0.1:$PLAIN_PORT$FOTO)" 200
  out=$(curl -s -o "$W/body" -D "$W/hdr" -w '%{http_code}' -X PUT --data-binary @"$W/foto-grande.bin" http://127.0.0.1:$PLAIN_PORT$FOTO)
  check "$(basename $conf) foto de 12 MB -> 413" "$out" 413
  grep -qi '^content-type: application/problem+json' "$W/hdr" && grep -q '"status":413' "$W/body" \
    && echo "ok   - $(basename $conf) foto 413 problem+json" || { echo "FAIL - $(basename $conf) foto 413 corpo"; fail=1; }
  check "$(basename $conf) 2 MB fora da rota da foto -> 413" "$(code -X POST --data-binary @"$W/outro-2m.bin" http://127.0.0.1:$PLAIN_PORT/api/Usuarios)" 413
  check "$(basename $conf) própria foto de 10,5 MB chega à API" "$(code -X PUT --data-binary @"$W/foto-ok.bin" http://127.0.0.1:$PLAIN_PORT$FOTO_PROPRIA)" 200
  check "$(basename $conf) própria foto de 12 MB -> 413" "$(code -X PUT --data-binary @"$W/foto-grande.bin" http://127.0.0.1:$PLAIN_PORT$FOTO_PROPRIA)" 413
  hub_checks "$(basename $conf)" ${P}-plain http "http://127.0.0.1:$PLAIN_PORT"
done

echo "RESULT fail=$fail"
exit "$fail"
