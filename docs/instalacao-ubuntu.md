# Instalar a API no Ubuntu

Passo a passo para baixar e rodar a API Almirante numa máquina Ubuntu (22.04 ou mais novo) ou Pop!_OS,
com Docker Compose. Sobem quatro serviços: SQL Server, o provisionamento das identidades SQL
(`sql-bootstrap`, roda uma vez e sai), a API e o Nginx, que é a única porta de entrada.

Se a máquina já é o runner de deploy automático (`deploy-linux`), não é preciso fazer nada disto: cada
push em `develop` atualiza tudo sozinho, inclusive o `.env` (ver [README](../README.md#integração-contínua)).

## Requisitos

- processador x86_64 (a imagem do SQL Server não roda em ARM);
- 4 GB de RAM livres (o SQL Server sozinho pede 2 GB) e uns 10 GB de disco;
- acesso à internet para baixar as imagens e os pacotes.

## 1. Instalar o Docker

Use o repositório oficial do Docker (o pacote `docker.io` do Ubuntu costuma vir sem o plugin `compose`):

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git openssl python3
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
$(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker "$USER"
```

Saia da sessão e entre de novo (ou reinicie) para o grupo `docker` valer. Confira:

```bash
docker compose version
```

## 2. Baixar o código

```bash
git clone https://github.com/LucasDevolps/Desbravadores.Backend.git
cd Desbravadores.Backend
```

`main` é a versão estável; `develop` tem as novidades ainda em validação.

## 3. Criar o `.env`

O `.env` guarda os segredos desta máquina. Ele nunca vai para o Git. Os comandos abaixo geram senhas e a
chave do JWT aleatórias, sem mostrá-las na tela:

```bash
cp .env.example .env
chmod 600 .env
sed -i "s|^SQL_SA_PASSWORD=.*|SQL_SA_PASSWORD=Sa#$(openssl rand -hex 16)|" .env
sed -i "s|^SQL_ADMIN_PASSWORD=.*|SQL_ADMIN_PASSWORD=Adm#$(openssl rand -hex 16)|" .env
sed -i "s|^JWT_KEY_V1=.*|JWT_KEY_V1=$(openssl rand -base64 32)|" .env
```

Depois abra o arquivo (`nano .env`) e defina `SEED_ADMIN_SENHA`: a senha do administrador inicial
(`SEED_ADMIN_EMAIL`, padrão `admin@local.dev`), com 15 a 128 caracteres e não trivial. Guarde essa senha
num gerenciador de senhas. Confira que não sobrou nenhum placeholder (o comando não deve imprimir nada):

```bash
grep -nE '^[A-Z0-9_]+=DEFINA_' .env
```

Os demais valores do `.env.example` já servem para uso local. Se as portas 8090, 8443 ou 14330 estiverem
ocupadas, troque `API_HOST_PORT`, `API_HTTPS_HOST_PORT` ou `SQL_HOST_PORT`.

## 4. Criar o certificado HTTPS local

O login só funciona por HTTPS. Para uso local, gere um certificado autoassinado (uma vez só):

```bash
mkdir -p nginx/certs
openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -noenc \
  -keyout nginx/certs/localhost.key -out nginx/certs/localhost.pem \
  -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
chmod 600 nginx/certs/localhost.key
```

Para publicar com domínio e certificado de verdade, use o modo TLS descrito no
[README](../README.md#tls-num-ambiente-publicado-domínioip-público-real).

## 5. Subir

```bash
docker compose -f compose.yaml -f compose.https.yaml build --pull
docker compose -f compose.yaml -f compose.https.yaml up -d --wait
docker compose -f compose.yaml -f compose.https.yaml ps
```

`--pull` baixa a imagem base mais recente do .NET (com as correções de segurança). Na primeira vez, o
download e o build levam alguns minutos. As migrations do banco rodam sozinhas quando a API inicia.

Se o build parar com "abaixo do mínimo 10.0.13", a imagem base em cache está desatualizada: rode o
`build --pull` acima de novo (com internet). A API nunca sobe com um runtime vulnerável.

## 6. Conferir

```bash
# API saudável pelo Nginx (deve responder "Healthy")
curl --cacert nginx/certs/localhost.pem https://localhost:8443/health; echo

# Runtime do ASP.NET Core: 10.0.13 ou mais novo (o build já recusa versão menor)
docker compose exec api dotnet --list-runtimes | grep AspNetCore

# Hub de notificações publicado e exigindo login (deve imprimir 401)
curl --cacert nginx/certs/localhost.pem -s -o /dev/null -w '%{http_code}\n' \
  -X POST 'https://localhost:8443/hubs/notificacoes/negotiate?negotiateVersion=1'
```

Para entrar como administrador e obter um token, siga [`docs/local-login.md`](local-login.md). O
Swagger fica em `http://localhost:8090/swagger` (ambiente `Development`). Notificações e o hub SignalR
estão descritos em [`docs/notificacoes.md`](notificacoes.md).

O que fica exposto: a porta HTTP do Nginx (`API_HOST_PORT`, padrão 8090) escuta em todas as interfaces,
então outras máquinas da rede conseguem chegar até ela. A porta HTTPS local (8443) e o SQL Server (14330)
escutam só em `127.0.0.1`. O `ufw` não bloqueia portas publicadas pelo Docker.

## Atualizar para uma versão nova

Faça um backup antes (abaixo). Depois:

```bash
git pull
docker compose -f compose.yaml -f compose.https.yaml build --pull
docker compose -f compose.yaml -f compose.https.yaml up -d --wait
```

As migrations novas são aplicadas na inicialização. Se uma versão nova trouxer uma variável nova, ela
aparece no `.env.example`: copie a linha para o seu `.env`.

## Backup do banco

```bash
export SQLCMDPASSWORD="$(grep '^SQL_SA_PASSWORD=' .env | cut -d= -f2-)"
docker exec -e SQLCMDPASSWORD almirante-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C \
  -Q "BACKUP DATABASE [almirante] TO DISK = N'/var/opt/mssql/data/almirante-backup.bak' WITH INIT"
docker cp almirante-sqlserver:/var/opt/mssql/data/almirante-backup.bak "./almirante-$(date +%F).bak"
unset SQLCMDPASSWORD
```

Guarde o arquivo `.bak` fora da máquina. Os dados ficam no volume `almirante-sqlserver-data`:
`docker compose down` para os serviços sem apagar nada; `docker compose down -v` **apaga o banco**.

## Problemas comuns

| Sintoma | O que fazer |
|---|---|
| `permission denied` ao usar `docker` | O usuário ainda não está no grupo `docker`: saia e entre de novo na sessão |
| `port is already allocated` | Troque a porta correspondente no `.env` e suba de novo |
| `sqlserver` reiniciando ou `unhealthy` | Falta memória (precisa de 2 GB) ou o processador não é x86_64 |
| API não sobe e o log cita `DEFINA_` ou senha fraca | Algum placeholder ficou no `.env` (passo 3) |
| `429` no login | Limite de tentativas do Nginx: aguarde um minuto |
| Login devolve `500` falando de cookie seguro | Use a porta HTTPS (8443), não a HTTP |

Logs: `docker compose -f compose.yaml -f compose.https.yaml logs api --tail 100`.
