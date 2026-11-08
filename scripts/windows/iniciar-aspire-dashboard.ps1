# Console do Aspire (dashboard standalone) no deploy Windows. Copiado pelo job deploy-windows para a pasta do console
# (C:\desbravadores\aspire-dashboard) e executado pela tarefa agendada AspireDashboardAlmirante como LOCAL SERVICE.
# Interface e OTLP só em 127.0.0.1; a interface exige o token de browser-token.txt (pasta legível só por administradores,
# SYSTEM e o próprio LOCAL SERVICE). A telemetria fica só em memória e some ao reiniciar.
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSCommandPath

$env:ASPNETCORE_URLS = 'http://127.0.0.1:18888'
$env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = 'http://127.0.0.1:18889'
$env:Dashboard__Frontend__AuthMode = 'BrowserToken'
$env:Dashboard__Frontend__BrowserToken = (Get-Content -LiteralPath (Join-Path $raiz 'browser-token.txt') -Raw).Trim()
# Sem autenticação no OTLP: ele só escuta no loopback, onde a API do IIS é quem envia.
$env:Dashboard__Otlp__AuthMode = 'Unsecured'

& (Join-Path $raiz 'bin\Aspire.Dashboard.exe') *> (Join-Path $raiz 'logs\dashboard.log')
exit $LASTEXITCODE
