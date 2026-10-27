#!/usr/bin/env bash
# Cobertura de testes: consolidação dos relatórios Cobertura do Coverlet e quality gate de regressão.
# Bash puro + ReportGenerator do manifesto local (.config/dotnet-tools.json). Política em docs/test-coverage.md.
#
#   bash scripts/coverage.sh normalize <dir-fontes> <entrada.xml> <saida.xml>
#   bash scripts/coverage.sh normalize-opencover <dir-fontes> <entrada.xml> <saida.xml>
#   bash scripts/coverage.sh report <dir-com-coberturas> <dir-relatorio>
#   bash scripts/coverage.sh gate <cobertura.xml> <baseline.json> [arquivo-resumo-markdown]
#   bash scripts/coverage.sh lacunas <cobertura.xml> [arquivo-resumo-markdown]
#
# O gate compara contagens inteiras (coberto × 10000 ≥ mínimo_em_centésimos × válido): nenhum arredondamento
# pode esconder uma regressão. Os percentuais exibidos são truncados (nunca arredondados para cima).
# COVERAGE_TITULO e COVERAGE_SUITES mudam o título e a linha "Suítes" do resumo (relatório unitário x combinado).
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

# Caminho absoluto de um diretório existente (no Git Bash do Windows, no formato C:/... que o .NET entende).
absoluto() {
  local caminho
  caminho=$(cd "$1" && pwd)
  if command -v cygpath > /dev/null 2>&1; then caminho=$(cygpath -m "$caminho"); fi
  echo "$caminho"
}
REPO=$(absoluto "$REPO")

falhar() { echo "ERRO: $*" >&2; exit 2; }

