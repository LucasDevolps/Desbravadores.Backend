# Login local pelo nginx

Execute na raiz do repositório em Bash/WSL, com Docker Compose, OpenSSL, cURL e Python 3. A autenticação usa HTTPS, cookie antiforgery e header `X-CSRF-TOKEN`. O override `compose.https.yaml` habilita o listener TLS; o Compose base continua sem depender de certificados locais. O nginx encaminha o protocolo real para a API pela rede confiável; a porta interna da API não é publicada.

## Preparar o ambiente

Configure `.env` conforme o README. `API_HOST_PORT` é HTTP; `API_HTTPS_HOST_PORT` é HTTPS. Por exemplo, use `8091` e `8444` quando as portas padrão estiverem ocupadas. `API_TRUSTED_PROXY_CIDR` deve corresponder à subnet do Compose (`172.30.0.0/24`).

Se os dois arquivos de certificado ainda não existirem, gere-os uma única vez:

```bash
mkdir -p nginx/certs
openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -noenc \
  -keyout nginx/certs/localhost.key -out nginx/certs/localhost.pem \
  -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
chmod 600 nginx/certs/localhost.key
```

Os certificados e a chave privada são ignorados pelo Git. O cURL abaixo confia explicitamente nesse certificado com `--cacert`; mantenha a validação TLS habilitada.

```bash
docker compose -f compose.yaml -f compose.https.yaml up -d --build
```

Se apenas portas, mounts ou healthcheck do nginx mudaram, recrie esse serviço: `docker compose -f compose.yaml -f compose.https.yaml up -d --no-deps nginx`. Um simples reload do nginx não publica novas portas Docker.

Em volumes de Data Protection criados anteriormente como `root`, corrija uma vez o proprietário do diretório existente (sem apagar as chaves):

```bash
docker compose exec --user root api chown -R app:app /var/lib/almirante/dataprotection
docker compose exec --user root api chmod 700 /var/lib/almirante/dataprotection
docker compose exec api sh -c 'test -w /var/lib/almirante/dataprotection'
```

A API continua executando como `app`. O Dockerfile prepara esse diretório para volumes novos.

## Obter e usar o token

Execute os comandos na mesma sessão Bash. Ajuste `BASE_URL` para a porta HTTPS do seu `.env`. A senha é solicitada sem aparecer na tela ou no histórico.

```bash
BASE_URL=https://localhost:8443
CERT=nginx/certs/localhost.pem
umask 077
SESSION_DIR=$(mktemp -d)

# 1. Obter CSRF e guardar o cookie associado.
curl --fail-with-body --silent --show-error --cacert "$CERT" \
  --cookie-jar "$SESSION_DIR/cookies.txt" \
  "$BASE_URL/api/Auth/csrf" > "$SESSION_DIR/csrf.json"
CSRF=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["csrfToken"])' \
  < "$SESSION_DIR/csrf.json")

# 2. Enviar a senha com o mesmo cookie e o header CSRF.
read -r -s -p 'Senha de admin@local.dev: ' LOGIN_PASSWORD
printf '\n'
printf '%s' "$LOGIN_PASSWORD" |
  python3 -c 'import json,sys; json.dump({"email":"admin@local.dev","senha":sys.stdin.read()},sys.stdout)' |
  curl --fail-with-body --silent --show-error --cacert "$CERT" \
    --cookie "$SESSION_DIR/cookies.txt" --cookie-jar "$SESSION_DIR/cookies.txt" \
    --header "X-CSRF-TOKEN: $CSRF" --header 'Content-Type: application/json' \
    --data-binary @- "$BASE_URL/api/Auth/login" > "$SESSION_DIR/login.json"
unset LOGIN_PASSWORD
TOKEN=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["token"]["accessToken"])' \
  < "$SESSION_DIR/login.json")

# 3. Validar o token no endpoint protegido.
printf 'Authorization: Bearer %s\n' "$TOKEN" > "$SESSION_DIR/bearer.headers"
curl --fail-with-body --silent --show-error --cacert "$CERT" \
  --header "@$SESSION_DIR/bearer.headers" "$BASE_URL/api/Auth/Me"

# Token para usar no Postman (Bearer Token):
printf '\n%s\n' "$TOKEN"
```

O token está em `token.accessToken` e sua expiração em `token.expiresAtUtc`. Os arquivos em `$SESSION_DIR` contêm a sessão e devem permanecer privados; remova-os ao terminar com `rm -rf -- "$SESSION_DIR"` e `unset TOKEN CSRF`.

Para refresh/logout, envie o cookie guardado e o CSRF. Se também enviar Bearer, obtenha primeiro um novo CSRF com esse Bearer, conforme [o fluxo de autenticação](authentication-security.md).

## Diagnóstico

- `500` com erro de `Cookie.SecurePolicy = Always`: a API recebeu HTTP. Use a porta HTTPS e confira o CIDR do proxy confiável; não force `X-Forwarded-Proto` no cliente.
- `500` com acesso negado a Data Protection: confira o proprietário do volume conforme acima.
- `400`: confira o par cookie/header CSRF; copiar apenas o header não basta.
- `401`: credenciais inválidas, token inválido/expirado ou sessão revogada. Alterar a senha do seed no `.env` não muda a senha de um usuário já existente.
- `429`: aguarde o `Retry-After`; o limite do nginx permanece ativo.
