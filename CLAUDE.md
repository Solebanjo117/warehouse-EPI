# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Este repositorio documenta en español (`README.md`, `docs/*.md`); el código y los
identificadores están en inglés. Mantén esa convención.

## Comandos

Si `dotnet` no está en `PATH`, antepone `& "C:\Program Files\dotnet\dotnet.exe"`.

```powershell
dotnet tool restore                                   # dotnet-ef 10.0.10 (herramienta local)
pwsh ./scripts/quality.ps1                            # puerta de calidad completa
dotnet run --project src\WarehouseEPI.Web             # http://localhost:5142 / https://localhost:7254
dotnet run --project src\WarehouseEPI.Web -- --create-admin   # primer ADMIN, interactivo (nunca NIP por argumento)
```

`scripts/quality.ps1` es la verificación obligatoria antes de integrar: restaura
con `--locked-mode`, `git diff --check`, `dotnet format whitespace|style|analyzers
--verify-no-changes` (excluyendo `Persistence/Migrations/**`), build Release,
`dotnet ef migrations has-pending-model-changes`, SQL idempotente y pruebas con
cobertura. `scripts/Test-Coverage.ps1` exige ≥85 % de líneas y ≥45 % de ramas
globalmente. Los resultados quedan en `artifacts/` (no versionado). El mismo
script corre en GitHub Actions (`Quality`), check requerido en `main`.

### Pruebas

```powershell
dotnet test WarehouseEPI.sln -c Release
dotnet test tests\WarehouseEPI.Tests\WarehouseEPI.Tests.csproj --filter "FullyQualifiedName~AdminRouteTests"
dotnet test tests\WarehouseEPI.Tests\WarehouseEPI.Tests.csproj --filter "DisplayName~Warehouse_map_saves"
```

- Las pruebas web (`tests/WarehouseEPI.Tests/Web`) usan `WebApplicationFactory`
  con EF InMemory: no necesitan PostgreSQL.
- Las de integración (`Inventory/PostgreSqlInventoryTests.cs`) recrean la base
  `warehouse_epi_test` y corren sin paralelismo. Toman la conexión de
  `WAREHOUSE_EPI_TEST_CONNECTION` o, si no existe, de
  `ConnectionStrings:Warehouse` en User Secrets. **Nunca apuntes
  `WAREHOUSE_EPI_TEST_CONNECTION` a `warehouseEPI`**; el fixture rechaza
  cualquier base distinta de `warehouse_epi_test`.
- `WAREHOUSE_EPI_PRODUCT_WORKBOOK` habilita una prueba opcional de lectura del
  archivo de productos.

### Migraciones

```powershell
dotnet ef migrations add NombreDescriptivo --project src\WarehouseEPI.Infrastructure --startup-project src\WarehouseEPI.Web
dotnet ef migrations script --project src\WarehouseEPI.Infrastructure --startup-project src\WarehouseEPI.Web
dotnet ef migrations list   --project src\WarehouseEPI.Infrastructure --startup-project src\WarehouseEPI.Web
```

Revisa el SQL, confirma que la base destino sea `warehouseEPI` y ten un respaldo
validado antes de `dotnet ef database update`. No renombres, edites, reformatees
ni elimines migraciones ya aplicadas.

### Release y servicio Windows

```powershell
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 -Version 0.10.7
pwsh ./scripts/release/Install-WarehouseEpiService.ps1 -PackagePath ./artifacts/releases/WarehouseEPI-0.10.7-win-x64.zip
pwsh ./scripts/release/Update-WarehouseEpiService.ps1 -PackagePath ./artifacts/releases/WarehouseEPI-0.10.8-win-x64.zip
pwsh ./scripts/release/Rollback-WarehouseEpiService.ps1 -Version 0.10.7
```

La publicación exige worktree limpio; instalación y actualización exigen
PowerShell elevado. `scripts/security/` contiene certificado LAN, rol
`warehouse_epi_app`, Data Protection, logs y respaldos; su procedimiento está en
`docs/DEVELOPMENT.md` y `docs/OPERATIONS.md`.

## Arquitectura

Aplicación local de almacén: una laptop sirve Kestrel en la LAN y tablets
Android con Chrome usan una interfaz ligera orientada a escáner.
.NET 10 (SDK fijado en `global.json`), Razor Pages, EF Core, PostgreSQL 18.

Dependencias permitidas: `Web -> Infrastructure -> Core`. `Core` no referencia a
nadie.

- **`src/WarehouseEPI.Core`** — entidades, enums y normalización
  (`CatalogNormalization`, `LocationNormalization`), sin EF ni ASP.NET.
