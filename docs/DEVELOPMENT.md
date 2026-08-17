# Guia de desarrollo

## Preparacion local

1. Instala .NET SDK 10.0.400 y PostgreSQL 18.
2. Configura User Secrets para `src/WarehouseEPI.Web` con los nombres de clave
   `ConnectionStrings:Warehouse` y `Security:PinLookupKey`.
3. Nunca pongas valores de secretos en `appsettings*.json`, Markdown, Git ni la
   linea de comandos compartida.
4. Desde la raiz restaura la herramienta de Entity Framework:

   ```powershell
   dotnet tool restore
   ```

Si `dotnet` no esta en `PATH`, antepone
`& "C:\Program Files\dotnet\dotnet.exe"` a los comandos.

## Ciclo de verificacion

```powershell
pwsh ./scripts/quality.ps1
```

El comando restaura herramientas y paquetes bloqueados, revisa espacios en
blanco y formato, compila en Release, valida el modelo de migraciones, genera
SQL idempotente y ejecuta las pruebas con cobertura. Los resultados quedan en
`artifacts/`, que no se versiona. La suite incluye pruebas web, de dominio y de
PostgreSQL.

> Advertencia: las pruebas de integracion recrean la base
> `warehouse_epi_test`. Si se define `WAREHOUSE_EPI_TEST_CONNECTION`, debe
> apuntar exclusivamente a esa base. Nunca configures una conexion de pruebas
> hacia `warehouseEPI` ni otra base con datos.

La prueba opcional del archivo de productos usa
`WAREHOUSE_EPI_PRODUCT_WORKBOOK`; solo valida la lectura y no inserta productos.

## Dependencias, formato y cobertura

Las versiones de paquetes se administran desde `Directory.Packages.props` y sus
resoluciones quedan bloqueadas mediante `packages.lock.json`. La restauracion
normal usa `--locked-mode`; para actualizar una dependencia de forma deliberada
ejecuta `dotnet restore --force-evaluate`, revisa todos los lockfiles y pasa la
verificacion completa antes de integrar el cambio.

El formateador se aplica a todo el codigo manual y excluye
`src/WarehouseEPI.Infrastructure/Persistence/Migrations`. No renombres, edites
ni reformatees migraciones ya aplicadas. La puerta de calidad requiere al menos
85% de cobertura de lineas y 45% de ramas globalmente.

Los analizadores se ejecutan con advertencias como errores. `.editorconfig`
mantiene una baseline explícita y documentada solo para reglas heredadas cuya
corrección requiere una revisión funcional separada; no debe extenderse para
cambios nuevos. La fase 10.3 retiró `CA1822` de la baseline global; las
migraciones inmutables y convenciones de pruebas conservan excepciones acotadas.

En GitHub Actions, el workflow `Quality` usa una instancia PostgreSQL efimera y
solamente la base `warehouse_epi_test`. Sus artefactos incluyen resultados TRX,
cobertura Cobertura y el SQL idempotente de migraciones.

## Seguridad de produccion LAN

La primera publicacion usa HTTPS directo desde Kestrel, sin proxy ni confianza
en encabezados `X-Forwarded-*`. Antes de iniciar el servicio en produccion:

1. Reserva una IP DHCP para la laptop y registra el nombre LAN `warehouse-epi`.
2. Desde PowerShell elevado, crea la ruta de claves con ACL restringida:

   ```powershell
   pwsh ./scripts/security/Initialize-DataProtectionKeys.ps1
   ```

   Crea también el directorio local de observabilidad con ACL restringida a la
   cuenta de aplicación y administradores:

   ```powershell
   pwsh ./scripts/security/Initialize-ObservabilityLogs.ps1
   ```

3. Emite el certificado de la LAN indicando la IP reservada y un directorio
   externo y seguro para el PFX de la CA:

   ```powershell
   pwsh ./scripts/security/New-WarehouseEpiLanCertificate.ps1 `
     -ServerIpAddress 192.168.1.50 `
     -OfflineBackupDirectory E:\WarehouseEPI-CA
   ```

   El script exporta solo el certificado público `.cer` para instalarlo como
   autoridad confiable en cada tablet. Mueve el PFX cifrado fuera de la laptop
   y conserva su contraseña por separado. Renueva el certificado del servidor
   anualmente; la CA dura diez años.

