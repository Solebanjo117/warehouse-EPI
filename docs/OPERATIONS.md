# Manual operativo de Warehouse EPI

Este manual describe la operación de la instalación local de Warehouse EPI en
la laptop servidor. Está dirigido a la persona responsable de la laptop y no
debe contener ni solicitar contraseñas, NIP, cadenas de conexión o archivos de
configuración protegidos.

## Estado instalado

Al 18 de agosto de 2026 la instancia fue activada con estos elementos:

- Servicio Windows: `WarehouseEPI`, con inicio automático y cuenta
  `NT SERVICE\WarehouseEPI`.
- Release activa: `0.10.7`, instalada en
  `C:\ProgramData\WarehouseEPI\Releases\0.10.7`.
- La IP LAN reservada actual es `192.168.6.68`. Al 18 de agosto de 2026 se
  validó la instalación en la IP anterior `192.168.5.192`; la nueva IP debe
  validarse después de renovar el certificado y reiniciar el servicio.
- Configuración protegida:
  `C:\ProgramData\WarehouseEPI\Config\service-settings.json`.
- Registros locales: `C:\ProgramData\WarehouseEPI\Logs`.
- Respaldo inicial validado en
  `C:\ProgramData\WarehouseEPI\Backups`.

Las tareas de respaldo diario y validación semanal están **desactivadas por
decisión operativa actual**. Los archivos existentes y las credenciales
protegidas no se borraron.

## Uso diario

1. En la tablet abre `https://192.168.6.68/`.
2. Si el navegador advierte sobre el certificado, instala primero el
   certificado público de la CA local; no omitas permanentemente la advertencia.
3. Para salud, versión, actividad agregada y fallas sanitizadas entra como ADMIN
   a `/Admin/System`.
4. La ruta `/health/live` es interna: solo responde desde la propia laptop y
   no es una página de diagnóstico para tablets.

## Comprobar y reiniciar el servicio

Abre PowerShell **como administrador**:

```powershell
Get-Service WarehouseEPI
Restart-Service WarehouseEPI
Get-Service WarehouseEPI
```

Reiniciar el servicio no recompila el proyecto, no ejecuta `dotnet publish`, no
aplica migraciones y no modifica la base de datos. Solo vuelve a iniciar el
ejecutable ya publicado de la Release activa. Habrá una interrupción breve de
acceso mientras vuelve a iniciar.

Para comprobarlo localmente, usa un host permitido y loopback:

```powershell
curl.exe --silent --fail --insecure --header 'Host: warehouse-epi' `
  https://127.0.0.1/health/live
```

El resultado correcto es HTTP 200. Después confirma desde una tablet que la
página raíz carga por HTTPS.

## Cambio de IP LAN a `192.168.6.68`

La IP está incluida en el certificado HTTPS y en `AllowedHosts`; por eso no
basta con cambiar la reserva DHCP. Una vez que `192.168.6.68` esté reservada
para la laptop, abre PowerShell **como administrador** en el repositorio y
renueva el certificado usando el respaldo PFX privado de la CA:

```powershell
pwsh ./scripts/security/Renew-WarehouseEpiLanCertificate.ps1 `
  -ServerIpAddress 192.168.7.10 `
  -CaPfxPath C:\WarehouseEPI-CA\warehouse-epi-local-ca.pfx
Restart-Service WarehouseEPI
```

El script solicita la contraseña de la CA sin mostrarla, genera e instala el
certificado para `warehouse-epi` y `192.168.6.68`, y actualiza de forma
protegida `AllowedHosts` y la huella del certificado. No edites
`service-settings.json` ni User Secrets manualmente. Si el respaldo PFX está
en otra ubicación, sustituye únicamente el valor de `-CaPfxPath`.

Después, confirma `Get-Service WarehouseEPI`, el health local de la sección
anterior y que una tablet abre `https://192.168.6.68/` sin error de nombre o
dirección del certificado.

### Si el servicio no inicia

No abras `dotnet run` junto con el servicio. Ambos intentan usar los puertos
80 y 443. Comprueba qué proceso los ocupa:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 80,443 |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

Si corresponde a una instancia manual de Warehouse EPI, ciérrala y vuelve a
iniciar el servicio. No finalices procesos que no hayas identificado. Revisa
también `/Admin/System`, los JSON de `C:\ProgramData\WarehouseEPI\Logs` y el
Visor de eventos de Windows, registro **Application**, para `WarehouseEPI.Web`
o `.NET Runtime`.

## Respaldos y recuperación

La instalación conserva estos recursos protegidos:

- Respaldos: `C:\ProgramData\WarehouseEPI\Backups`.
- Credencial de respaldo: `C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass`.
- Tareas: `\WarehouseEPI\WarehouseEPI-DailyBackup` y
  `\WarehouseEPI\WarehouseEPI-WeeklyRestoreValidation`.

Comprueba su estado desde PowerShell elevado:

```powershell
Get-ScheduledTask -TaskPath '\WarehouseEPI\' |
  Select-Object TaskName, State
```

Para reactivar la protección programada:

```powershell
Enable-ScheduledTask -TaskPath '\WarehouseEPI\' -TaskName 'WarehouseEPI-DailyBackup'
Enable-ScheduledTask -TaskPath '\WarehouseEPI\' -TaskName 'WarehouseEPI-WeeklyRestoreValidation'
```

Para desactivarla de nuevo sin borrar nada:

```powershell
Disable-ScheduledTask -TaskPath '\WarehouseEPI\' -TaskName 'WarehouseEPI-DailyBackup'
Disable-ScheduledTask -TaskPath '\WarehouseEPI\' -TaskName 'WarehouseEPI-WeeklyRestoreValidation'
```

Cuando estén activas, la primera tarea crea un respaldo custom diario a las
02:00; la segunda restaura el respaldo más reciente los domingos a las 03:00
en una base temporal y después la elimina. Nunca apuntes una restauración a
`warehouseEPI` sin un procedimiento de recuperación aprobado.

Desde la fase 11.9.4 cada `warehouseEPI-<fecha>.dump` se acompaña de
`warehouseEPI-<fecha>-references.zip`. El ZIP contiene los fondos del croquis y
un manifiesto SHA-256; ambos archivos forman una sola unidad de recuperación.
No copies, retengas ni restaures uno sin el otro cuando la base tenga registros
en `warehouse_map_reference_images`. Los originales permanecen en
`C:\ProgramData\WarehouseEPI\WarehouseMapReferences`, fuera de las Releases y
con acceso restringido a Administradores, SYSTEM y el servicio.

Para validar manualmente el flujo aislado:

```powershell
pwsh ./scripts/security/Invoke-WarehouseEpiBackup.ps1
pwsh ./scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1
```

Ejecuta ambos comandos como administrador. No pases contraseñas por parámetros,
archivos versionados, consola compartida ni documentación.

### Respaldo portátil para cambiar de laptop

Durante una ventana de corte, cuando ya no se admitan nuevos movimientos, crea
el paquete final directamente en un USB cifrado:

```powershell
pwsh ./scripts/security/New-WarehouseEpiMigrationBackup.ps1 `
  -DestinationDirectory E:\WarehouseEPI-Transfer
```

El script crea un respaldo nuevo, valida `pg_restore --list`, lo restaura en
una base temporal, incluye el ZIP pareado de fondos y los archivos válidos de
branding, y publica `WarehouseEPI-migration-<fecha>.zip` con su `.sha256`.
No contiene `service-settings.json`, contraseñas, la CA privada ni
`Security:PinLookupKey`.

En la laptop nueva, después de instalar PostgreSQL, crear el `PGPASSFILE`
protegido y transferir los secretos por separado:

```powershell
pwsh ./scripts/security/Test-WarehouseEpiMigrationBackup.ps1 `
  -PackagePath E:\WarehouseEPI-Transfer\WarehouseEPI-migration-<fecha>.zip `
  -RequireExternalHash
pwsh ./scripts/security/Restore-WarehouseEpiMigrationBackup.ps1 `
  -PackagePath E:\WarehouseEPI-Transfer\WarehouseEPI-migration-<fecha>.zip
```

La restauración solicita confirmación y se niega a reemplazar una base
`warehouseEPI` existente o directorios de referencias/branding no vacíos. Sigue
después `docs/INSTALLATION_NEW_LAPTOP.md` para configurar NIP, HTTPS, rol mínimo,
servicio y validación física.

### Runbook pendiente: migración de conteos cíclicos de Fase 13.5

Este procedimiento queda preparado, pero no autoriza por sí mismo una
intervención productiva. Ejecútalo únicamente durante una ventana aprobada y
con una identidad de migración; el rol mínimo de la aplicación no debe recibir
permisos DDL.

1. Crea y valida un respaldo antes de tocar el esquema:

   ```powershell
   pwsh ./scripts/security/Invoke-WarehouseEpiBackup.ps1
   pwsh ./scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1
   ```

2. Genera un SQL acotado desde la última migración previa y revísalo sin
   aplicarlo:

   ```powershell
   dotnet ef migrations script 20260819150547_WipProductionFlow 20260821120408_Phase135CycleCounts --idempotent --project ./src/WarehouseEPI.Infrastructure/WarehouseEPI.Infrastructure.csproj --startup-project ./src/WarehouseEPI.Web/WarehouseEPI.Web.csproj --configuration Release --output ./artifacts/phase-13-5-cycle-counts.sql
   ```

   Confirma que crea únicamente las cinco tablas de conteos cíclicos, sus
   restricciones e índices y la ampliación del propósito de movimientos. Si el
   SQL intenta retirar tablas, saldos o historial, cancela la intervención.

