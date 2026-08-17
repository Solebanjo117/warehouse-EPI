# Warehouse EPI

[![Quality](https://github.com/Solebanjo117/warehouse-EPI/actions/workflows/quality.yml/badge.svg)](https://github.com/Solebanjo117/warehouse-EPI/actions/workflows/quality.yml)

Sistema web local para registrar, consultar y auditar movimientos de almacén.
Una laptop ejecuta el servidor dentro de la red local y tablets Android usan
Google Chrome para las operaciones diarias.

## Estado

Las fases 1 a 9 y 10.1 a 10.7 estan terminadas: catalogos, usuarios por NIP,
ubicaciones, movimientos, historial, correcciones, lotes internos automaticos,
calidad reproducible, seguridad, observabilidad, recuperación local y
publicación como servicio Windows antes del despliegue LAN.

## Capacidades actuales

- Entradas, salidas, transferencias y ajustes con NIP por operacion.
- Saldos por producto, ubicacion y lote interno; los totales se derivan de esos
  saldos.
- Idempotencia por UUID, transacciones atomicas y control de concurrencia.
- Inventario negativo con advertencia, sin bloqueo.
- Catalogos de productos, codigos de barras, ubicaciones y usuarios.
- Escaneo de producto y ubicación con cámara para códigos Code 128 en HTTPS,
  además del escáner físico HID y la captura manual.
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

## Preparacion de produccion LAN

La aplicacion en produccion requiere HTTPS, una CA local confiable, un anillo
persistente de Data Protection y el usuario PostgreSQL `warehouse_epi_app`.
Ejecuta los scripts de `scripts/security/` desde PowerShell elevado y sigue la
guia de desarrollo; no ejecutes certificados ni cambios de permisos contra la
base operativa sin un respaldo validado.

La configuracion de produccion debe incluir `AllowedHosts` con `warehouse-epi`
y su IP reservada, `Security:DataProtectionKeysPath` y
`Security:ServerCertificateThumbprint`. Ninguno de esos valores ni las
credenciales PostgreSQL debe versionarse.

La observabilidad permanece local: los JSON rotativos se guardan en
`C:\ProgramData\WarehouseEPI\Logs` (50 MB por archivo, 30 días). Antes de
iniciar producción, crea la ruta y limita sus ACL para la cuenta de aplicación y
administradores:

```powershell
pwsh ./scripts/security/Initialize-ObservabilityLogs.ps1
```

`/health/live` solo responde desde loopback de la laptop y no incluye detalle
de base de datos. El estado completo y sanitizado está en `/Admin/System` para
ADMIN. Los registros no incluyen query strings, NIP, cookies, formularios,
secretos ni cadenas de conexión.

## Respaldo local de PostgreSQL

La fase 10.6 conserva respaldos locales custom de PostgreSQL en
`C:\ProgramData\WarehouseEPI\Backups`: uno diario y 30 días de retención. La
restauración se valida semanalmente en una base temporal y nunca reemplaza
`warehouseEPI`. La copia externa cifrada queda pendiente.

Desde PowerShell elevado, prepara directorio, credenciales privadas y tareas:

```powershell
pwsh ./scripts/security/Initialize-WarehouseEpiBackupDirectory.ps1
pwsh ./scripts/security/Initialize-WarehouseEpiBackupCredentials.ps1
pwsh ./scripts/security/Install-WarehouseEpiBackupTasks.ps1
```

El último script registra respaldo diario a las 02:00 y validación de
restauración los domingos a las 03:00 bajo `SYSTEM`. El usuario PostgreSQL
configurado debe poder leer `warehouseEPI` y crear/eliminar bases temporales
locales para la validación. Nunca pases una contraseña en la línea de comandos.

## Release y servicio Windows

Warehouse EPI se publica como aplicación autocontenida `win-x64`. Cada Release
es inmutable, tiene versión SemVer y manifiesto SHA-256. El servicio se ejecuta
como `NT SERVICE\WarehouseEPI`; configuración, claves, logs y respaldos quedan
fuera de la carpeta publicada.

```powershell
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 -Version 0.10.7
pwsh ./scripts/release/Install-WarehouseEpiService.ps1 `
  -PackagePath ./artifacts/releases/WarehouseEPI-0.10.7-win-x64.zip
```

La publicación exige un worktree limpio. La instalación exige PowerShell
elevado y un respaldo 10.6 válido. Las actualizaciones usan
`Update-WarehouseEpiService.ps1`; si el nuevo ejecutable no supera el preflight
o `/health/live`, se reactiva automáticamente la versión anterior. Se conservan
la versión activa y dos previas.

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

## Desarrollador principal
Castilla Orta Juan Antonio