4. Configura mediante User Secrets o variables protegidas:
   `AllowedHosts=warehouse-epi;192.168.1.50`,
   `Security:DataProtectionKeysPath=C:\ProgramData\WarehouseEPI\DataProtection-Keys`
   y el thumbprint informado por el script en
   `Security:ServerCertificateThumbprint`.
5. Haz un respaldo `pg_dump -Fc`, provisiona el rol de aplicación y verifica
   sus permisos sin imprimir contraseñas:

   ```powershell
   pwsh ./scripts/security/Initialize-WarehouseEpiAppRole.ps1
   pwsh ./scripts/security/Test-WarehouseEpiAppRole.ps1
   ```

   Después cambia `ConnectionStrings:Warehouse` al rol `warehouse_epi_app` en
   User Secrets. `postgres` se reserva para administración y migraciones; el
   proceso web no debe poseer permisos de esquema.

La aplicación rechaza el arranque en `Production` si faltan la ruta absoluta de
claves, certificado, hosts explícitos o una ruta de observabilidad existente y
escribible. Los JSON de observabilidad quedan en
`C:\ProgramData\WarehouseEPI\Logs`, rotan diariamente y al llegar a 50 MB, y
se conservan 30 días. Nunca incluyen NIP, cookies, formularios, query strings,
secretos ni cadenas de conexión. Cada respuesta incluye un `X-Correlation-ID`
válido, útil para relacionar la solicitud con su evento seguro.

`/health/live` comprueba solo el proceso y responde exclusivamente a loopback;
desde la LAN devuelve 404. No ejecuta migraciones ni escrituras. Para un
diagnóstico local, ADMIN consulta `/Admin/System`, que muestra salud y latencia
de PostgreSQL, uptime, versión, conteos agregados de movimientos de 24 horas y
fallas sanitizadas sin detalles de usuarios, SKU, NIP o excepciones.

Las cookies de producción
usan los prefijos `__Host-`, HTTPS obligatorio y `SameSite=Strict`. Los POST se
limitan por IP: login ADMIN 5 cada 5 minutos, administración 10 por minuto y
operaciones 30 por minuto; no existe bloqueo por usuario ni NIP.

## Respaldo y recuperación local

La fase 10.6 usa `pg_dump` en formato custom para `warehouseEPI`, valida cada
archivo con `pg_restore --list` antes de publicarlo y elimina solo respaldos
propios con más de 30 días. La ruta es
`C:\ProgramData\WarehouseEPI\Backups`, fuera del repositorio y con ACL
restringida. No copies manualmente un archivo de respaldo a Git ni a una carpeta
compartida sin cifrado.

En PowerShell elevado instala el directorio, archivo `PGPASSFILE` y tareas:

```powershell
pwsh ./scripts/security/Initialize-WarehouseEpiBackupDirectory.ps1
pwsh ./scripts/security/Initialize-WarehouseEpiBackupCredentials.ps1
pwsh ./scripts/security/Install-WarehouseEpiBackupTasks.ps1
```

`Initialize-WarehouseEpiBackupCredentials.ps1` solicita la contraseña de forma
interactiva y la guarda con ACL privada; no acepta ni imprime la contraseña. La
tarea diaria usa `Invoke-WarehouseEpiBackup.ps1` a las 02:00. Los domingos a las
03:00 se toma el último respaldo y se restaura en una base temporal con prefijo
`warehouse_epi_restore_validation_`; se valida que tenga tablas `public` y se
elimina incluso si la restauración falla. Nunca ejecutes restauración sobre
`warehouseEPI`.

Para comprobar el proceso manualmente, primero ejecuta el respaldo y después:

```powershell
pwsh ./scripts/security/Invoke-WarehouseEpiBackup.ps1
pwsh ./scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1
```

La copia externa cifrada queda pendiente de una decisión de medio físico o
recurso de red; mientras tanto, el respaldo local no sustituye un plan de
recuperación ante pérdida total de la laptop.