- **`src/WarehouseEPI.Infrastructure`** — `Persistence/WarehouseDbContext` (mapeo
  explícito `snake_case`, `xmin` como `RowVersion`) y servicios por área:
  `Inventory`, `Catalogs`, `Locations`, `Labels`, `Reporting`, `Settings`,
  `Imports`, `Security`.
- **`src/WarehouseEPI.Web`** — Razor Pages (`Pages/Operations` público en LAN,
  `Pages/Admin` con cookie y política `AdminOnly`, `Pages/Reports`,
  `Pages/Inventory`), `Program.cs` como única composición de DI, `Hosting/`
  (preflight de producción y carga de `service-settings.json`),
  `Observability/`, `Security/`, `Bootstrap/`.
- **`tests/WarehouseEPI.Tests`** — carpetas espejo de las áreas de Infrastructure
  más `Web/` para contratos de rutas y HTML.

### Flujo de inventario (el núcleo del sistema)

`InventoryMovementService` es la fachada: autentica el NIP y coordina la
transacción. A su alrededor, `InventoryMovementRules` normaliza y valida,
`InventoryLotEngine` resuelve los lotes internos diarios `AUTO-YYYYMMDD` y su
consumo (las salidas consumen primero los más antiguos) e
`InventoryMovementStore` concentra los bloqueos de fila en orden estable y la
persistencia. `InventoryCorrectionService` con `InventoryReversalService`
generan reverso y, si corresponde, reemplazo. Las peticiones son idempotentes
por UUID y el comprobante se consulta con un UUID no predecible.

### Invariantes que no se deben romper

- La verdad física es `producto + ubicación + lote`. No crees un total de
  producto independiente de los saldos por ubicación; los totales se derivan.
- Movimientos y cambios de saldo son inmutables: se corrigen con reverso y
  reemplazo auditables, nunca editando ni borrando.
- Los ajustes reciben conteo final y detectan saldo desactualizado con `xmin`.
- Inventario negativo permitido con advertencia, sin bloqueo.
- Cantidades en `numeric(18,4)`.
- Las asignaciones fijas producto-ubicación no sustituyen saldos y sobreviven a
  un saldo cero.

### Seguridad

Roles `ADMIN` y `OPERATOR`; ambos operan inventario, solo ADMIN administra. El
operador no inicia sesión: cada movimiento pide NIP aunque exista cookie
administrativa. El NIP se localiza por HMAC (`PinLookup`, clave
`Security:PinLookupKey`) y se valida con PBKDF2 (`PinHash`); nunca se almacena en
texto plano. Secretos y cadenas de conexión solo en User Secrets o en
`C:\ProgramData\WarehouseEPI\Config\service-settings.json`; jamás en Git, logs,
documentación ni línea de comandos.

En `Production` la aplicación aborta el arranque si faltan la ruta absoluta de
claves de Data Protection, el thumbprint del certificado, `AllowedHosts`
explícitos o una ruta de logs escribible. `/health/live` responde solo desde
loopback; el diagnóstico completo está en `/Admin/System` (ADMIN). Los logs JSON
locales nunca incluyen NIP, cookies, formularios, query strings, secretos ni
excepciones crudas.

## Convenciones al editar

- `TreatWarningsAsErrors` con analizadores `10-recommended`. `.editorconfig`
  mantiene una baseline acotada de reglas heredadas: no la extiendas para código
  nuevo.
- Versiones centralizadas en `Directory.Packages.props` y bloqueadas en
  `packages.lock.json`. Para actualizar una dependencia:
  `dotnet restore --force-evaluate`, revisa todos los lockfiles y pasa la puerta
  de calidad.
- CSP sin `script-src 'unsafe-inline'`: nada de `<script>` inline ni atributos
  `onclick=` en `.cshtml`. El JS vive en `wwwroot/js/*.js` y las pruebas de
  contrato en `tests/WarehouseEPI.Tests/Web` lo verifican.
- Pasa `CancellationToken` en operaciones asíncronas de I/O.
- Archivos nuevos con LF; respeta `.editorconfig` y `.gitattributes`.
- Interfaz ligera: se prueba en la tablet más lenta.
- Para compilaciones aisladas (cuando la app está corriendo) usa
  `--artifacts-path artifacts\validation\...`; nunca `-p:OutputPath` relativo,
  porque MSBuild lo resuelve dentro de cada proyecto.

## Documentación

Antes de editar, lee `docs/CONTEXT.md` (estado, fases, decisiones confirmadas y
pendientes; es la fuente de continuidad) y revisa `git status` para no pisar
trabajo en curso. `docs/ARCHITECTURE.md` conserva el diseño,
`docs/DEVELOPMENT.md` el flujo técnico y `docs/OPERATIONS.md` la operación de la
laptop servidor. Las decisiones descritas como "probablemente" no son requisitos.