# O Coverlet grava um <source> absoluto da máquina e caminhos relativos a ele (com "\" no Windows). Relatórios
# do job Windows e do Linux só se somam corretamente se apontarem para os mesmos arquivos: o <source> passa a ser
# <dir-fontes> e os separadores viram "/". Sem isso o ReportGenerator trataria cada cópia como outro arquivo.
normalize() {
  local fontes="$1" entrada="$2" saida="$3"
  [[ -s "$entrada" ]] || falhar "relatório de cobertura inexistente ou vazio: $entrada"
  grep -q '<coverage ' "$entrada" || falhar "arquivo não é um relatório Cobertura: $entrada"
  [[ "$(grep -c '<source>' "$entrada")" == 1 ]] || falhar "esperado exatamente um <source> em $entrada."
  fontes="${fontes%/}/"
  local fontes_sed=${fontes//\\/\\\\}
  fontes_sed=${fontes_sed//&/\\&}
  fontes_sed=${fontes_sed//|/\\|}
  sed -e "s|<source>.*</source>|<source>$fontes_sed</source>|" \
      -e ':a' -e 's|\(filename="[^"\\]*\)\\|\1/|' -e 'ta' \
      "$entrada" > "$saida"
}

# OpenCover identifica cada ramo pelo offset no IL; o Cobertura só diz "1 de 2" por linha, sem dizer qual. Ao unir as duas
# suítes, só com OpenCover o ReportGenerator soma o ramo coberto por uma (ex.: banco em memória) ao coberto pela outra (SQL
# Server). Os caminhos absolutos de cada máquina (fullPath) passam a apontar para <dir-fontes>, com "/".
normalize_opencover() {
  local fontes="$1" entrada="$2" saida="$3"
  [[ -s "$entrada" ]] || falhar "relatório de cobertura inexistente ou vazio: $entrada"
  grep -q '<CoverageSession' "$entrada" || falhar "arquivo não é um relatório OpenCover: $entrada"
  fontes="${fontes%/}/"
  local fontes_sed=${fontes//\\/\\\\}
  fontes_sed=${fontes_sed//&/\\&}
  fontes_sed=${fontes_sed//|/\\|}
  sed -e "s|fullPath=\"[^\"]*[\\\\/]src[\\\\/]|fullPath=\"$fontes_sed|g" \
      -e ':a' -e 's|\(fullPath="[^"\\]*\)\\|\1/|' -e 'ta' \
      "$entrada" > "$saida"
}

report() {
  local entrada="$1" saida="$2"
  [[ -d "$entrada" ]] || falhar "diretório de entrada inexistente: $entrada"
  # O logger TRX copia o anexo para <máquina>/In/...; só a cópia da pasta do coletor entra (evita duplicata). Com OpenCover
  # presente (formato preferido, ver normalize_opencover), ele é usado; senão, o Cobertura.
  local formato=opencover
  local -a arquivos=()
  while IFS= read -r arquivo; do arquivos+=("$arquivo"); done \
    < <(find "$entrada" -name coverage.opencover.xml -not -path '*/In/*' | LC_ALL=C sort)
  if ((${#arquivos[@]} == 0)); then
    formato=cobertura
    while IFS= read -r arquivo; do arquivos+=("$arquivo"); done \
      < <(find "$entrada" -name coverage.cobertura.xml -not -path '*/In/*' | LC_ALL=C sort)
  fi
  ((${#arquivos[@]} > 0)) || falhar "nenhum coverage.opencover.xml nem coverage.cobertura.xml encontrado em $entrada."

  rm -rf "$saida"
  mkdir -p "$saida/entradas"
  saida=$(absoluto "$saida")
  local i=0 relatorios=""
  for arquivo in "${arquivos[@]}"; do
    i=$((i + 1))
    if [[ "$formato" == opencover ]]; then
      normalize_opencover "$REPO/src" "$arquivo" "$saida/entradas/$i.opencover.xml"
    else
      normalize "$REPO/src" "$arquivo" "$saida/entradas/$i.cobertura.xml"
    fi
    relatorios+="${relatorios:+;}$saida/entradas/$i.$formato.xml"
    echo "entrada $i ($formato): $arquivo"
  done

  (cd "$REPO" && dotnet tool run reportgenerator \
    "-reports:$relatorios" \
    "-targetdir:$saida" \
    "-reporttypes:Html;Cobertura;MarkdownSummaryGithub" \
    "-title:Almirante" \
    "-verbosity:Warning")
  [[ -s "$saida/Cobertura.xml" ]] || falhar "ReportGenerator não gerou $saida/Cobertura.xml."
}

# Atributo numérico inteiro da raiz <coverage ...>.
atributo_raiz() {
  local valor
  valor=$(grep -m1 -o '<coverage [^>]*>' "$1" | grep -o " $2=\"[0-9]*\"" | grep -o '[0-9]\+' || true)
  [[ -n "$valor" ]] || falhar "atributo '$2' ausente ou inválido na raiz de $1 (arquivo corrompido?)."
  echo "$valor"
}

# Número decimal do baseline (até 2 casas) convertido para centésimos de ponto percentual.
centesimos_do_baseline() {
  local valor
  valor=$(grep -o "\"$2\"[[:space:]]*:[[:space:]]*[^,}]*" "$1" | head -1 | sed 's/.*:[[:space:]]*//; s/[[:space:]]*$//' || true)
  [[ "$valor" =~ ^[0-9]{1,3}(\.[0-9]{1,2})?$ ]] || falhar "'$2' ausente ou inválido em $1 (use número com até 2 casas, ex.: 57.25)."
  local inteiro=${valor%%.*} fracao=00
  [[ "$valor" == *.* ]] && fracao="${valor#*.}0" && fracao=${fracao:0:2}
  local centesimos=$((10#$inteiro * 100 + 10#$fracao))
  ((centesimos <= 10000)) || falhar "'$2' acima de 100 em $1."
  echo "$centesimos"
}

pct() { printf '%d.%02d%%' $(($1 / 100)) $(($1 % 100)); }

# Percentual truncado em centésimos; 0 itens válidos é erro (métrica vazia não passa no gate).
pct_truncado() {
  (($2 > 0)) || falhar "relatório sem itens válidos para '$3' — a coleta falhou?"
  echo $(($1 * 10000 / $2))
}

gate() {
  local cobertura="$1" baseline="$2" resumo="${3:-}"
  [[ -s "$cobertura" ]] || falhar "relatório consolidado inexistente ou vazio: $cobertura"
  [[ -s "$baseline" ]] || falhar "baseline inexistente ou vazio: $baseline"

  local lc lv bc bv
  lc=$(atributo_raiz "$cobertura" lines-covered)
  lv=$(atributo_raiz "$cobertura" lines-valid)
  bc=$(atributo_raiz "$cobertura" branches-covered)
  bv=$(atributo_raiz "$cobertura" branches-valid)
  ((lc <= lv && bc <= bv)) || falhar "contagens incoerentes em $cobertura."

  local base_l base_b tol
  base_l=$(centesimos_do_baseline "$baseline" lineCoverageBaseline)
  base_b=$(centesimos_do_baseline "$baseline" branchCoverageBaseline)
  tol=$(centesimos_do_baseline "$baseline" allowedRegressionPercentagePoints)
  local min_l=$((base_l - tol)) min_b=$((base_b - tol))
  ((min_l < 0)) && min_l=0
  ((min_b < 0)) && min_b=0

  local atual_l atual_b ok_l=1 ok_b=1
  atual_l=$(pct_truncado "$lc" "$lv" linhas)
  atual_b=$(pct_truncado "$bc" "$bv" branches)
  ((lc * 10000 >= min_l * lv)) || ok_l=0
  ((bc * 10000 >= min_b * bv)) || ok_b=0

  local status_l="✅" status_b="✅" status="✅ aprovado"
  ((ok_l)) || status_l="❌"
  ((ok_b)) || status_b="❌"
  ((ok_l && ok_b)) || status="❌ reprovado — regressão acima da tolerância"

  local commit assemblies arquivos
  commit=${COVERAGE_COMMIT:-$(git -C "$REPO" rev-parse HEAD 2>/dev/null || echo desconhecido)}
  assemblies=$(grep -o '<package name="[^"]*"' "$cobertura" | sed 's/<package name="//; s/"$//' | LC_ALL=C sort -u | paste -sd, - | sed 's/,/, /g')
  arquivos=$(grep -o '<class [^>]*filename="[^"]*"' "$cobertura" | grep -o 'filename="[^"]*"' | LC_ALL=C sort -u | wc -l | tr -d ' ')

  local titulo=${COVERAGE_TITULO:-Test Coverage}
  local suites=${COVERAGE_SUITES:-testes sem dependências externas + \`Category=RequiresSqlServer\`}
  local markdown
  markdown=$(cat <<EOF
## $titulo

| Métrica | Atual | Cobertos/Válidos | Baseline | Mínimo permitido | Gate |
|---|---:|---:|---:|---:|:---:|
| Linhas | $(pct "$atual_l") | $lc/$lv | $(pct "$base_l") | $(pct "$min_l") | $status_l |
| Branches | $(pct "$atual_b") | $bc/$bv | $(pct "$base_b") | $(pct "$min_b") | $status_b |

- **Quality gate:** $status (tolerância: $(pct "$tol" | tr -d '%') p.p. abaixo do baseline)
- **Commit medido:** \`$commit\`
- **Assemblies:** $assemblies ($arquivos arquivos-fonte)
- **Suítes:** $suites
- Percentuais truncados em 2 casas; o gate compara as contagens exatas. Política: \`docs/test-coverage.md\`.
EOF
)
  echo "$markdown"
  [[ -z "$resumo" ]] || echo "$markdown" >> "$resumo"

  if ((ok_l && ok_b)); then
    echo "Quality gate de cobertura: aprovado."
    return 0
  fi
  ((ok_l)) || echo "FALHOU: cobertura de linhas $(pct "$atual_l") < mínimo $(pct "$min_l") (baseline $(pct "$base_l") − tolerância $(pct "$tol"))." >&2
  ((ok_b)) || echo "FALHOU: cobertura de branches $(pct "$atual_b") < mínimo $(pct "$min_b") (baseline $(pct "$base_b") − tolerância $(pct "$tol"))." >&2
  return 1
}

# Arquivos com linhas ou branches descobertos, em Markdown: deixa verificável QUAL código falta cobrir. A mesma linha
# aparece no método e na classe (e uma classe parcial em vários <class>): vale o maior número de hits/branches cobertos.
lacunas() {
  local cobertura="$1" resumo="${2:-}"
  [[ -s "$cobertura" ]] || falhar "relatório consolidado inexistente ou vazio: $cobertura"
  grep -q '<coverage ' "$cobertura" || falhar "arquivo não é um relatório Cobertura: $cobertura"
  local titulo=${COVERAGE_TITULO:-Test Coverage} tabela
  tabela=$(awk '
    /<source>/ {
      s = $0; sub(/.*<source>/, "", s); sub(/<\/source>.*/, "", s); gsub(/\\/, "/", s)
      if (s != "" && s !~ /\/$/) s = s "/"
      fontes[++nf] = s; next
    }
    /<class / {
      f = ""; if (match($0, /filename="[^"]*"/)) f = substr($0, RSTART + 10, RLENGTH - 11)
      gsub(/\\/, "/", f)
      for (i = 1; i <= nf; i++) if (fontes[i] != "" && index(f, fontes[i]) == 1) { f = substr(f, length(fontes[i]) + 1); break }
      next
    }
    /<line / && f != "" {
      if (!match($0, /number="[0-9]+"/)) next; n = substr($0, RSTART + 8, RLENGTH - 9) + 0
      if (!match($0, /hits="[0-9]+"/)) next; h = substr($0, RSTART + 6, RLENGTH - 7) + 0
      k = f SUBSEP n
      if (!(k in hits) || h > hits[k]) hits[k] = h
      if (match($0, /\([0-9]+\/[0-9]+\)/)) {
        split(substr($0, RSTART + 1, RLENGTH - 2), p, "/")
        if (!(k in bc) || p[1] + 0 > bc[k]) { bc[k] = p[1] + 0; bt[k] = p[2] + 0 }
      }
    }
    END {
      for (k in hits) {
        split(k, q, SUBSEP)
        c = (k in bc) ? bc[k] : 0; t = (k in bt) ? bt[k] : 0
        if (hits[k] == 0 || c < t) printf "%s\t%d\t%d\t%d\t%d\n", q[1], q[2], hits[k], c, t
      }
    }' "$cobertura" | LC_ALL=C sort -t "$(printf '\t')" -k1,1 -k2,2n | awk -F '\t' '
    function fecha_intervalo() { if (ini != "") { l = l (l == "" ? "" : ", ") (ini == fim ? ini : ini "-" fim); ini = "" } }
    function emite() { fecha_intervalo(); if (arq != "") printf "| `%s` | %s | %s |\n", arq, (l == "" ? "—" : l), (b == "" ? "—" : b) }
    $1 != arq { emite(); arq = $1; l = ""; b = ""; ini = "" }
    {
      if ($3 == 0) { if (ini != "" && $2 == fim + 1) fim = $2; else { fecha_intervalo(); ini = $2; fim = $2 } }
      if ($4 < $5) b = b (b == "" ? "" : ", ") $2 " (" $4 "/" $5 ")"
    }
    END { emite() }')
  local markdown
  if [[ -z "$tabela" ]]; then
    markdown="### Lacunas — $titulo"$'\n\n'"Nenhuma: todas as linhas e branches medidos estão cobertos."
  else
    markdown="### Lacunas — $titulo"$'\n\n'"| Arquivo | Linhas descobertas | Branches descobertos (linha: cobertos/total) |"$'\n'"|---|---|---|"$'\n'"$tabela"
  fi
  echo "$markdown"
  [[ -z "$resumo" ]] || { echo; echo "$markdown"; } >> "$resumo"
}

comando="${1:-}"
shift || true
case "$comando" in
  normalize) (($# == 3)) || falhar "uso: coverage.sh normalize <dir-fontes> <entrada.xml> <saida.xml>"; normalize "$@" ;;
  normalize-opencover) (($# == 3)) || falhar "uso: coverage.sh normalize-opencover <dir-fontes> <entrada.xml> <saida.xml>"; normalize_opencover "$@" ;;
  report) (($# == 2)) || falhar "uso: coverage.sh report <dir-com-coberturas> <dir-relatorio>"; report "$@" ;;
  gate) (($# == 2 || $# == 3)) || falhar "uso: coverage.sh gate <cobertura.xml> <baseline.json> [resumo.md]"; gate "$@" ;;
  lacunas) (($# == 1 || $# == 2)) || falhar "uso: coverage.sh lacunas <cobertura.xml> [resumo.md]"; lacunas "$@" ;;
  *) falhar "comando desconhecido '${comando}'. Use normalize, report, gate ou lacunas." ;;
esac