## Release versionada y servicio Windows

La publicación de producción es autocontenida para `win-x64`, no single-file.
Debe ejecutarse desde un commit con worktree limpio; la versión se incorpora al
ensamblado y aparece en `/Admin/System`:

```powershell
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 -Version 0.10.7
```

El resultado queda en `artifacts/releases/` como ZIP, manifiesto interno y
SHA-256 externo. No edites el contenido publicado. Antes de la primera
instalación debe existir un respaldo 10.6 validado. Desde PowerShell elevado:

```powershell
pwsh ./scripts/release/Install-WarehouseEpiService.ps1 `
  -PackagePath ./artifacts/releases/WarehouseEPI-0.10.7-win-x64.zip
```

El instalador migra los User Secrets actuales en memoria a
`C:\ProgramData\WarehouseEPI\Config\service-settings.json` y restringe sus ACL;
no imprime valores. Después crea el servicio `WarehouseEPI` con inicio
automático retrasado y cuenta virtual `NT SERVICE\WarehouseEPI`. Esa cuenta solo
puede leer la Release/configuración/certificado y modificar Data Protection y
logs. PostgreSQL y los respaldos permanecen separados.

Antes de instalar por primera vez, detén cualquier `dotnet run` de Warehouse
EPI para liberar los puertos 80 y 443. El servicio fija explícitamente el
`contentRoot` a su carpeta versionada, por lo que no depende del directorio de
trabajo de Windows.

Para actualizar o volver a una versión instalada:

```powershell
pwsh ./scripts/release/Update-WarehouseEpiService.ps1 `
  -PackagePath ./artifacts/releases/WarehouseEPI-0.10.8-win-x64.zip
pwsh ./scripts/release/Rollback-WarehouseEpiService.ps1 -Version 0.10.7
```

Los scripts verifican SHA-256, rutas y reparse points; ejecutan
`--validate-production` sin escuchar puertos, escribir datos ni aplicar
migraciones. Tras cambiar el ejecutable esperan el servicio y comprueban
`https://127.0.0.1/health/live`. Si falla, restauran el ejecutable anterior. Se
mantienen la versión activa y dos anteriores. El simulacro operativo completo
de reinicio/rollback pertenece a la fase 10.8.

El escáner por cámara de las operaciones lee únicamente Code 128 mediante una
copia local de ZXing Browser con licencia MIT. Requiere HTTPS en la tablet; por
HTTP el botón informa esta condición y se conserva la captura manual y el
escáner HID. Al leer un código válido usa la misma resolución que la tecla Enter.

## Migraciones

Antes de crear o aplicar una migracion, confirma que la base operativa sea
`warehouseEPI` y que no haya cambios ajenos en el arbol de trabajo.

```powershell
dotnet tool restore
dotnet ef migrations add NombreDescriptivo `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web

dotnet ef migrations script `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web
```

Revisa el SQL generado, realiza un respaldo validable y confirma el destino
antes de ejecutar `dotnet ef database update`. Despues, audita esquema, indices,
restricciones y datos esperados; luego compila, prueba y revisa el diff.

No renombres, edites ni elimines migraciones que ya se hayan aplicado en
`warehouseEPI`.

## Convenciones

- Respeta `.editorconfig` y `.gitattributes`; los archivos nuevos usan LF.
- Mantiene la primera interfaz ligera para tablets lentas.
- Pasa `CancellationToken` en operaciones asincronas de I/O.
- Preserva la inmutabilidad de movimientos confirmados.
- Usa `numeric(18,4)` para cantidades y no agregues totales independientes.
- Antes de editar, lee `docs/CONTEXT.md`, revisa `git status` y comprende los
  cambios existentes.

## Comandos utiles

```powershell
dotnet run --project src\WarehouseEPI.Web
dotnet run --project src\WarehouseEPI.Web -- --create-admin
dotnet ef migrations list --project src\WarehouseEPI.Infrastructure --startup-project src\WarehouseEPI.Web
```

El comando de creacion de administrador solicita datos de forma interactiva; no
debe recibir ni imprimir un NIP como argumento.
