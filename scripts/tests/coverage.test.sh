#!/usr/bin/env bash
# Testes de scripts/coverage.sh SEM .NET: relatórios Cobertura sintéticos provam que o quality gate
# (a) aprova no limite exato, (b) reprova qualquer regressão acima da tolerância — inclusive a que um
# arredondamento esconderia —, (c) trata entrada ausente/corrompida como erro e (d) que a normalização
# deixa os relatórios do Windows e do Linux apontando para os mesmos arquivos.
# Uso: bash scripts/tests/coverage.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SCRIPT="$ROOT/scripts/coverage.sh"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
falhas=0

ok() { echo "ok   - $1"; }
falha() { falhas=$((falhas + 1)); echo "FAIL - $1 :: $2"; }

# cobertura ARQUIVO LINHAS_COBERTAS LINHAS_VALIDAS BRANCHES_COBERTOS BRANCHES_VALIDOS
cobertura() {
  cat > "$1" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0" branch-rate="0" lines-covered="$2" lines-valid="$3" branches-covered="$4" branches-valid="$5" version="0">
  <sources><source>/repo/src/</source></sources>
  <packages>
    <package name="Almirante.Api" line-rate="0" branch-rate="0">
      <classes><class name="A.B" filename="Almirante.Api/A.cs" line-rate="0" branch-rate="0"></class></classes>
    </package>
  </packages>
</coverage>
EOF
}

# baseline LINHAS BRANCHES TOLERANCIA
baseline() {
  cat > "$TMP/baseline.json" <<EOF
{
  "lineCoverageBaseline": $1,
  "branchCoverageBaseline": $2,
  "allowedRegressionPercentagePoints": $3
}
EOF
}

# gate_espera CODIGO_ESPERADO DESCRICAO [TRECHO_ESPERADO_NA_SAIDA]
gate_espera() {
  local esperado=$1 desc=$2 trecho=${3:-}
  COVERAGE_COMMIT=teste bash "$SCRIPT" gate "$TMP/c.xml" "$TMP/baseline.json" "$TMP/resumo.md" > "$TMP/out" 2>&1
  local rc=$?
  if [[ $rc != "$esperado" ]]; then falha "$desc" "exit $rc (esperado $esperado): $(cat "$TMP/out")"; return; fi
  if [[ -n "$trecho" ]] && ! grep -qF -- "$trecho" "$TMP/out"; then falha "$desc" "saída sem '$trecho': $(cat "$TMP/out")"; return; fi
  ok "$desc"
}

baseline 52.00 60.00 2.00
cobertura "$TMP/c.xml" 5000 10000 580 1000
gate_espera 0 "aprova exatamente no mínimo permitido (baseline − tolerância)" "| Linhas | 50.00% | 5000/10000 | 52.00% | 50.00% | ✅ |"

cobertura "$TMP/c.xml" 4999 10000 580 1000
gate_espera 1 "reprova linhas 0,01 p.p. abaixo do mínimo" "FALHOU: cobertura de linhas 49.99% < mínimo 50.00%"

cobertura "$TMP/c.xml" 49999 100000 580 1000
gate_espera 1 "reprova 49,999% contra mínimo de 50% (arredondar esconderia a regressão)" "FALHOU: cobertura de linhas"

cobertura "$TMP/c.xml" 6000 10000 579 1000
gate_espera 1 "reprova regressão só de branches" "FALHOU: cobertura de branches 57.90% < mínimo 58.00%"
grep -q "FALHOU: cobertura de linhas" "$TMP/out" && falha "não acusa linhas sem motivo" "$(cat "$TMP/out")" || ok "não acusa linhas sem motivo"

baseline 52.5 60 0.25
cobertura "$TMP/c.xml" 5225 10000 5975 10000
gate_espera 0 "aceita baseline com 0 ou 1 casa decimal (52.5 − 0.25 = 52.25)" "| 52.50% | 52.25% |"

