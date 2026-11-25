# SQLite Sales Benchmark

Prueba de carga de SQLite + EF Core: **100.000 ventas de 200 cajeros por defecto**, escalable a **1.000.000**, contra una API ASP.NET Core que encola en un `Channel` acotado a **10.000** y persiste en **lotes de 500** (o vuelca el lote parcial a los **200 ms** desde la primera venta).

El cliente de consola:

1. borra la base (`sales.db`, `-wal`, `-shm`) y la deja limpia,
2. lanza las ventas por HTTP con 200 workers concurrentes,
3. espera a que la cola drene,
4. verifica conteo, importes, reparto por cajero, duplicados e integridad,
5. genera un informe HTML autocontenido con gráficas SVG, tablas y ventas perdidas.

## Requisitos

- .NET SDK **11.0.100**
- Probado en macOS (APFS). Los números cambian según máquina y calentamiento.

## Arranque rápido

Terminal 1 — API:

```bash
dotnet run --project src/Sales.Api
```

Terminal 2 — prueba completa:

```bash
dotnet run --project tools/Sales.LoadTest -- \
  --cashiers 200 --sales-per-cashier 500 --warmup 2000
```

El informe queda en `results/<runId>/report.html`.

Prueba de **1.000.000** (200 cajeros × 5.000 ventas):

```bash
dotnet run --project tools/Sales.LoadTest -- \
  --cashiers 200 --sales-per-cashier 5000 --warmup 2000
```

Resultados verificados en esta máquina:

| Corrida | Ventas | Throughput | HTTP | p95 | Lotes | DB / WAL | Cola máx |
|---|---|---|---|---|---|---|---|
| 100k | 100.000 | 68.657/s | 1,46 s | 5,5 ms | 200 | 3,73 / 5,27 MB | 1.514 |
| 1M (saturación) | 1.000.000 | 54.942/s | 18,20 s | 11,3 ms | 2.000 | 35,72 / 5,33 MB | **10.000** |
| 1M (caché caliente) | 1.000.000 | 68.208/s | 14,66 s | 5,0 ms | 2.000 | 35,77 / 5,33 MB | 1.668 |

En la corrida de saturación el Channel llegó a su tope de 10.000 y el backpressure subió la latencia; en la siguiente, con la caché caliente, el escritor mantuvo el ritmo. Los informes están en `results/`.

Prueba corta de humo:

```bash
dotnet run --project tools/Sales.LoadTest -- \
  --cashiers 10 --sales-per-cashier 20 --output results/smoke
```

## Opciones del cliente

```text
--cashiers N             cajeros concurrentes (default 200)
--sales-per-cashier N    ventas por cajero (default 500)
--url URL                base de la API (default http://localhost:5080)
--output DIR             carpeta de resultados (default results)
--warmup N               ventas de calentamiento antes de medir
--timeout S              timeout HTTP por request (default 180)
--sample-ms N            muestreo de /api/stats (default 250)
--label TEXTO            etiqueta de la corrida (se añade al runId)
--inflight N             ventas concurrentes por cajero (default 1)
--no-csv                 no generar sales.csv
--no-reset               no borrar la base antes de la corrida
--open                   abrir el informe al terminar
--help                   ayuda
```

## Matriz de PRAGMAs

Compara las cuatro configuraciones (WAL/NORMAL, WAL/FULL, WAL/FULL+`fullfsync`, DELETE/FULL) con 3 repeticiones de 1M y warm-up de 100k:

```bash
./scripts/run-matrix.sh
# o ajustando la matriz:
REPS=3 WARMUP=100000 TOTAL=1000000 ./scripts/run-matrix.sh
```

El script arranca y para la API con cada configuración:

| Variable | Valores |
|---|---|
| `SQLITE_JOURNAL_MODE` | `WAL`, `DELETE` |
| `SQLITE_SYNCHRONOUS` | `OFF`, `NORMAL`, `FULL`, `EXTRA` |
| `SQLITE_FULLFSYNC` | `0`, `1` |
| `SQLITE_CHECKPOINT_FULLFSYNC` | `0`, `1` |

Genera `results/matrix/comparison.html` con la mediana de throughput, p95, p99, `SaveChanges`, cola máxima y enlaces a cada informe individual. `GET /api/info` devuelve los PRAGMAs efectivos y el HTML los muestra en la sección de configuración.

Resultados de la última matriz (1M, 3 repeticiones, mediana):

| Config | Journal | synchronous | fullfsync | Throughput | p95 | SaveChanges |
|---|---|---|---|---|---|---|
| A | WAL | NORMAL | OFF | 67.229/s | 5,3 ms | 6,9 ms |
| B | WAL | FULL | OFF | 65.566/s | 5,3 ms | 6,6 ms |
| C | WAL | FULL | ON | 44.812/s | 10,6 ms | 11,2 ms |
| D | DELETE | FULL | OFF | 54.498/s | 7,0 ms | 9,2 ms |

`FULL` sin `fullfsync` queda dentro del ruido de `NORMAL`; `fullfsync=ON` es la durabilidad estricta de macOS y cuesta ~33 % de throughput.

## Durabilidad: no perder ventas

