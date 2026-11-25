#!/usr/bin/env bash
#
# Matriz de PRAGMAs SQLite: 4 configuraciones × N repeticiones de 1M de ventas.
#
# Uso:
#   ./scripts/run-matrix.sh
#   REPS=3 WARMUP=100000 TOTAL=1000000 ./scripts/run-matrix.sh
#
# Variables:
#   REPS=3              repeticiones medidas por configuración
#   WARMUP=100000       ventas de calentamiento antes de cada repetición
#   TOTAL=1000000       ventas medidas por repetición
#   CASHIERS=200        cajeros concurrentes
#   CONFIGURATION=Debug Debug|Release
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPS="${REPS:-3}"
WARMUP="${WARMUP:-100000}"
TOTAL="${TOTAL:-1000000}"
CASHIERS="${CASHIERS:-200}"
CONFIGURATION="${CONFIGURATION:-Debug}"
SALES_PER_CASHIER="${SALES_PER_CASHIER:-$((TOTAL / CASHIERS))}"
RESULTS="$ROOT/results/matrix"

API_DLL="$ROOT/src/Sales.Api/bin/$CONFIGURATION/net11.0/Sales.Api.dll"
LOADTEST_DLL="$ROOT/tools/Sales.LoadTest/bin/$CONFIGURATION/net11.0/Sales.LoadTest.dll"
COMPARE_DLL="$ROOT/tools/Sales.Compare/bin/$CONFIGURATION/net11.0/Sales.Compare.dll"

if [[ "$SALES_PER_CASHIER" -lt 1 ]]; then
  echo "ERROR: TOTAL/CASHIERS debe ser >= 1."
  exit 1
fi

if lsof -ti tcp:5080 >/dev/null 2>&1; then
  echo "ERROR: ya hay algo escuchando en :5080. Páralo antes de lanzar la matriz."
  exit 1
fi

echo "Compilando ($CONFIGURATION)..."
dotnet build "$ROOT/SqliteSales.sln" -c "$CONFIGURATION" -v:q

mkdir -p "$RESULTS/logs"

api_pid=""
caffeinate_pid=""

stop_api() {
  if [[ -n "${api_pid:-}" ]]; then
    kill "$api_pid" 2>/dev/null || true
  fi

  for _ in $(seq 1 80); do
    if ! lsof -ti tcp:5080 >/dev/null 2>&1; then
      api_pid=""
      return
    fi
    sleep 0.25
  done

  for pid in $(lsof -ti tcp:5080 2>/dev/null || true); do
    kill "$pid" 2>/dev/null || true
  done
  api_pid=""
}

cleanup() {
  stop_api
  if [[ -n "${caffeinate_pid:-}" ]]; then
    kill "$caffeinate_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

if command -v caffeinate >/dev/null 2>&1; then
  caffeinate -dimsu -w $$ &
  caffeinate_pid=$!
fi

CONFIGS=(
  "A|WAL|NORMAL|0"
  "B|WAL|FULL|0"
  "C|WAL|FULL|1"
  "D|DELETE|FULL|0"
)

echo
echo "Matriz: ${#CONFIGS[@]} configuraciones × $REPS repeticiones × $TOTAL ventas"
echo "Warm-up: $WARMUP ventas · cajeros: $CASHIERS · ventas/cajero: $SALES_PER_CASHIER"
echo "Resultados: $RESULTS"
echo

for entry in "${CONFIGS[@]}"; do
  IFS='|' read -r letter journal sync fullfsync <<< "$entry"
  label="$(printf '%s-%s-%s-ff%s' "$letter" "$journal" "$sync" "$fullfsync" | tr '[:upper:]' '[:lower:]')"
  out="$RESULTS/$label"
  api_log="$RESULTS/logs/$label-api.log"
  mkdir -p "$out"

  echo "=== $label · journal=$journal synchronous=$sync fullfsync=$fullfsync ==="

  SQLITE_JOURNAL_MODE="$journal" \
  SQLITE_SYNCHRONOUS="$sync" \
  SQLITE_FULLFSYNC="$fullfsync" \
  ACK_MODE=enqueue \
    dotnet "$API_DLL" \
      --contentRoot "$ROOT/src/Sales.Api" \
      --urls http://localhost:5080 \
      > "$api_log" 2>&1 &
  api_pid=$!

  healthy=0
  for _ in $(seq 1 120); do
    if curl -sf http://localhost:5080/api/health >/dev/null 2>&1; then
      healthy=1
      break
    fi
    sleep 0.5
  done

  if [[ "$healthy" -ne 1 ]]; then
    echo "ERROR: la API no arrancó. Log: $api_log"
    tail -40 "$api_log" || true
    exit 1
  fi

  curl -s http://localhost:5080/api/info > "$out/api-info.json"

  if command -v jq >/dev/null 2>&1; then
    jq -r '"  efectivo: journal=\(.pragmas.journal_mode) synchronous=\(.pragmas.synchronous) fullfsync=\(.pragmas.fullfsync)"' \
      "$out/api-info.json" || true
  fi

  for rep in $(seq 1 "$REPS"); do
    echo "  repetición $rep/$REPS"
    dotnet "$LOADTEST_DLL" \
      --cashiers "$CASHIERS" \
      --sales-per-cashier "$SALES_PER_CASHIER" \
      --warmup "$WARMUP" \
      --label "${label}-rep${rep}" \
      --output "$out"
  done

  stop_api
  echo
done

echo "=== Comparativa ==="
dotnet "$COMPARE_DLL" "$RESULTS"
echo
echo "Listo: $RESULTS/comparison.html"