: > "$TMP/resumo.md"
baseline 52.00 60.00 2.00
cobertura "$TMP/c.xml" 4000 10000 580 1000
gate_espera 1 "reprovação ainda escreve o resumo" "❌"
if grep -q '^## Test Coverage' "$TMP/resumo.md" && grep -q '❌ reprovado' "$TMP/resumo.md" && grep -q 'Almirante.Api' "$TMP/resumo.md"; then
  ok "resumo markdown tem título, status e assemblies"
else
  falha "resumo markdown tem título, status e assemblies" "$(cat "$TMP/resumo.md")"
fi

rm -f "$TMP/c.xml"
gate_espera 2 "relatório inexistente é erro" "inexistente"

echo '<coverage line-rate="0.5">' > "$TMP/c.xml"
gate_espera 2 "relatório sem contagens (corrompido) é erro" "ausente ou inválido"

echo 'isto não é xml' > "$TMP/c.xml"
gate_espera 2 "arquivo que não é Cobertura é erro" "ausente ou inválido"

cobertura "$TMP/c.xml" 0 0 0 0
gate_espera 2 "relatório sem linhas válidas é erro (coleta vazia não passa)" "sem itens válidos"

cobertura "$TMP/c.xml" 11 10 1 1
gate_espera 2 "contagens incoerentes são erro" "incoerentes"

cobertura "$TMP/c.xml" 5000 10000 580 1000
echo '{ "lineCoverageBaseline": 52.00 }' > "$TMP/baseline.json"
gate_espera 2 "baseline sem chave obrigatória é erro" "branchCoverageBaseline"

baseline '"52"' 60 2
gate_espera 2 "baseline com valor não numérico é erro" "lineCoverageBaseline"

baseline 52.123 60 2
gate_espera 2 "baseline com mais de 2 casas é erro (sem arredondamento implícito)" "lineCoverageBaseline"

baseline 101 60 2
gate_espera 2 "baseline acima de 100 é erro" "acima de 100"

rm -f "$TMP/baseline.json"
gate_espera 2 "baseline inexistente é erro" "baseline inexistente"

# normalize: relatório do job Windows passa a apontar para os mesmos arquivos do Linux.
cat > "$TMP/win.xml" <<'EOF'
<coverage lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0">
  <sources>
    <source>D:\a\Desbravadores.Backend\Desbravadores.Backend\backend\</source>
  </sources>
  <class name="Almirante.Api.Controllers.X" filename="Almirante.Api\Controllers\Sub\X.cs" line-rate="0.5">
</coverage>
EOF
if bash "$SCRIPT" normalize /home/runner/work/repo/src "$TMP/win.xml" "$TMP/norm.xml" > "$TMP/out" 2>&1 \
  && grep -qF '<source>/home/runner/work/repo/src/</source>' "$TMP/norm.xml" \
  && grep -qF 'filename="Almirante.Api/Controllers/Sub/X.cs"' "$TMP/norm.xml" \
  && grep -qF 'name="Almirante.Api.Controllers.X"' "$TMP/norm.xml" \
  && ! grep -q '\\' "$TMP/norm.xml"; then
  ok "normalize troca o <source> e os separadores do Windows"
else
  falha "normalize troca o <source> e os separadores do Windows" "$(cat "$TMP/out" "$TMP/norm.xml" 2>/dev/null)"
fi

sed 's|<sources>|<sources><source>/outro/</source>|' "$TMP/win.xml" > "$TMP/dois.xml"
if bash "$SCRIPT" normalize /x "$TMP/dois.xml" "$TMP/norm.xml" > "$TMP/out" 2>&1; then
  falha "normalize recusa mais de um <source>" "deveria falhar"
else
  ok "normalize recusa mais de um <source>"
fi

if bash "$SCRIPT" normalize /x "$TMP/nao-existe.xml" "$TMP/norm.xml" > "$TMP/out" 2>&1; then
  falha "normalize recusa entrada inexistente" "deveria falhar"
