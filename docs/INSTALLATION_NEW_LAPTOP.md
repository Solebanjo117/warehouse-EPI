# Implementación de Warehouse EPI en otra laptop

Este runbook instala Warehouse EPI en una laptop Windows nueva, desde la
preparación del código hasta su operación como servicio local HTTPS. Incluye
dos rutas de base de datos:

- **Migración:** mover la instalación y los datos reales de la laptop anterior.
- **Instalación limpia:** crear una base vacía y un primer administrador.

No mezcles ambas rutas. Los comandos se ejecutan en PowerShell. Cuando una
sección diga **PowerShell como administrador**, abre una terminal nueva con
**Ejecutar como administrador**.

## 1. Datos que deben decidirse antes de comenzar

Sustituye estos ejemplos por los valores reales y conserva el registro de la
instalación fuera del repositorio:

```powershell
$deployBranch = 'main'                 # Rama confirmada y enviada a GitHub.
$serverIp = '192.168.1.50'             # IP reservada para la laptop nueva.
$releaseVersion = '0.10.8'             # Versión SemVer nueva y no publicada antes.
$repositoryRoot = 'C:\WarehouseEPI\Source'
$transferRoot = 'E:\WarehouseEPI-Transfer' # USB cifrado o medio seguro.
```

Las variables viven solamente en la terminal donde se definieron. Vuelve a
ejecutar este bloque cada vez que abras una terminal normal o elevada nueva.

Confirma además:

- Windows 11 de 64 bits, actualizado, con red LAN marcada como privada.
- Nombre LAN `warehouse-epi` y una reserva DHCP para `$serverIp`.
- Rama y commit exactos que se instalarán.
- Contraseña administrativa nueva de PostgreSQL.
- Si se migran datos: respaldo `.dump`, ZIP pareado de referencias, archivos de
  branding, PFX de la CA local y su contraseña, y la clave
  `Security:PinLookupKey` de la instalación anterior.
- Una ventana aprobada para el corte si la laptop anterior está en producción.

> `Security:PinLookupKey` no es reemplazable al migrar una base existente. Los
> NIP almacenados dependen de esa clave. Transfiérela mediante un gestor de
> secretos o medio cifrado; nunca por Git, correo, chat o este documento.

## 2. Preparar la laptop anterior

### 2.1 Confirmar que el código recuperable está en GitHub

Desde el repositorio actual:

```powershell
git status --short --branch
git remote -v
git branch -vv
git diff --check
```

Revisa cada cambio antes de confirmarlo. No uses `git add .` sin inspección y
no incluyas secretos, respaldos, artefactos o configuración de `ProgramData`.

```powershell
git add <archivo1> <archivo2>
git diff --cached
git commit -m "Preparar versión para nueva laptop"
git push -u origin <rama-confirmada>
git status --short --branch
git rev-parse HEAD
```

La última salida debe identificar el commit que se clonará. Un archivo local
sin commit y sin `push` no aparecerá en la laptop nueva.

### 2.2 Crear y validar el respaldo operativo

En **PowerShell como administrador**, desde el repositorio:

```powershell
pwsh ./scripts/security/New-WarehouseEpiMigrationBackup.ps1 `
  -DestinationDirectory $transferRoot
```

El comando crea un respaldo nuevo, valida una restauración en una base temporal
y genera dos archivos en el medio cifrado:

```text
WarehouseEPI-migration-<fecha>.zip
WarehouseEPI-migration-<fecha>.zip.sha256
```

El paquete integra base, fondos del croquis y branding. Detén el procedimiento
si falla cualquiera de sus dos validaciones. Conserva el ZIP y su `.sha256`
juntos en un USB cifrado.

No copies `service-settings.json` como método de instalación. La laptop nueva
creará su propia configuración protegida. Conserva y transfiere por separado:

- el valor anterior de `Security:PinLookupKey` si se moverá la base real;
- `warehouse-epi-local-ca.pfx` y su contraseña si las tablets deben seguir
  confiando en la misma CA;
- únicamente el certificado público `.cer` para los dispositivos cliente.

## 3. Instalar herramientas en la laptop nueva

### 3.1 Instalar o habilitar WinGet

En **PowerShell como administrador**:

```powershell
winget --version
```

Si no existe, instala o actualiza **App Installer** desde Microsoft Store y
abre una terminal nueva. No descargues ejecutables desde sitios de terceros.

### 3.2 Instalar Git, PowerShell 7 y .NET SDK 10

```powershell
winget install --id Git.Git -e --source winget `
  --accept-package-agreements --accept-source-agreements
winget install --id Microsoft.PowerShell -e --source winget `
  --accept-package-agreements --accept-source-agreements
