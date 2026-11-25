#!/usr/bin/env bash
#
# Todo en uno: N pasadas de 1M con la configuración ganadora y un HTML con
# cada pasada + la media. Sin prueba de caída, sin CSV y sin carpetas.
#
# Uso:
#   ./scripts/run-all.sh
#   REPS=5 TOTAL=1000000 WARMUP=100000 ./scripts/run-all.sh
#
set -euo pipefail

ulimit -n 10240 2>/dev/null || true

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPS="${REPS:-5}"
TOTAL="${TOTAL:-1000000}"
WARMUP="${WARMUP:-100000}"
CASHIERS="${CASHIERS:-200}"
CONFIGURATION="${CONFIGURATION:-Debug}"
SALES_PER_CASHIER="${SALES_PER_CASHIER:-$((TOTAL / CASHIERS))}"

API_DLL="$ROOT/src/Sales.Api/bin/$CONFIGURATION/net11.0/Sales.Api.dll"
LOADTEST_DLL="$ROOT/tools/Sales.LoadTest/bin/$CONFIGURATION/net11.0/Sales.LoadTest.dll"
COMPARE_DLL="$ROOT/tools/Sales.Compare/bin/$CONFIGURATION/net11.0/Sales.Compare.dll"
FINAL="$ROOT/results/final.html"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/sqlite-bench.XXXXXX")"
api_pid=""
caffeinate_pid=""

stop_port() {
  for pid in $(lsof -ti tcp:5080 2>/dev/null || true); do
    kill "$pid" 2>/dev/null || true
  done
}

stop_api() {
  if [[ -n "${api_pid:-}" ]]; then
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
  fi
  stop_port
  api_pid=""
}

cleanup() {
  stop_api
  if [[ -n "${caffeinate_pid:-}" ]]; then
    kill "$caffeinate_pid" 2>/dev/null || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

if command -v caffeinate >/dev/null 2>&1; then
  caffeinate -dimsu -w $$ &
  caffeinate_pid=$!
fi

echo "Parando cualquier API en :5080..."
stop_port
sleep 1

echo "Compilando ($CONFIGURATION)..."
dotnet build "$ROOT/SqliteSales.sln" -c "$CONFIGURATION" -v:q

mkdir -p "$ROOT/results"

echo "Arrancando API con la configuración ganadora..."
SQLITE_JOURNAL_MODE=WAL \
SQLITE_SYNCHRONOUS=FULL \
SQLITE_FULLFSYNC=1 \
SQLITE_CHECKPOINT_FULLFSYNC=1 \
WRITER_BATCH_SIZE=5000 \
WRITER_FLUSH_MS=10 \
ACK_MODE=commit \
  dotnet "$API_DLL" \
    --contentRoot "$ROOT/src/Sales.Api" \
    --urls http://localhost:5080 \
    > "$WORK/api.log" 2>&1 &
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
  echo "ERROR: la API no arrancó. Log:"
  tail -40 "$WORK/api.log" || true
  exit 1
fi

echo "Configuración: WAL/FULL/fullfsync ON/checkpoint ON · lote 5000 · ventana 10 ms · inflight 10 · commit"
echo "Pasadas: $REPS × $TOTAL ventas (warm-up $WARMUP)"
echo

for rep in $(seq 1 "$REPS"); do
  echo "Pasada $rep/$REPS..."
  if ! dotnet "$LOADTEST_DLL" \
        --cashiers "$CASHIERS" \
        --sales-per-cashier "$SALES_PER_CASHIER" \
        --warmup "$WARMUP" \
        --inflight 10 \
        --no-csv \
        --label "pass${rep}" \
        --output "$WORK/passes" > "$WORK/pass${rep}.log" 2>&1; then
    echo "ERROR en la pasada $rep. Últimas líneas:"
    tail -40 "$WORK/pass${rep}.log" || true
    exit 1
  fi
done

stop_api

echo
echo "Generando el HTML final..."
dotnet "$COMPARE_DLL" \
  "$WORK/passes" \
  --passes \
  --final \
  --no-links \
  --output "$FINAL"

if command -v open >/dev/null 2>&1; then
  open "$FINAL" 2>/dev/null || true
fi