else
  ok "normalize recusa entrada inexistente"
fi

# Não chega a chamar o ReportGenerator: sem entrada, falha antes.
mkdir -p "$TMP/vazio/maquina/In/x"
cobertura "$TMP/vazio/maquina/In/x/coverage.cobertura.xml" 1 1 1 1
if bash "$SCRIPT" report "$TMP/vazio" "$TMP/rel" > "$TMP/out" 2>&1; then
  falha "report sem relatórios de entrada é erro (cópia TRX em In/ não conta)" "deveria falhar"
elif grep -q "nenhum coverage.cobertura.xml" "$TMP/out"; then
  ok "report sem relatórios de entrada é erro (cópia TRX em In/ não conta)"
else
  falha "report sem relatórios de entrada é erro (cópia TRX em In/ não conta)" "$(cat "$TMP/out")"
fi

# Rótulos do resumo: o relatório unitário e o combinado não podem sair com o mesmo título/suítes.
baseline 52.00 60.00 2.00
cobertura "$TMP/c.xml" 5000 10000 580 1000
: > "$TMP/resumo.md"
COVERAGE_TITULO="Cobertura unitária" COVERAGE_SUITES="só a suíte sem dependências" COVERAGE_COMMIT=teste \
  bash "$SCRIPT" gate "$TMP/c.xml" "$TMP/baseline.json" "$TMP/resumo.md" > "$TMP/out" 2>&1
if grep -q '^## Cobertura unitária$' "$TMP/resumo.md" && grep -qF -- '- **Suítes:** só a suíte sem dependências' "$TMP/resumo.md"; then
  ok "gate usa COVERAGE_TITULO e COVERAGE_SUITES no resumo"
else
  falha "gate usa COVERAGE_TITULO e COVERAGE_SUITES no resumo" "$(cat "$TMP/resumo.md")"
fi

# Gate de 100% (baseline 100, tolerância 0): uma única linha ou branch descoberto reprova.
baseline 100.00 100.00 0.00
cobertura "$TMP/c.xml" 10000 10000 1000 1000
gate_espera 0 "gate de 100% aprova cobertura total"
cobertura "$TMP/c.xml" 9999 10000 1000 1000
gate_espera 1 "gate de 100% reprova uma linha descoberta" "FALHOU: cobertura de linhas 99.99% < mínimo 100.00%"
cobertura "$TMP/c.xml" 10000 10000 999 1000
gate_espera 1 "gate de 100% reprova um branch descoberto" "FALHOU: cobertura de branches"

# lacunas: lista por arquivo, relativa ao <source>, com a mesma linha no método e na classe contada uma vez
# (vale o maior número de hits/branches cobertos) e linhas consecutivas agrupadas em intervalos.
cat > "$TMP/lac.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<coverage lines-covered="3" lines-valid="8" branches-covered="3" branches-valid="6" version="0">
  <sources>
    <source>C:\repo\src\</source>
  </sources>
  <packages>
    <package name="Almirante.Api">
      <classes>
        <class name="A" filename="C:\repo\src\Almirante.Api\Services\A.cs">
          <methods><method name="M"><lines>
            <line number="10" hits="0" branch="true" condition-coverage="0% (0/2)" />
          </lines></method></methods>
          <lines>
            <line number="10" hits="3" branch="true" condition-coverage="50% (1/2)" />
            <line number="11" hits="0" branch="false" />
            <line number="12" hits="0" branch="false" />
            <line number="13" hits="0" branch="false" />
            <line number="20" hits="0" branch="false" />
          </lines>
        </class>
        <class name="A+Parcial" filename="C:\repo\src\Almirante.Api\Services\A.cs">
          <lines><line number="20" hits="0" branch="false" /></lines>
        </class>
        <class name="B" filename="C:\repo\src\Almirante.Api\B.cs">
          <lines>
            <line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" />
            <line number="2" hits="1" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
