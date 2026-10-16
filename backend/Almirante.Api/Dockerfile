FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Arquitetura da imagem (definida pelo BuildKit): o publish leva só o binário nativo do Skia dela (issue #116), e não os
# de todas as plataformas que o pacote traz (centenas de MB a mais na imagem).
ARG TARGETARCH
WORKDIR /src

COPY Almirante.Api/Almirante.Api.csproj Almirante.Api/
COPY Almirante.ServiceDefaults/Almirante.ServiceDefaults.csproj Almirante.ServiceDefaults/
RUN dotnet restore Almirante.Api/Almirante.Api.csproj -a "$TARGETARCH"

COPY Almirante.Api/ Almirante.Api/
COPY Almirante.ServiceDefaults/ Almirante.ServiceDefaults/
RUN dotnet publish Almirante.Api/Almirante.Api.csproj -c Release -a "$TARGETARCH" --self-contained false -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is only used by the container HEALTHCHECK/compose healthcheck to probe /health.
# The base image already ships a non-root "app" user, so it is reused instead of creating a new one.
# apt-get upgrade applies Ubuntu security fixes published after the last rebuild of the aspnet image
# (e.g. CVE-2026-84782 in openssl/libssl3t64), so the image-scan gate does not depend on Microsoft's
# rebuild cadence. --no-install-recommends keeps it from pulling packages the base image does not have.
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Diretório das chaves do Data Protection (DataProtection__KeysPath no compose.yaml). Precisa pertencer
# ao usuário "app": um volume nomeado vazio herda essa posse ao ser montado. Sem isso o volume nasce
# como root, a API não grava a chave e todo antiforgery (csrf/login/refresh/logout) responde 500.
RUN mkdir -p /var/lib/almirante/dataprotection \
    && chown app:app /var/lib/almirante/dataprotection \
    && chmod 700 /var/lib/almirante/dataprotection

# Fotos de usuários (Media__Storage__Path no compose.yaml, issue #116): mesmo motivo do diretório acima. Só o usuário
# "app" entra na pasta; a API grava cada foto com permissão 0600 (nunca executável).
RUN mkdir -p /var/lib/almirante/fotos \
    && chown app:app /var/lib/almirante/fotos \
    && chmod 700 /var/lib/almirante/fotos

USER app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Almirante.Api.dll"]