winget install --id Microsoft.DotNet.SDK.10 -e --source winget `
  --accept-package-agreements --accept-source-agreements
```

El SDK incluye el runtime necesario para compilar. `global.json` exige la banda
`10.0.400` y permite su último parche. Si WinGet instala otra banda y
`dotnet --version` no satisface `global.json`, descarga el instalador x64 exacto
de .NET 10 desde <https://dotnet.microsoft.com/download/dotnet/10.0>.

### 3.3 Instalar PostgreSQL 18

Descarga el instalador Windows x64 enlazado por el sitio oficial:
<https://www.postgresql.org/download/windows/>. Durante el asistente:

1. Instala PostgreSQL Server y Command Line Tools; pgAdmin es opcional.
2. Conserva el puerto `5432`.
3. Define una contraseña fuerte y nueva para `postgres`.
4. Usa la configuración regional adecuada del sistema.
5. No expongas el puerto 5432 a Internet ni a la LAN.

Los scripts del repositorio esperan esta ruta:

```text
C:\Program Files\PostgreSQL\18\bin
```

Si el instalador no la agregó al `PATH`, en **PowerShell como administrador**:

```powershell
$postgresBin = 'C:\Program Files\PostgreSQL\18\bin'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (($machinePath -split ';') -notcontains $postgresBin) {
  [Environment]::SetEnvironmentVariable(
    'Path', "$machinePath;$postgresBin", 'Machine')
}
```

Cierra la terminal y abre una nueva. Verifica todo:

```powershell
git --version
pwsh --version
dotnet --version
dotnet --info
psql --version
& 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe' --version
Get-Service 'postgresql*'
```

## 4. Clonar exactamente el código que se instalará

Abre PowerShell normal:

```powershell
New-Item -ItemType Directory -Force -Path (Split-Path $repositoryRoot) | Out-Null
git clone --branch $deployBranch `
  https://github.com/Solebanjo117/warehouse-EPI.git `
  $repositoryRoot
Set-Location $repositoryRoot
git fetch --all --prune
git status --short --branch
git rev-parse HEAD
git log -1 --oneline
```

Compara el hash con el registrado en la laptop anterior. Si no coincide, no
continúes hasta elegir explícitamente la rama o el commit correcto. Para fijar
un commit ya publicado:

```powershell
git switch --detach <hash-confirmado>
git rev-parse HEAD
```

Lee la documentación de continuidad antes de operar la instalación:

```powershell
Get-Content -Raw README.md
Get-Content -Raw docs\ARCHITECTURE.md
Get-Content -Raw docs\DEVELOPMENT.md
Get-Content -Raw docs\CONTEXT.md
```

## 5. Restaurar herramientas y compilar

```powershell
dotnet tool restore
dotnet restore WarehouseEPI.sln --locked-mode
dotnet build WarehouseEPI.sln --configuration Release --no-restore
```

No ejecutes todavía `quality.ps1` contra credenciales del rol mínimo. La suite
PostgreSQL recrea exclusivamente `warehouse_epi_test` y requiere un usuario con
permiso para crear y eliminar esa base. Se configura después de guardar
temporalmente la conexión administrativa.

## 6. Configurar secretos de desarrollo de forma local

El proyecto ya tiene `UserSecretsId`; no edites `appsettings.json`. El siguiente
bloque pide la contraseña sin mostrarla ni escribirla literalmente en el
historial. Para una migración, introduce también la **misma** PinLookupKey de la
laptop anterior. Para una instalación limpia, el bloque genera una nueva.

```powershell
$project = 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj'
$databasePasswordSecure = Read-Host -AsSecureString 'Contraseña de postgres'
$databasePasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
  $databasePasswordSecure)
