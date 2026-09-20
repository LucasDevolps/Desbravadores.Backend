#!/usr/bin/env bash
# Valida nginx.tls.conf/nginx.conf/nginx.windows.conf com containers Docker descartáveis (prefixo pr59fix-).
# Requer docker + openssl + curl. Uso: bash scripts/tests/nginx.test.sh
set -u
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
W=$(mktemp -d)
P=almirante-nginx-test
mkdir -p "$W/certs" "$W/gen" "$W/stub"
cleanup() { docker rm -f ${P}-api ${P}-tls ${P}-plain >/dev/null 2>&1; docker network rm ${P}-net >/dev/null 2>&1; }
cleanup
docker network create ${P}-net >/dev/null

openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=api.exemplo.test" -addext "subjectAltName=DNS:api.exemplo.test" \
  -keyout "$W/certs/privkey.pem" -out "$W/certs/fullchain.pem" >/dev/null 2>&1
bash "$REPO/scripts/deploy-config.sh" gerar-host-tls "$W/gen/tls-host.conf" api.exemplo.test

cat > "$W/stub/default.conf" <<'EOF'
server {
  listen 8080;
  location = /api/Auth/login   { default_type application/json; return 401 '{"s":401}'; }
  location = /api/Auth/refresh { default_type application/json; return 401 '{"s":401}'; }
  location = /health { return 200 "ok"; }
  location / { add_header X-Seen-Host $host always; return 404; }
}
EOF
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
sed 's/__IIS_PORT__/8080/; s/127.0.0.1:8090/80/' "$REPO/nginx/nginx.windows.conf" | sed 's/server 127.0.0.1:8080/server api:8080/' > "$W/win.conf"
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
done

cleanup
rm -rf "$W"
echo "RESULT fail=$fail"
