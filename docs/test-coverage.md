# Cobertura de testes e quality gate

O CI mede a cobertura do backend em todo push e PR para `main` e `develop`, publica os relatórios e aplica dois gates
(issues #67 e #160):

- **cobertura combinada** (as duas suítes juntas): **100% de linhas e de branches** do código próprio medido, sem tolerância;
- **cobertura unitária** (só a suíte sem dependências externas): publicada separada e protegida contra regressão em relação ao
  baseline observado. Ela não chega a 100% por desenho (seção [Lacunas da cobertura unitária](#lacunas-da-cobertura-unitária)), e a
  cobertura de integração nunca é usada para declarar 100% unitário.

Cobertura não prova correção: os testes precisam afirmar resultados e efeitos persistidos, não apenas executar código. A matriz que
liga cada endpoint e regra aos testes está em [`rastreabilidade.md`](rastreabilidade.md).

## Unitária x combinada

| Medição | Suítes | Onde roda | Gate |
|---|---|---|---|
| Unitária | `Category!=RequiresDocker&Category!=RequiresSqlServer`: testes puros e de componente em processo (`WebApplicationFactory` com EF InMemory, ViaCEP simulado) | job `build-and-test` (Windows) | não regredir mais de 1 p.p. abaixo de `src/coverage-baseline-unit.json` |
| Combinada | a unitária **+** `Category=RequiresSqlServer` (SQL Server 2022 real e descartável, API em processo real) | job `sqlserver-integration` (Linux) + job `coverage` | 100% de linhas e branches (`src/coverage-baseline.json`) |

Não há testes `Category=RequiresDocker` hoje: o filtro só reserva a categoria.

## Como funciona no CI

```text
build-and-test (Windows)                   sqlserver-integration (Linux + SQL Server 2022 descartável)
  testes sem dependências externas           testes Category=RequiresSqlServer
  Coverlet: Cobertura + OpenCover            Coverlet: Cobertura + OpenCover
  → coverage-unit-<sha>                      → coverage-sqlserver-<sha>
                      \                         /
                       coverage (Linux)
                         unitária  = só coverage-unit         → coverage-report/unitaria
                         combinada = as duas, unidas          → coverage-report/combinada
                         gate 100% na combinada; não regressão na unitária
                         Job Summary com números, lacunas (arquivo/linha/branch) e detalhe por classe
                         artifact coverage-<sha>
```

1. **Coleta:** os dois jobs rodam `dotnet test` com `--settings src/coverage.runsettings --collect "XPlat Code Coverage"`
   (`coverlet.collector`), nos formatos Cobertura e OpenCover.
2. **Publicação bruta:** cada job publica só a pasta do coletor (`TestResults/<guid>/coverage.*.xml`); a cópia que o logger TRX põe
   em `<máquina>/In/` é ignorada para não duplicar a entrada.
3. **Consolidação** (`bash scripts/coverage.sh report <entrada> <saida>`): normaliza os caminhos de cada máquina (o mesmo arquivo
   medido no Windows e no Linux precisa ser o mesmo arquivo para o ReportGenerator) e une os relatórios com o
   [ReportGenerator](https://reportgenerator.io/) fixado em `.config/dotnet-tools.json`. O job gera duas saídas: `unitaria` (só a
   suíte sem dependências) e `combinada` (as duas).
4. **Gates** (`bash scripts/coverage.sh gate`): comparam as contagens inteiras do Cobertura consolidado com o baseline de cada
   medição e escrevem a tabela no Job Summary. O gate unitário roda mesmo se o combinado reprovar, para os dois números saírem sempre.
5. **Lacunas** (`bash scripts/coverage.sh lacunas`): lista, por arquivo, as linhas e os branches descobertos (`linha (cobertos/total)`)
   das duas medições, no Job Summary e em `coverage-report/<medição>/Lacunas.md`.

### Por que a união usa OpenCover

O Cobertura só informa "N de M branches" por linha, sem dizer quais. Um `if (db.Database.IsRelational())` coberto do lado falso
pela suíte em memória e do lado verdadeiro pela suíte SQL Server aparecia como 1/2 na combinada, porque o ReportGenerator não
consegue unir ramos que não sabe identificar. O OpenCover identifica cada ramo pelo offset no IL, e a união soma o que cada suíte
cobriu. Na medição local do `LoginLockout`, por exemplo, a linha acima passa de 1/2 (Cobertura) para 2/2 (OpenCover).
O Cobertura continua sendo gerado, para leitura direta e para o gate (o ReportGenerator o produz a partir da união).

### Processos filhos

O Coverlet instrumenta as DLLs do diretório de testes. Um processo filho que carrega essas DLLs (a CLI `reset-admin-password` e a
API dos testes `ApiProcess*`) grava a própria cobertura **ao encerrar normalmente**. Por isso o `ApiProcess` encerra a API com
`SIGTERM` no Linux (desligamento gracioso, como o Docker faz) e só usa `Kill` como último recurso; no Windows o `Kill` é o único
caminho e a cobertura desse processo se perde (a suíte SQL Server do CI roda em Linux).

### Onde ver o resultado

- **Job Summary** do job `coverage` (aba *Summary* do run do *Backend CI* ou *Checks* do PR): as duas tabelas (unitária e
  combinada), as lacunas e o detalhamento por assembly e classe.
- **Artifact `coverage-<sha>`**: `combinada/` e `unitaria/`, cada uma com `index.html` navegável, `Cobertura.xml`,
  `SummaryGithub.md`, `Lacunas.md` e `entradas/` (os relatórios normalizados de cada suíte).

## O que entra na medição

| Item | Valor |
|---|---|
| Assemblies medidos | `Almirante.Api` e `Almirante.ServiceDefaults` (`Include` do `src/coverage.runsettings`) |
| Fora da medição | `Almirante.Api.Tests` (o próprio teste) e `Almirante.AppHost` |
| Métricas | linhas e branches |

**Por que o `Almirante.AppHost` fica fora.** São 37 linhas de orquestração do .NET Aspire usadas só no desenvolvimento local: a
mesma configuração de identidades e do bootstrap já é exercitada pela API real (`ApiProcess*`, `SqlIdentityModelTests`) e pelos
testes dos scripts do Compose. Testá-lo exigiria o runtime do Aspire e contêineres. A exclusão é por assembly, explícita e
documentada aqui; ela não esconde código da API.

### Exclusões dentro dos assemblies medidos

Ficam fora da métrica **apenas** arquivos gerados por ferramenta (`ExcludeByFile` do `src/coverage.runsettings`):

| Padrão | O que é | Por quê |
|---|---|---|
| `**/Data/Migrations/*.Designer.cs` | metadados de modelo de cada migration | gerados pelo `dotnet ef` (`// <auto-generated />`) |
| `**/Data/Migrations/*ModelSnapshot.cs` | snapshot do modelo do EF | idem |
| `**/obj/**/*.cs` | saída de source generators (Regex, comentários XML do OpenAPI) | gerado no build, nem existe no repositório |

O `Up` e o `Down` das migrations **continuam medidos**: `MigrationsReversibilidadeTests` sobe todas, desce até o banco vazio e sobe
de novo. Não há `[ExcludeFromCodeCoverage]` no código. Qualquer exclusão nova precisa ser mínima, visível no runsettings e
justificada nesta tabela no mesmo PR, nunca para esconder código difícil de testar.

## Números

Medidos pelo CI, sempre com a mesma suíte e o mesmo escopo:

- **Antes:** `develop` em `20a450d303c6d87ea7f76a516980884d7edd136f` (run 40495756276), união por Cobertura.
- **Depois:** `35f9a32` (run 41108178588), união por OpenCover.

| Medição | Antes | Depois |
|---|---|---|
| Unitária — linhas | 7803/15849 (49,23%) | 8141/15801 (51,52%) |
| Unitária — branches | 2476/3949 (62,70%) | 2738/3925 (69,75%) |
| Combinada — linhas | 15319/15849 (96,65%) | **15801/15801 (100%)** |
| Combinada — branches | 3568/3949 (90,35%) | **3925/3925 (100%)** |

| Projeto | Medição | Antes (linhas / branches) | Depois (linhas / branches) |
|---|---|---|---|
| `Almirante.Api` | Unitária | 7755/15800 (49,08%) / 2459/3931 (62,55%) | 8092/15752 (51,37%) / 2720/3907 (69,62%) |
| `Almirante.Api` | Combinada | 15271/15800 (96,65%) / 3551/3931 (90,33%) | 15752/15752 (100%) / 3907/3907 (100%) |
| `Almirante.ServiceDefaults` | Unitária | 48/49 (97,96%) / 17/18 (94,44%) | 49/49 (100%) / 18/18 (100%) |
| `Almirante.ServiceDefaults` | Combinada | 48/49 (97,96%) / 17/18 (94,44%) | 49/49 (100%) / 18/18 (100%) |

| Suíte | Testes antes | Testes depois |
|---|---|---|
| Sem dependências | 1272 (0 ignorados) | 1437 (0 ignorados) |
| `Category=RequiresSqlServer` | 421 (0 ignorados) | 475 (0 ignorados) |

Nenhum teste falhou em nenhum dos runs.

Os totais mudaram entre as colunas por três motivos: o OpenCover conta os branches de outra forma, código morto foi removido, e ramos
impossíveis saíram em refatorações sem mudança de comportamento (lista na descrição do PR #175). Parte dos 100% vem dessas remoções e
de extrair métodos para testá-los direto: isso é medição de código exercitado, não prova de ausência de defeitos. Ramos
extraídos para teste direto, como o retorno `-999` do `sp_getapplock` (`EventosService.ConferirLockDoGrupo`), não são alcançados pelo SQL Server real.

### Estabilidade da medição

Com tolerância zero, um ramo coberto só por sorte de tempo reprova o CI ao acaso. A auditoria achou dois casos, ambos corrigidos com
falha injetada: a falha inesperada no desligamento do `GravadorAuditoriaHttp` e o retry da leitura em `NotificacoesService`. Se o gate
acusar uma linha que estava coberta sem mudança no código, trate como teste dependente de tempo e torne-o determinístico; não
reduza o baseline nem reexecute até passar.

## Lacunas da cobertura unitária

A suíte sem dependências externas não executa o que só existe no SQL Server ou num processo real. As lacunas, arquivo por arquivo,
saem no Job Summary (`Lacunas — cobertura unitaria`) e em `coverage-report/unitaria/Lacunas.md`; por categoria:

| Categoria | Exemplos | Por que não é unitário | Onde é validado |
|---|---|---|---|
| Migrations (`Up`/`Down`) | `Data/Migrations/*.cs` (~3.850 linhas) | DDL, triggers e conversões de dados só fazem sentido executados no SQL Server | `MigrationsReversibilidadeTests` e os `*MigrationTests` |
| Serviços transacionais com SQL Server | `FormacaoService`, `EventosService`, `CatalogoFormacaoService`, `PatrimonioService`, `DesejosAlmoxarifadoService`, `AlteracaoSenhaService`, `UsuariosService` (escrita) | usam transação com `SESSION_CONTEXT` (o trigger de auditoria recusa sem ele), `sp_getapplock`, `UPDLOCK`, `rowversion` e SQL bruto; o InMemory ignora transações e não executa SQL. O próprio código recusa rodar sem banco relacional | `*SqlServerTests` do módulo |
| Identidades SQL e privilégios | `DbPrivilegeAuditor`, `DbCredentialManager` (rotação), `SessaoAuditoria` | consultam permissões efetivas e trocam senhas de usuários contidos | `SqlIdentityModelTests`, `DbPrivilegeTests`, `DbCredentialManagerSqlServerTests`, `SessaoAuditoriaPoolSqlTests` |
| Entidades e DTOs só lidos/gravados pelo banco | `Entities/*` (colunas de histórico), DTOs de módulos SQL | propriedades preenchidas por trigger ou só usadas nos fluxos acima | `HistoricosDeAuditoriaSqlServerTests` e os testes de cada módulo |
| Processo real | `Program.cs` com `DbCredentials:AppUser`, CLI com conexão administrativa | startup com identidades SQL e encerramento do processo | `ApiProcessSqlServerTests`, `AdminPasswordResetCliProcessTests` |

Não usar cobertura de integração para fechar a unitária: o número unitário é sempre o da suíte sem dependências.

## Política dos gates

```text
mínimo = baseline − tolerância
aprova se  cobertos × 10000 ≥ mínimo_em_centésimos × válidos      (linhas E branches, contagens inteiras)
```

- **Combinada:** `src/coverage-baseline.json` com 100.00/100.00 e tolerância 0.00. Uma única linha ou branch descoberto reprova.
  `scripts/tests/coverage.test.sh` falha se esse JSON for rebaixado; baixar a meta exige mudar o teste e esta política no mesmo PR.
- **Unitária:** `src/coverage-baseline-unit.json`, baseline observado (truncado em 2 casas) com tolerância de 1 p.p. (~160 linhas):
  impede que testes saiam da suíte sem dependências sem ninguém perceber.
- Relatório ausente, corrompido, sem linhas válidas ou baseline inválido reprovam (exit 2); regressão reprova com exit 1 e a
  mensagem indica métrica, valor e mínimo.
- Para bloquear o merge, o check `coverage` precisa constar dos checks obrigatórios dos rulesets de `main` e `develop`
  (configuração do repositório, feita por quem administra; ver [README](../README.md#proteção-de-branches)).

### Atualizar o baseline unitário

Só por PR, com os números do Job Summary do CI desse PR (nunca inventados): subir quando a unitária aumentar de forma consistente;
descer só com justificativa explícita (código bem testado removido, mudança de escopo) e revisão desta documentação.

## Reproduzir localmente

Requer .NET 10 SDK e Bash. `TestResults/` e `coverage-report/` são ignorados pelo Git.

```bash
dotnet tool restore
dotnet restore src/Almirante.slnx
dotnet build src/Almirante.slnx --configuration Release --no-restore

dotnet test src/Almirante.Api.Tests/Almirante.Api.Tests.csproj --configuration Release --no-build \
  --filter "Category!=RequiresDocker&Category!=RequiresSqlServer" \
  --settings src/coverage.runsettings --collect "XPlat Code Coverage" --results-directory TestResults/unit

# Só contra um SQL Server descartável/dedicado a testes — ver "Testes" no README e o CONTRIBUTING.
ALMIRANTE_TEST_SQLSERVER="Server=127.0.0.1,<porta>;User ID=sa;Password=<senha-descartável>;Encrypt=True;TrustServerCertificate=True" \
dotnet test src/Almirante.Api.Tests/Almirante.Api.Tests.csproj --configuration Release --no-build \
  --filter "Category=RequiresSqlServer" \
  --settings src/coverage.runsettings --collect "XPlat Code Coverage" --results-directory TestResults/sqlserver

bash scripts/coverage.sh report TestResults/unit coverage-report/unitaria
bash scripts/coverage.sh report TestResults coverage-report/combinada
bash scripts/coverage.sh gate coverage-report/combinada/Cobertura.xml src/coverage-baseline.json
bash scripts/coverage.sh gate coverage-report/unitaria/Cobertura.xml src/coverage-baseline-unit.json
bash scripts/coverage.sh lacunas coverage-report/combinada/Cobertura.xml
```

Em máquina com pouca memória (ex.: SQL Server no WSL com ~4 GB), rode a suíte SQL Server sozinha e com
`-- xUnit.MaxParallelThreads=2`. Rodando a suíte SQL Server no Windows, a cobertura da API em processo filho (`ApiProcess*`) não é
gravada (ver [Processos filhos](#processos-filhos)), então a combinada local fica um pouco abaixo da do CI.

O script tem testes próprios, sem .NET: `bash scripts/tests/coverage.test.sh` (no check `deploy-scripts`). A presença da coleta, dos
artifacts, dos dois relatórios, dos gates e das lacunas no workflow é verificada por `bash scripts/tests/workflows.test.sh`.

## Configurações avaliadas na issue #67

- **`.editorconfig` — mínimo.** Só espaçamento (indentação com espaços, 4 em C# e 2 em XML/JSON/YAML/Bash, sem espaço no fim da
  linha exceto Markdown, newline final). Sem regras de análise nem `charset`/`end_of_line`.
- **`Directory.Build.props` — não adicionado.** Os quatro projetos já declaram as mesmas propriedades e usam SDKs diferentes; a
  configuração de cobertura está centralizada no `src/coverage.runsettings`.