try {
  $databasePassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    $databasePasswordPointer)
  $escapedPassword = $databasePassword.Replace('"', '""')
  $adminConnection = "Host=localhost;Port=5432;Database=warehouseEPI;Username=postgres;Password=`"$escapedPassword`""
  dotnet user-secrets set 'ConnectionStrings:Warehouse' $adminConnection `
    --project $project
}
finally {
  $databasePassword = $null
  $escapedPassword = $null
  $adminConnection = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($databasePasswordPointer)
}
```

Elige solo uno de los bloques siguientes.

**Migración de datos existentes:**

```powershell
$pinSecure = Read-Host -AsSecureString 'Security:PinLookupKey anterior'
$pinPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pinSecure)
try {
  $pinLookupKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pinPointer)
  dotnet user-secrets set 'Security:PinLookupKey' $pinLookupKey --project $project
}
finally {
  $pinLookupKey = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pinPointer)
}
```

**Base completamente nueva:**

```powershell
$pinLookupKey = [Convert]::ToBase64String(
  [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
try {
  dotnet user-secrets set 'Security:PinLookupKey' $pinLookupKey --project $project
}
finally {
  $pinLookupKey = $null
}
```

Comprueba solamente los nombres, sin ejecutar `dotnet user-secrets list`, que
también mostraría sus valores:

```powershell
$secretsPath = Join-Path $env:APPDATA `
  'Microsoft\UserSecrets\65a21f3a-aca9-4d20-9174-cb80f10d706a\secrets.json'
(Get-Content -Raw $secretsPath | ConvertFrom-Json).PSObject.Properties.Name |
  Sort-Object
```

## 7A. Ruta de migración: restaurar la base real

La restauración en `warehouseEPI` debe usar el respaldo **final**, tomado y
validado después de impedir nuevos movimientos en la laptop anterior. Si aún
no empieza el corte, ejecuta solo la validación temporal de 7A.1 y pospón 7A.2.
No uses un respaldo preliminar como estado productivo mientras la laptop
anterior sigue recibiendo operaciones.

### 7A.1 Preparar credenciales y validar el respaldo

En **PowerShell como administrador**, desde el repositorio:

```powershell
pwsh ./scripts/security/Initialize-WarehouseEpiBackupDirectory.ps1
pwsh ./scripts/security/Initialize-WarehouseEpiBackupCredentials.ps1
```

Copia el paquete y su `.sha256` desde el medio seguro. Compara primero el hash
externo y después valida cada componente del ZIP:

```powershell
$migrationPackage = Join-Path $transferRoot `
  'WarehouseEPI-migration-<fecha>.zip'
Get-Content "$migrationPackage.sha256"
Get-FileHash $migrationPackage -Algorithm SHA256
pwsh ./scripts/security/Test-WarehouseEpiMigrationBackup.ps1 `
  -PackagePath $migrationPackage -RequireExternalHash
```

La validación comprueba manifiesto, rutas, tamaños y SHA-256 sin restaurar aún
la base productiva.

### 7A.2 Restaurar en una base nueva

El restaurador vuelve a validar el paquete y hace una restauración temporal
antes de crear la base definitiva. Se detiene si `warehouseEPI` ya existe o si
los directorios externos no están vacíos. No elimines datos para forzarlo.

```powershell
pwsh ./scripts/security/Restore-WarehouseEpiMigrationBackup.ps1 `
  -PackagePath $migrationPackage
```

### 7A.3 Restaurar archivos externos

El mismo restaurador extrae únicamente los fondos y logos declarados y
validados por el manifiesto. No se requiere una copia manual adicional.

### 7A.4 Auditar la restauración y las migraciones

```powershell
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
& $psql --host=localhost --port=5432 --username=postgres `
  --dbname=warehouseEPI --command='SELECT current_database(), current_schema();'
& $psql --host=localhost --port=5432 --username=postgres `
  --dbname=warehouseEPI --command='SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id;'
dotnet ef migrations list `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web
dotnet ef migrations has-pending-model-changes `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web `
  --configuration Release
```

Si el código contiene migraciones posteriores al respaldo, genera primero SQL
idempotente y revísalo:

```powershell
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
dotnet ef migrations script --idempotent `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web `
  --configuration Release `
  --output artifacts\new-laptop-migrations.sql
notepad artifacts\new-laptop-migrations.sql
```

Solo después de confirmar destino, respaldo y SQL aprobado:

```powershell
dotnet ef database update `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web `
  --configuration Release
```

## 7B. Ruta limpia: crear base y administrador

Confirma primero que no existe una base con ese nombre:

```powershell
psql --host=localhost --port=5432 --username=postgres --dbname=postgres `
  --command="SELECT datname FROM pg_database WHERE datname = 'warehouseEPI';"
```

Si no devuelve filas:

```powershell
createdb --host=localhost --port=5432 --username=postgres `
  --encoding=UTF8 'warehouseEPI'
dotnet ef migrations script --idempotent `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web `
  --configuration Release `
  --output artifacts\new-laptop-migrations.sql
notepad artifacts\new-laptop-migrations.sql
```

Después de revisar el SQL y confirmar que la base está vacía:

```powershell
dotnet ef database update `
  --project src\WarehouseEPI.Infrastructure `
  --startup-project src\WarehouseEPI.Web `
  --configuration Release
dotnet run --project src\WarehouseEPI.Web -- --create-admin
```

El último comando solicita los datos y el NIP de forma interactiva. No pases el
NIP como argumento.

## 8. Ejecutar la verificación de código

Con la conexión administrativa todavía guardada en User Secrets, configura
explícitamente la base aislada de pruebas en la terminal actual. Nunca uses
`warehouseEPI` como nombre en esta variable:

```powershell
$testPasswordSecure = Read-Host -AsSecureString 'Contraseña de postgres para pruebas'
$testPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
  $testPasswordSecure)
try {
  $testPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    $testPasswordPointer)
  $escapedTestPassword = $testPassword.Replace('"', '""')
  $env:WAREHOUSE_EPI_TEST_CONNECTION = "Host=localhost;Port=5432;Database=warehouse_epi_test;Username=postgres;Password=`"$escapedTestPassword`""
  pwsh ./scripts/quality.ps1
}
finally {
  Remove-Item Env:WAREHOUSE_EPI_TEST_CONNECTION -ErrorAction SilentlyContinue
  $testPassword = $null
  $escapedTestPassword = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($testPasswordPointer)
}
```

La verificación debe terminar correctamente antes de publicar. Diferencia este
resultado de la validación posterior en navegador, tablet, lector e impresora.

## 9. Preparar seguridad HTTPS de producción

### 9.1 Directorios protegidos

En **PowerShell como administrador**:

```powershell
pwsh ./scripts/security/Initialize-DataProtectionKeys.ps1
pwsh ./scripts/security/Initialize-ObservabilityLogs.ps1
pwsh ./scripts/security/Initialize-WarehouseEpiBackupDirectory.ps1
```

### 9.2 Certificado LAN

Si se migró la CA anterior, conserva la confianza ya instalada en tablets:

```powershell
pwsh ./scripts/security/Renew-WarehouseEpiLanCertificate.ps1 `
  -ServerIpAddress $serverIp `
  -CaPfxPath (Join-Path $transferRoot 'warehouse-epi-local-ca.pfx')
```

Si es una instalación completamente nueva y no existe CA anterior:

```powershell
$caBackupDirectory = 'E:\WarehouseEPI-CA'
pwsh ./scripts/security/New-WarehouseEpiLanCertificate.ps1 `
  -ServerIpAddress $serverIp `
  -OfflineBackupDirectory $caBackupDirectory
```

El script nuevo informa la huella pero no actualiza automáticamente todos los
User Secrets. Guarda los valores requeridos:

```powershell
dotnet user-secrets set 'AllowedHosts' "warehouse-epi;$serverIp" `
  --project $project
dotnet user-secrets set 'Security:DataProtectionKeysPath' `
  'C:\ProgramData\WarehouseEPI\DataProtection-Keys' --project $project
dotnet user-secrets set 'Security:ServerCertificateThumbprint' `
  '<huella-informada-por-el-script>' --project $project
```

`Renew-WarehouseEpiLanCertificate.ps1` ya actualiza `AllowedHosts` y la huella,
pero aún se debe guardar `Security:DataProtectionKeysPath`:

```powershell
dotnet user-secrets set 'Security:DataProtectionKeysPath' `
  'C:\ProgramData\WarehouseEPI\DataProtection-Keys' --project $project
```

Mueve el PFX de la CA fuera del servidor y conserva su contraseña por separado.
Distribuye a cada tablet únicamente:

```text
artifacts\security\warehouse-epi-local-ca.cer
```

Instálalo como autoridad raíz confiable en el dispositivo y reinicia Chrome.
Nunca copies el PFX ni una clave privada a una tablet.

### 9.3 Firewall de Windows

Confirma primero que la interfaz LAN tenga perfil `Private`:

```powershell
Get-NetConnectionProfile
```

En **PowerShell como administrador**, abre solo HTTP/HTTPS en redes privadas:

```powershell
New-NetFirewallRule -DisplayName 'Warehouse EPI HTTP LAN' `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 80 -Profile Private
New-NetFirewallRule -DisplayName 'Warehouse EPI HTTPS LAN' `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443 -Profile Private
```

No crees una regla para PostgreSQL 5432.

## 10. Provisionar el rol PostgreSQL de la aplicación

En PowerShell normal, el primer script pide la contraseña de `postgres` y luego
una contraseña nueva para `warehouse_epi_app`; el segundo verifica que el rol
pueda operar datos pero no modificar el esquema:

```powershell
pwsh ./scripts/security/Initialize-WarehouseEpiAppRole.ps1
pwsh ./scripts/security/Test-WarehouseEpiAppRole.ps1
```

Actualiza la conexión local para que la aplicación use el rol mínimo:

```powershell
$appPasswordSecure = Read-Host -AsSecureString 'Contraseña de warehouse_epi_app'
$appPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
  $appPasswordSecure)
try {
  $appPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    $appPasswordPointer)
  $escapedAppPassword = $appPassword.Replace('"', '""')
  $appConnection = "Host=localhost;Port=5432;Database=warehouseEPI;Username=warehouse_epi_app;Password=`"$escapedAppPassword`""
  dotnet user-secrets set 'ConnectionStrings:Warehouse' $appConnection `
    --project $project
}
finally {
  $appPassword = $null
  $escapedAppPassword = $null
  $appConnection = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($appPasswordPointer)
}
```

Las migraciones futuras deben seguir ejecutándose con una identidad
administrativa, respaldo validado y SQL revisado; el servicio no recibe DDL.

## 11. Configurar respaldos automáticos

En **PowerShell como administrador**:

```powershell
if (-not (Test-Path `
  'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass')) {
  pwsh ./scripts/security/Initialize-WarehouseEpiBackupCredentials.ps1
}
pwsh ./scripts/security/Install-WarehouseEpiBackupTasks.ps1
pwsh ./scripts/security/Invoke-WarehouseEpiBackup.ps1
pwsh ./scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1
Get-ScheduledTask -TaskPath '\WarehouseEPI\' |
  Select-Object TaskName, State
