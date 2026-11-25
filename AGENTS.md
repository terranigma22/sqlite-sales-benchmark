# AGENTS.md

Benchmark de escritura en SQLite con EF Core: **100.000 ventas de 200 cajeros por defecto**, escalable a **1.000.000** (`--sales-per-cashier 5000`) contra una API ASP.NET Core. El cliente de consola resetea la base, lanza la carga, verifica el cuadre y genera `results/<runId>/report.html`.

## Comandos

```bash
# API (debe estar arrancada; escucha en :5080)
dotnet run --project src/Sales.Api

# Prueba de 100.000 (resetea la base por defecto)
dotnet run --project tools/Sales.LoadTest -- --cashiers 200 --sales-per-cashier 500 --warmup 2000

# Prueba de 1.000.000
dotnet run --project tools/Sales.LoadTest -- --cashiers 200 --sales-per-cashier 5000 --warmup 2000

# Prueba corta de humo
dotnet run --project tools/Sales.LoadTest -- --cashiers 10 --sales-per-cashier 20 --output results/smoke

# Todo en uno: matriz durable + ganadora + prueba de caída -> results/final.html
./scripts/run-all.sh

# Matriz de PRAGMAs: 4 configuraciones × 3 reps de 1M
./scripts/run-matrix.sh

# Compilar
dotnet build SqliteSales.sln
```

`--help` lista las opciones. `--no-reset` conserva la base; `--open` abre el informe.

## Arquitectura

- `src/Sales.Api`: Minimal API + `SaleQueue` + `SalesWriterService` + `ResetService` + `VerifyService`.
- `src/Sales.Contracts`: DTOs compartidos y `ClientSaleIds` (codificación del id).
- `tools/Sales.LoadTest`: consola, cliente HTTP, verificación y generador HTML (SVG estático).

Flujo: `POST /api/sales` valida contra el catálogo en memoria y hace `await channel.Writer.WriteAsync`. El Channel es acotado a **10.000** con `FullMode.Wait`: si está lleno, el request espera (backpressure). Un único `BackgroundService` consume y guarda lotes de **500** (configurable), o vuelca el lote parcial a los **200 ms** (configurable) desde la primera venta.

Con `ACK_MODE=commit` (default) cada `SaleCommand` lleva un `TaskCompletionSource`; el endpoint espera y responde **201** solo después del `SaveChanges`. Con `enqueue` responde **202** al encolar (semántica antigua: una caída puede perder ventas en memoria).

## Decisiones que no son obvias

- **Un solo escritor SQLite.** El Channel garantiza la serialización; no añadir otro consumidor ni otra instancia de la API contra la misma base.
- **PK = `ClientSaleId`** (`cashierId * 1.000.000 + sequence`), `ValueGeneratedNever`. Es la clave de idempotencia y evita `AUTOINCREMENT` y un índice único extra.
- **`sequence` debe ser < 1.000.000.** El id codifica cajero y secuencia en un entero; 5.000 (la corrida de 1M) es seguro. El cliente lo valida.
- **Dinero en céntimos (`long`).** EF Core SQLite mapea `decimal` a TEXT.
- **Fechas en Unix ms (`long`).**
- **WAL + `synchronous=NORMAL`.** Los PRAGMAs por conexión están en `SqlitePragmaInterceptor`; `journal_mode=WAL` se aplica al crear/reiniciar la base.
- **Reset real:** `POST /api/admin/reset` pausa al escritor (`WriterGate`), espera el lote en vuelo, limpia el pool (`SqliteConnection.ClearAllPools`), borra `sales.db`, `-wal` y `-shm`, recrea y siembra. No borrar el fichero a mano con la API corriendo.
- **`/api/admin/*` es destructivo.** Solo para benchmark; no exponerlo.
- **`EnsureCreated`, no migraciones.** El `dotnet-ef` global es 9.0.6 y el proyecto usa EF Core 11; no hace falta `dotnet ef`.
- **Este directorio es su propio repo git.** El home (`~`) también es un repo con miles de ficheros; no ejecutar `git add .` desde el home.
- **`results/` está ignorado.** La base por defecto vive en `src/Sales.Api/bin/Debug/net11.0/data/sales.db`; se puede cambiar con `SALES_DB`.
- **PRAGMAs por entorno:** `SQLITE_JOURNAL_MODE`, `SQLITE_SYNCHRONOUS`, `SQLITE_FULLFSYNC`, `SQLITE_CHECKPOINT_FULLFSYNC`. `synchronous`/`fullfsync` se aplican por conexión en `SqlitePragmaInterceptor`; `journal_mode` es persistente y lo aplica `ResetService`. `GET /api/info` devuelve los valores efectivos.
- **Matriz:** `./scripts/run-matrix.sh` (REPS/WARMUP/TOTAL configurables) y `tools/Sales.Compare` genera `results/matrix/comparison.html`.
- **Commit-ack:** `ACK_MODE=commit` (default) → 201 tras el commit durable; `ACK_MODE=enqueue` → 202 al encolar. `WRITER_BATCH_SIZE` y `WRITER_FLUSH_MS` configuran el group commit.
- **Matriz durable:** `./scripts/run-durable-matrix.sh` (WAL/FULL/fullfsync ON + checkpoint ON; secuencial y pipelining). Prueba de caída: `./scripts/crash-test.sh` (kill -9, exige `lost = 0`).
- **Todo en uno:** `./scripts/run-all.sh` usa un temporal, ejecuta N pasadas de 1M con la configuración ganadora y deja solo `results/final.html` con cada pasada y la media (sin CSV ni carpetas).
- **Pipelining:** `--inflight N` mantiene N ventas concurrentes por cajero. Sin pipelining, con 200 cajeros y commit-ack, el throughput está limitado por `200 / (flush + commit)`. `--no-csv` evita el CSV.