3. Solo después de aprobar respaldo y SQL, aplica el destino explícito con la
   configuración productiva protegida:

   ```powershell
   dotnet ef database update 20260821120408_Phase135CycleCounts --project ./src/WarehouseEPI.Infrastructure/WarehouseEPI.Infrastructure.csproj --startup-project ./src/WarehouseEPI.Web/WarehouseEPI.Web.csproj --configuration Release
   ```

4. Audita `__EFMigrationsHistory`, existencia de las cinco tablas, restricciones
   e índices; después valida creación, conteo ciego, reconteo, conciliación sin
   ajuste y aprobación autorizada de una diferencia. Registra por separado la
   prueba en laptop LAN, tablet, lector HID/cámara e impresora.

No inicies, detengas ni reemplaces el servicio como efecto colateral de estos
comandos. Si la Release activa requiere actualización, utiliza el procedimiento
de publicación y reversión de la sección siguiente.

## Publicar, actualizar y revertir una Release

Una Release es una carpeta inmutable y no se compila dentro de la instancia
activa. Antes de publicar, confirma que el worktree está limpio y elige una
versión SemVer nueva:

```powershell
git status --short
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 -Version 0.10.8
```

El paquete y su SHA-256 quedan en `artifacts/releases/`. Conserva ambos juntos.
Para actualizar, desde PowerShell elevado:

```powershell
pwsh ./scripts/release/Update-WarehouseEpiService.ps1 `
  -PackagePath ./artifacts/releases/WarehouseEPI-0.10.8-win-x64.zip
```

El script verifica integridad, configuración, certificado, directorios y
PostgreSQL antes de tocar el servicio. Después activa la nueva Release y
comprueba el health local. No ejecuta migraciones. Si falla el inicio o el
health check, restaura automáticamente la versión anterior.

Para volver deliberadamente a una versión instalada:

```powershell
pwsh ./scripts/release/Rollback-WarehouseEpiService.ps1 -Version 0.10.7
```

El servicio conserva la Release activa y dos previas. No borres manualmente
carpetas dentro de `C:\ProgramData\WarehouseEPI\Releases`.

## Checklist después de cambios operativos

1. `Get-Service WarehouseEPI` informa `Running`.
2. El health local devuelve HTTP 200.
3. Una tablet abre la página raíz por HTTPS en la IP reservada.
4. `/Admin/System` muestra la versión esperada y PostgreSQL sano.
5. Si los respaldos están habilitados, las dos tareas aparecen `Ready` y existe
   al menos un `.dump` validado.
6. Registra fecha, versión, resultado y cualquier error sanitizado; nunca copies
   secretos, NIP, SKU ni datos de usuarios en ese registro.

## Límites de seguridad

- No modifiques ACL, certificados, Data Protection ni
  `service-settings.json` manualmente.
- No expongas PostgreSQL a Internet.
- No ejecutes migraciones durante instalación, actualización, rollback o
  reinicio del servicio.
- El respaldo actual es local; todavía no sustituye una copia externa cifrada
  frente a pérdida total de la laptop.

## Flujo operativo WIP

1. En **Salida**, elige **Surtir WIP**, escanea producto y rack, selecciona
   `WIP-2`, `WIP-3` o `WIP-4`, captura cantidad y confirma con NIP. Se crea una
   transferencia: el rack disminuye, WIP aumenta y los lotes se conservan.
2. En **Procesar WIP**, elige consumo, regreso a bodega o proveedor. La operación
   usa el saldo acumulado del producto en WIP y no pide un surtimiento original.
   Sólo la devolución a proveedor exige referencia.
3. Consulta `/Reports/Wip` para ver existencias actuales, actividad efectiva y,
   en una sección separada, el consumo asumido del modelo legado.
4. Una corrección ADMIN genera reverso y reemplazo auditables. Las disposiciones
   históricas permanecen visibles y corregibles, pero no determinan el saldo.

Antes del primer despliegue WIP:

1. Confirma base `warehouseEPI`, esquema `public` y revisa
   `artifacts/wip/WipProductionFlow.sql`.
2. Ejecuta y valida `pg_dump -Fc`; no apliques la migración sin un respaldo legible.
3. Verifica la migración incremental, confirma que no inventa saldo WIP y aplica
   la Release sólo dentro de una ventana sin movimientos WIP.
4. Crea la campaña ciega **Saldo inicial WIP**, cuenta físicamente WIP-2/3/4 y
   aprueba las diferencias; ese conteo será el único saldo inicial.
5. Audita saldos, lotes, movimientos, reporte y alerta; después prueba en tablet,
   cámara y lector antes de reanudar la operación.

Estado del modelo legado: aplicado el 19 de agosto de 2026 con respaldo validado
`BackupDatabase/public-before-wip-production-flow-20260819-154603.dump`. El
nuevo control de saldo WIP sigue pendiente de despliegue autorizado y conteo físico.