```

Las tareas quedan programadas a las 02:00 diariamente y los domingos a las
03:00 para validación aislada. El respaldo local no sustituye una copia externa
cifrada.

## 12. Publicar e instalar el servicio Windows

La publicación exige un worktree limpio. No publiques directamente desde una
copia con cambios sin confirmar:

```powershell
git status --short
git rev-parse HEAD
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 `
  -Version $releaseVersion
```

Comprueba el ZIP y su hash externo:

```powershell
$packagePath = Join-Path $repositoryRoot `
  "artifacts\releases\WarehouseEPI-$releaseVersion-win-x64.zip"
Get-Item $packagePath, "$packagePath.sha256"
Get-Content "$packagePath.sha256"
Get-FileHash $packagePath -Algorithm SHA256
```

Antes de instalar, identifica cualquier proceso en 80/443. No termines un
proceso sin reconocerlo:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 80,443 -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

En **PowerShell como administrador**:

```powershell
Set-Location $repositoryRoot
pwsh ./scripts/release/Install-WarehouseEpiService.ps1 `
  -PackagePath $packagePath
```

El instalador crea configuración protegida en
`C:\ProgramData\WarehouseEPI\Config`, instala `NT SERVICE\WarehouseEPI`, ejecuta
el preflight y valida `/health/live`. No aplica migraciones.

