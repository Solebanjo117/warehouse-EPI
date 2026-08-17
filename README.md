# Warehouse EPI

[![Quality](https://github.com/Solebanjo117/warehouse-EPI/actions/workflows/quality.yml/badge.svg)](https://github.com/Solebanjo117/warehouse-EPI/actions/workflows/quality.yml)

Sistema web local para registrar, consultar y auditar movimientos de almacén.
Una laptop ejecuta el servidor dentro de la red local y tablets Android usan
Google Chrome para las operaciones diarias.

## Estado

Las fases 1 a 9 y 10.1 estan terminadas: catalogos, usuarios por NIP,
ubicaciones, movimientos, historial, correcciones, lotes internos automaticos y
estandarizacion del repositorio. La fase 10.2 añade validaciones de calidad
reproducibles antes del rediseño visual y del piloto.

## Capacidades actuales

- Entradas, salidas, transferencias y ajustes con NIP por operacion.
- Saldos por producto, ubicacion y lote interno; los totales se derivan de esos
  saldos.
- Idempotencia por UUID, transacciones atomicas y control de concurrencia.
- Inventario negativo con advertencia, sin bloqueo.
- Catalogos de productos, codigos de barras, ubicaciones y usuarios.
- Historial inmutable, correcciones auditables y consulta publica de inventario.

## Tecnologia y estructura

- .NET SDK 10, ASP.NET Core Razor Pages y Entity Framework Core.
- PostgreSQL 18 mediante Npgsql.
- `src/WarehouseEPI.Core`: dominio y reglas.
- `src/WarehouseEPI.Infrastructure`: persistencia y servicios de inventario.
- `src/WarehouseEPI.Web`: aplicacion web.
- `tests/WarehouseEPI.Tests`: pruebas unitarias, web e integracion PostgreSQL.

Consulta [la arquitectura](docs/ARCHITECTURE.md) y la
[guia de desarrollo](docs/DEVELOPMENT.md) antes de modificar el sistema.

## Requisitos

- Windows con .NET SDK 10.0.400.
- PostgreSQL 18 accesible localmente.
- Git.

El archivo `global.json` fija el SDK esperado. Si `dotnet` no esta disponible
en `PATH`, utiliza `C:\Program Files\dotnet\dotnet.exe`.

## Inicio rapido local

1. Configura en User Secrets del proyecto web las claves
   `ConnectionStrings:Warehouse` y `Security:PinLookupKey`. Nunca incluyas sus
   valores en archivos versionados.
2. Restaura las herramientas locales:

   ```powershell
   dotnet tool restore
   ```

3. Revisa el SQL y confirma la base de destino antes de aplicar una migracion.
   El flujo seguro esta descrito en la guia de desarrollo.
4. Ejecuta la verificacion completa:

   ```powershell
   pwsh ./scripts/quality.ps1
   ```

   La verificacion crea y elimina datos solamente en `warehouse_epi_test` para
   las pruebas de integracion. Configura `WAREHOUSE_EPI_TEST_CONNECTION` solo
   si apunta exactamente a esa base; nunca apuntes ese valor a `warehouseEPI`.

5. Inicia la aplicacion:

   ```powershell
   dotnet run --project src\WarehouseEPI.Web
   ```

Durante el desarrollo, la aplicacion usa `http://localhost:5142` y
`https://localhost:7254`.

## Primer administrador

Para una base nueva, crea el primer administrador de forma interactiva. No
incluyas el NIP en la linea de comandos.

```powershell
dotnet run --project src\WarehouseEPI.Web -- --create-admin
```

## Reglas importantes

- No almacenar contraseñas, NIP ni secretos en Git, documentacion o registros.
- No modificar ni borrar movimientos confirmados; corregirlos mediante reverso
  y reemplazo auditables.
- No aplicar migraciones sin revisar SQL, confirmar la base y tener respaldo.
- No crear un saldo total de producto independiente del saldo por ubicacion.

El estado operativo, las decisiones confirmadas y el roadmap se mantienen en
[docs/CONTEXT.md](docs/CONTEXT.md).
