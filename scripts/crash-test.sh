#!/usr/bin/env bash
#
# Prueba de caída: commit-ack estricto, se mata la API con kill -9 a mitad de
# corrida, se reinicia sobre la misma base y se comprueba que ninguna venta
# confirmada con 201 falta.
#
# Uso:
#   ./scripts/crash-test.sh
#   TOTAL=1000000 CASHIERS=200 ./scripts/crash-test.sh
#
set -euo pipefail

ulimit -n 10240 2>/dev/null || true

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

TOTAL="${TOTAL:-200000}"
CASHIERS="${CASHIERS:-200}"
SALES_PER_CASHIER="${SALES_PER_CASHIER:-$((TOTAL / CASHIERS))}"
CONFIGURATION="${CONFIGURATION:-Debug}"
KILL_AFTER="${KILL_AFTER:-3}"
BATCH="${WRITER_BATCH_SIZE:-5000}"
FLUSH="${WRITER_FLUSH_MS:-10}"
INFLIGHT="${INFLIGHT:-10}"
RESULTS="${OUTPUT_DIR:-$ROOT/results/crash-test}"

API_DLL="$ROOT/src/Sales.Api/bin/$CONFIGURATION/net11.0/Sales.Api.dll"
LOADTEST_DLL="$ROOT/tools/Sales.LoadTest/bin/$CONFIGURATION/net11.0/Sales.LoadTest.dll"
API_LOG="$RESULTS/api.log"
CLIENT_LOG="$RESULTS/client.log"

if lsof -ti tcp:5080 >/dev/null 2>&1; then
  echo "ERROR: ya hay algo escuchando en :5080. Páralo antes de lanzar la prueba."
  exit 1
fi

echo "Compilando ($CONFIGURATION)..."
dotnet build "$ROOT/SqliteSales.sln" -c "$CONFIGURATION" -v:q
mkdir -p "$RESULTS"

api_pid=""
caffeinate_pid=""

stop_api() {
  if [[ -n "${api_pid:-}" ]]; then
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
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

start_api() {
  SQLITE_JOURNAL_MODE=WAL \
  SQLITE_SYNCHRONOUS=FULL \
  SQLITE_FULLFSYNC=1 \
  SQLITE_CHECKPOINT_FULLFSYNC=1 \
  WRITER_BATCH_SIZE="$BATCH" \
  WRITER_FLUSH_MS="$FLUSH" \
  ACK_MODE=commit \
    dotnet "$API_DLL" \
      --contentRoot "$ROOT/src/Sales.Api" \
      --urls http://localhost:5080 \
      >> "$API_LOG" 2>&1 &
  api_pid=$!

  for _ in $(seq 1 120); do
    if curl -sf http://localhost:5080/api/health >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.5
  done

  echo "ERROR: la API no arrancó. Log: $API_LOG"
  tail -40 "$API_LOG" || true
  return 1
}

: > "$API_LOG"
: > "$CLIENT_LOG"

echo "Arrancando API estricta (commit-ack, batch $BATCH, flush ${FLUSH}ms, inflight $INFLIGHT, fullfsync ON)..."
start_api
curl -s http://localhost:5080/api/info > "$RESULTS/api-info.json"

echo "Lanzando $TOTAL ventas y matando la API a los ${KILL_AFTER}s..."
dotnet "$LOADTEST_DLL" \
  --cashiers "$CASHIERS" \
  --sales-per-cashier "$SALES_PER_CASHIER" \
  --warmup 0 \
  --inflight "$INFLIGHT" \
  --no-csv \
  --label crash-run \
  --output "$RESULTS" \
  > "$CLIENT_LOG" 2>&1 &
client_pid=$!

sleep "$KILL_AFTER"
echo "kill -9 $api_pid"
kill -9 "$api_pid" 2>/dev/null || true
wait "$api_pid" 2>/dev/null || true
api_pid=""

for _ in $(seq 1 80); do
  if ! lsof -ti tcp:5080 >/dev/null 2>&1; then
    break
  fi
  sleep 0.25
done

echo "Reiniciando API sobre la misma base (sin reset)..."
start_api

client_exit=0
wait "$client_pid" || client_exit=$?

summary="$(find "$RESULTS" -name summary.json -type f | head -1)"
if [[ -z "$summary" ]]; then
  echo "ERROR: el cliente no generó summary.json. Log: $CLIENT_LOG"
  tail -40 "$CLIENT_LOG" || true
  exit 1
fi

echo
echo "=== Resumen de la prueba de caída ==="
jq '{pass, attempted, accepted, persisted, lost, rejected, recovered, expected:(.expected.count), verify:.verify.count, failures}' "$summary"

lost="$(jq -r '.lost' "$summary")"
accepted="$(jq -r '.accepted' "$summary")"
persisted="$(jq -r '.persisted' "$summary")"

echo
echo "Cliente: exit=$client_exit · informes: $(dirname "$summary")"
echo "Ventas con 201/202: $accepted · filas en DB: $persisted · perdidas: $lost"

if [[ "$lost" == "0" ]]; then
  echo "OK: ninguna venta confirmada se perdió tras el kill -9."
  exit 0
fi

echo "FALLO: $lost ventas confirmadas no están en la base."
exit 1