## Verificación y PASS

El cliente exige que la base cuadre exactamente con lo enviado: filas = IDs distintos = suma de cantidades, importe = ventas × precio, el mismo número de ventas por cajero, `integrity_check = ok`, 0 perdidas, 0 rechazadas y 0 duplicadas. Para 100k son 100.000 filas y 500 por cajero; para 1M, 1.000.000 filas y 5.000 por cajero. Si algo falla, el HTML lista las ventas perdidas (cajero/secuencia/id/error) y el proceso sale con código 1.

## Resultados verificados (macOS, .NET 11.0.100)

| Corrida | Ventas | Throughput | HTTP | p95 | p99 | Lotes | SaveChanges | DB / WAL | Cola máx |
|---|---|---|---|---|---|---|---|---|---|
| 100k | 100.000 | 68.657/s | 1,46 s | 5,5 ms | 7,7 ms | 200 | 6,4 ms | 3,73 / 5,27 MB | 1.514 |
| 1M (saturación) | 1.000.000 | 54.942/s | 18,20 s | 11,3 ms | 18,6 ms | 2.000 | 8,3 ms | 35,72 / 5,33 MB | **10.000** |
| 1M (caché caliente) | 1.000.000 | 68.208/s | 14,66 s | 5,0 ms | 6,9 ms | 2.000 | 6,4 ms | 35,77 / 5,33 MB | 1.668 |

- 100k: `results/2026-11-25-110035/report.html`
- 1M saturación: `results/2026-11-25-111449/report.html`
- 1M última: `results/2026-11-25-111801/report.html`
- Verificación 1M: 1.000.000 filas / IDs distintos / unidades, importe **$19.990.000,00**, 5.000 por cajero, `integrity_check = ok`, 0 perdidas / duplicadas.
- La primera corrida de 1M llenó el Channel (10.000) y activó el backpressure; la segunda, con la caché caliente, no lo llenó. Es la variabilidad esperada: usa `--warmup` y compara varias corridas.

### Matriz de PRAGMAs (1M, 3 repeticiones, mediana)

| Config | Journal | synchronous | fullfsync | Throughput | p95 | p99 | SaveChanges | Cola máx |
|---|---|---|---|---|---|---|---|---|
| A | WAL | NORMAL | OFF | 67.229/s (65.525–68.593) | 5,3 ms | 7,6 ms | 6,9 ms | 10.000 |
| B | WAL | FULL | OFF | 65.566/s (59.576–67.607) | 5,3 ms | 7,5 ms | 6,6 ms | 10.000 |
| C | WAL | FULL | ON | 44.812/s (44.784–45.649) | 10,6 ms | 13,2 ms | 11,2 ms | 10.000 |
| D | DELETE | FULL | OFF | 54.498/s (54.498–56.209) | 7,0 ms | 10,3 ms | 9,2 ms | 10.000 |

- **B (WAL+FULL) queda dentro del ruido de A (NORMAL)**: en esta máquina `FULL` sin `fullfsync` no cuesta throughput apreciable.
- **C (`fullfsync=ON`) es la durabilidad estricta de macOS** (`F_FULLFSYNC`): ~33 % menos throughput y p95 10,6 ms.
- **D (DELETE+FULL)** es ~19 % más lento que WAL/NORMAL; no aporta ventaja.
- Comparativa: `results/matrix/comparison.html`.

### Resultado final: 5 pasadas con la ganadora (commit-ack)

`./scripts/run-all.sh` — 5 pasadas de 1M con la configuración fija, warm-up 100k, sin CSV ni carpetas. Deja **solo** `results/final.html`.

Configuración: `WAL` + `synchronous=FULL` + `fullfsync=ON` + `checkpoint_fullfsync=ON` · lote 5.000 · ventana 10 ms · inflight 10 · `ACK_MODE=commit` (201 tras el fsync).

| Pasada | Throughput | Duración | p95 | p99 | SaveChanges |
|---|---|---|---|---|---|
| 1 | 39.354/s | 25,41 s | 62,4 ms | 70,5 ms | 15,6 ms |
| 2 | 39.135/s | 25,55 s | 62,8 ms | 71,5 ms | 15,7 ms |
| 3 | 38.835/s | 25,75 s | 62,5 ms | 70,8 ms | 15,8 ms |
| 4 | 38.548/s | 25,94 s | 63,4 ms | 69,6 ms | 16,1 ms |
| 5 | 38.782/s | 25,79 s | 62,2 ms | 69,9 ms | 15,8 ms |
| **Media** | **38.931/s** | **25,69 s** | **62,6 ms** | **70,5 ms** | **15,8 ms** |
| Mediana | 38.835/s | — | 62,5 ms | 70,5 ms | 15,8 ms |

- 5/5 PASS: 1.000.000 filas por pasada, 0 perdidas.
- Dispersión baja: 38.548–39.354/s (±1 %).
- Sin pipelining, con 200 cajeros y commit-ack, el techo es ~`200 / (flush + commit)`; el pipelining es lo que recupera el throughput.

## Limitaciones

- La cola es en memoria: si el proceso muere, se pierden las ventas encoladas (no hay outbox durable).
- El reset debe ejecutarse sin carga; el cliente lo hace antes de medir.
- `Microsoft.Data.Sqlite` implementa async como síncrono; paralelizar escrituras no ayuda.