EOF
: > "$TMP/resumo.md"
if COVERAGE_TITULO="Combinada" bash "$SCRIPT" lacunas "$TMP/lac.xml" "$TMP/resumo.md" > "$TMP/out" 2>&1 \
  && grep -qF '### Lacunas — Combinada' "$TMP/out" \
  && grep -qF '| `Almirante.Api/Services/A.cs` | 11-13, 20 | 10 (1/2) |' "$TMP/out" \
  && ! grep -qF 'B.cs' "$TMP/out" \
  && [[ "$(grep -c '^| `' "$TMP/out")" == 1 ]] \
  && grep -qF '| `Almirante.Api/Services/A.cs` |' "$TMP/resumo.md"; then
  ok "lacunas lista só os arquivos com algo descoberto, sem duplicar linhas, relativo ao <source>"
else
  falha "lacunas lista só os arquivos com algo descoberto, sem duplicar linhas, relativo ao <source>" "$(cat "$TMP/out")"
fi

sed -e 's/hits="0"/hits="1"/g' -e 's/(0\/2)/(2\/2)/; s/(1\/2)/(2\/2)/' "$TMP/lac.xml" > "$TMP/lac-total.xml"
if bash "$SCRIPT" lacunas "$TMP/lac-total.xml" > "$TMP/out" 2>&1 && grep -qF 'Nenhuma: todas as linhas e branches medidos estão cobertos.' "$TMP/out"; then
  ok "lacunas sem nada descoberto diz que não há lacunas"
else
  falha "lacunas sem nada descoberto diz que não há lacunas" "$(cat "$TMP/out")"
fi

if bash "$SCRIPT" lacunas "$TMP/nao-existe.xml" > "$TMP/out" 2>&1; then
  falha "lacunas recusa relatório inexistente" "deveria falhar"
else
  ok "lacunas recusa relatório inexistente"
fi

# Os baselines versionados precisam ser válidos para o gate (formato e faixas).
cobertura "$TMP/c.xml" 1 1 1 1
for arquivo in coverage-baseline.json coverage-baseline-unit.json; do
  if COVERAGE_COMMIT=teste bash "$SCRIPT" gate "$TMP/c.xml" "$ROOT/src/$arquivo" > "$TMP/out" 2>&1; then
    ok "src/$arquivo é lido pelo gate"
  else
    falha "src/$arquivo é lido pelo gate" "$(cat "$TMP/out")"
  fi
done

# Issue #160: o gate da cobertura combinada é 100% de linhas e branches, sem tolerância. Baixar a meta exige mudar este teste
# no mesmo PR (e a política em docs/test-coverage.md), nunca só o JSON.
cobertura "$TMP/c.xml" 15848 15849 3949 3949
if COVERAGE_COMMIT=teste bash "$SCRIPT" gate "$TMP/c.xml" "$ROOT/src/coverage-baseline.json" > "$TMP/out" 2>&1; then
  falha "src/coverage-baseline.json reprova uma única linha descoberta (meta de 100%)" "$(cat "$TMP/out")"
else
  ok "src/coverage-baseline.json reprova uma única linha descoberta (meta de 100%)"
fi
cobertura "$TMP/c.xml" 15849 15849 3948 3949
if COVERAGE_COMMIT=teste bash "$SCRIPT" gate "$TMP/c.xml" "$ROOT/src/coverage-baseline.json" > "$TMP/out" 2>&1; then
  falha "src/coverage-baseline.json reprova um único branch descoberto (meta de 100%)" "$(cat "$TMP/out")"
else
  ok "src/coverage-baseline.json reprova um único branch descoberto (meta de 100%)"
fi

if ((falhas > 0)); then
  echo
  echo "$falhas teste(s) de cobertura falharam." >&2
  exit 1
fi
echo
echo "Script de cobertura: tudo certo."