## 13. Validación final

En la laptop servidor:

```powershell
Get-Service WarehouseEPI
Get-CimInstance Win32_Service -Filter "Name='WarehouseEPI'" |
  Select-Object Name, State, StartMode, StartName, PathName
curl.exe --silent --fail --insecure --header 'Host: warehouse-epi' `
  https://127.0.0.1/health/live
Get-NetTCPConnection -State Listen -LocalPort 80,443 |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

Después valida por separado:

1. `https://warehouse-epi/` y `https://<IP-reservada>/` desde la laptop.
2. La misma URL desde una tablet LAN sin advertencia de certificado.
3. Inicio de sesión ADMIN y `/Admin/System`: versión y PostgreSQL sanos.
4. Consulta pública de inventario.
5. Una operación controlada con NIP existente; si los NIP fallan después de
   migrar, detén la operación y revisa `Security:PinLookupKey`.
6. Lector HID, cámara Code 128, impresión y diseño responsivo en dispositivo
   físico.
7. Existencia del `.dump` y ZIP pareado más recientes, y tareas en estado
   `Ready`.

La compilación y las pruebas no sustituyen las verificaciones LAN y físicas.

## 14. Corte de la laptop anterior

La secuencia correcta del corte es:

1. Completa en la laptop nueva herramientas, clonación, compilación, secretos y
   directorios, pero no restaures aún `warehouseEPI` desde un respaldo viejo.