Por defecto la API usa **`ACK_MODE=commit`**: `POST /api/sales` responde **201** solo después de que el lote esté commiteado con el `synchronous` configurado. Si el proceso muere antes, el cliente recibe error y puede reintentar; la PK `ClientSaleId` es la clave de idempotencia.

```bash
# Configuración estricta de macOS + group commit
SQLITE_SYNCHRONOUS=FULL \
SQLITE_FULLFSYNC=1 \
SQLITE_CHECKPOINT_FULLFSYNC=1 \
WRITER_BATCH_SIZE=5000 \
WRITER_FLUSH_MS=50 \
ACK_MODE=commit \
  dotnet run --project src/Sales.Api
```

| Variable | Default | Uso |
|---|---|---|
| `ACK_MODE` | `commit` | `commit` = 201 tras el commit; `enqueue` = 202 al encolar (antiguo) |
| `WRITER_BATCH_SIZE` | `500` | ventas por lote |
| `WRITER_FLUSH_MS` | `200` | ventana máxima para completar un lote |

Todo en uno (deja **solo** `results/final.html`, sin carpetas ni CSV):

```bash
./scripts/run-all.sh
```

Hace **5 pasadas de 1M** con la configuración ganadora en un directorio temporal, calcula la media y borra todo lo demás.

Resultado de la última ejecución (commit-ack, `results/final.html`):

Configuración: `WAL` + `synchronous=FULL` + `fullfsync=ON` + `checkpoint_fullfsync=ON` · lote 5.000 · ventana 10 ms · inflight 10 · `ACK_MODE=commit`.

| Pasada | Throughput | Duración | p95 | p99 | SaveChanges |
|---|---|---|---|---|---|
| 1 | 39.354/s | 25,41 s | 62,4 ms | 70,5 ms | 15,6 ms |
| 2 | 39.135/s | 25,55 s | 62,8 ms | 71,5 ms | 15,7 ms |
| 3 | 38.835/s | 25,75 s | 62,5 ms | 70,8 ms | 15,8 ms |
| 4 | 38.548/s | 25,94 s | 63,4 ms | 69,6 ms | 16,1 ms |
| 5 | 38.782/s | 25,79 s | 62,2 ms | 69,9 ms | 15,8 ms |
| **Media** | **38.931/s** | **25,69 s** | **62,6 ms** | **70,5 ms** | **15,8 ms** |
| Mediana | 38.835/s | — | 62,5 ms | 70,5 ms | 15,8 ms |

5/5 PASS, 1.000.000 filas por pasada, 0 perdidas. Dispersión 38.548–39.354/s (±1 %).

Informe publicado: [docs/benchmark.html](docs/benchmark.html).

Por separado (opcional):

```bash
./scripts/run-durable-matrix.sh   # secuencial y pipelining, ventanas 5/10/50 ms
```

## Qué se mide

- **Cliente:** requests intentados, HTTP 202, rechazadas, recuperadas, ventas perdidas, latencia p50/p95/p99/máx, throughput.
- **Servidor:** encoladas, persistidas, fallidas, descartadas, longitud de cola, lotes completos/parciales, `SaveChanges` medio/p95, espera en cola.
- **Base:** filas, IDs distintos, suma de cantidades, importe total, duplicados, `PRAGMA integrity_check`, tamaño DB/WAL.
- **Por cajero:** las ventas y el importe que le corresponden (500 en 100k, 5.000 en 1M).
- **Gráficas:** histograma de latencia, cola del Channel y throughput instantáneo.

El PASS exige que la base cuadre exactamente con lo enviado: filas = IDs distintos = suma de cantidades, importe = ventas × precio, el mismo número de ventas por cajero, `integrity_check = ok`, 0 perdidas, 0 rechazadas y 0 duplicadas. Para 100k son 100.000 filas y 500 por cajero; para 1M, 1.000.000 filas y 5.000 por cajero (importe **$19.990.000,00**).

## Estructura

```text
src/Sales.Api/          Minimal API, Channel, writer EF Core, reset y verificación
src/Sales.Contracts/    DTOs y codificación de ClientSaleId
tools/Sales.LoadTest/   Consola, cliente HTTP, verificación y HTML (SVG)
results/                Informes (ignorado por git)
```

## Configuración de la API

`src/Sales.Api/appsettings.json`:

```json
{
  "Urls": "http://localhost:5080",
  "Benchmark": {
    "CashierCount": 200,
    "UnitPriceCents": 1999
  }
}
```

La base se crea por defecto en `src/Sales.Api/bin/Debug/net11.0/data/sales.db`. Se puede cambiar con la variable de entorno `SALES_DB`.

## Detalles que conviene conocer

- SQLite admite **un solo escritor**; el `Channel` y el `BackgroundService` lo garantizan.
- La PK es `ClientSaleId = cashierId * 1.000.000 + sequence`, sin `AUTOINCREMENT`; también sirve de clave de idempotencia.
- El dinero se guarda en **céntimos enteros** (`decimal` se mapea a TEXT en SQLite).
- WAL + `synchronous=NORMAL`.
- `POST /api/admin/reset` es destructivo y solo está pensado para esta prueba.
- Si el proceso muere, las ventas en memoria se pierden (no hay cola durable).

Más contexto para agentes: [AGENTS.md](AGENTS.md).
