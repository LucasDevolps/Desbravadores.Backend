FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Almirante.Api/Almirante.Api.csproj Almirante.Api/
COPY Almirante.ServiceDefaults/Almirante.ServiceDefaults.csproj Almirante.ServiceDefaults/
RUN dotnet restore Almirante.Api/Almirante.Api.csproj

COPY Almirante.Api/ Almirante.Api/
COPY Almirante.ServiceDefaults/ Almirante.ServiceDefaults/
RUN dotnet publish Almirante.Api/Almirante.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is only used by the container HEALTHCHECK/compose healthcheck to probe /health.
# The base image already ships a non-root "app" user, so it is reused instead of creating a new one.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Key ring do Data Protection (volume em compose.yaml). Sem este diretório na imagem, o volume nomeado
# nasce root:root 755 e o usuário "app" não consegue gravar a chave: o antiforgery falha (500). Um
# volume novo herda dono e permissão daqui.
RUN mkdir -p /var/lib/almirante/dataprotection \
    && chown app:app /var/lib/almirante/dataprotection \
    && chmod 700 /var/lib/almirante/dataprotection

USER app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Almirante.Api.dll"]