2. Impide nuevos movimientos en la laptop anterior.
3. Crea y valida allí el respaldo final de base, referencias y branding.
4. Transfiérelo, valida sus hashes y ejecuta 7A.1–7A.4 una sola vez contra la
   base todavía inexistente de la laptop nueva.
5. Completa rol mínimo, respaldos, Release, servicio y validación final.
6. Activa la reserva DHCP/nombre LAN definitivo para la laptop nueva.
7. Mantén la anterior apagada o aislada para evitar dos servidores con el mismo
   nombre e IP.
8. Conserva la laptop anterior y el respaldo final durante el periodo de
   reversión acordado; no borres datos como parte de este runbook.

Si aparecieron movimientos en la laptop anterior después del respaldo
restaurado, no habilites la nueva como producción. La sustitución de una base
ya restaurada requiere un procedimiento destructivo de reconstrucción aprobado
y queda deliberadamente fuera de este runbook.

## 15. Actualizaciones posteriores

Para una nueva Release ya construida:

```powershell
pwsh ./scripts/release/Update-WarehouseEpiService.ps1 `
  -PackagePath .\artifacts\releases\WarehouseEPI-<version>-win-x64.zip
```

Para volver a una Release que aún está instalada:

```powershell
pwsh ./scripts/release/Rollback-WarehouseEpiService.ps1 -Version <version>
```

Actualizar o revertir el servicio no ejecuta migraciones. Nunca borres
manualmente las Releases, configuración, claves, logs, respaldos o archivos de
branding/referencias dentro de `C:\ProgramData\WarehouseEPI`.

## 16. Evidencia mínima que debe conservarse

Registra sin secretos:

- fecha y responsable;
- modelo de laptop, versión de Windows e IP reservada;
- rama, commit y versión de Release;
- versión de .NET y PostgreSQL;
- SHA-256 del paquete y del respaldo usado;
- resultado de `quality.ps1`, restauración aislada y health local;
- resultado separado de laptop, tablet, lector, cámara e impresora;
- ubicación segura de la CA/PFX y del respaldo externo, sin contraseñas.

## Fuentes de descarga oficiales

- .NET para Windows: <https://learn.microsoft.com/dotnet/core/install/windows>
- Descargas .NET 10: <https://dotnet.microsoft.com/download/dotnet/10.0>
- Git para Windows: <https://git-scm.com/install/windows>
- PostgreSQL para Windows: <https://www.postgresql.org/download/windows/>
- WinGet: <https://learn.microsoft.com/windows/package-manager/winget/install>
