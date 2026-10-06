# Recompilar, aplicar migraciones y actualizar Warehouse EPI en producción

Guía para actualizar una laptop donde el servicio WarehouseEPI ya está instalado.
Preparada el 5 de octubre de 2026; revisada el 6 de octubre de 2026 contra los scripts del repositorio.
Para instalar otra laptop desde cero, consulta [INSTALLATION_SERVER.md](INSTALLATION_SERVER.md).

## Qué hace cada operación

| Operación | Resultado |
| --- | --- |
| dotnet build | Recompila el código. El servicio continúa usando su ejecutable instalado. |
| dotnet ef migrations has-pending-model-changes | Comprueba si el modelo compilado tiene cambios sin migración. |
| dotnet ef migrations script | Genera un archivo SQL para revisar. No aplica las migraciones. |
| psql ejecutando el SQL revisado | Aplica los cambios a la base elegida explícitamente. |
| Publish-WarehouseEpiRelease.ps1 | Publica un paquete autocontenido win-x64, con versión, manifiesto y SHA-256. |
| Update-WarehouseEpiService.ps1 | Instala el paquete, cambia el ejecutable del servicio y comprueba su arranque. No aplica migraciones. |
| Restart-Service WarehouseEPI | Reinicia el ejecutable instalado. No recompila ni aplica migraciones. |
| Corte documental WIP en /Admin/Inventory/WipCutover | Convierte los saldos WIP históricos mediante una operación ADMIN independiente, con vista previa, motivo y NIP. No se ejecuta al migrar o actualizar el servicio. |

**Orden de trabajo:** comprobar producción → preparar código → compilar y validar
→ preparar/revisar SQL → publicar paquete → respaldar → detener servicio
→ aplicar SQL → actualizar servicio → verificar base y aplicación.

Ejecuta los bloques en orden, en la misma ventana de PowerShell. Detente ante
un error: no continúes con una compilación, prueba o migración fallida.
La aplicación permanece disponible durante la preparación. La interrupción
empieza al detener el servicio en el paso 8.

## 1. Abrir PowerShell 7 como administrador

En Windows, busca PowerShell 7 y selecciona **Ejecutar como administrador**.
Acepta el aviso de Control de cuentas de usuario (UAC).
Una terminal normal no puede leer la configuración protegida ni modificar el
servicio, aunque sí pueda compilar el programa.

~~~powershell
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = 'C:\warehouse-EPI'
$serviceConfigPath = 'C:\ProgramData\WarehouseEPI\Config\service-settings.json'
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
$psqlPath = Join-Path $pgBin 'psql.exe'
$backupPassFile = 'C:\ProgramData\WarehouseEPI\BackupCredentials\postgresql-backup.pgpass'
Set-Location -LiteralPath $sourceRoot

. (Join-Path $sourceRoot 'scripts\release\WarehouseEpi.Release.Common.ps1')
Assert-WarehouseEpiAdministrator
foreach ($requiredFile in @($serviceConfigPath, $psqlPath, $backupPassFile)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Falta un archivo requerido: $requiredFile"
    }
}

function Invoke-NativeChecked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "El comando terminó con código $LASTEXITCODE. Detenga el procedimiento."
    }
}

Invoke-NativeChecked { dotnet --version }
Invoke-NativeChecked { dotnet tool restore --tool-manifest (Join-Path $sourceRoot 'dotnet-tools.json') }
Invoke-NativeChecked { dotnet ef --version }
Invoke-NativeChecked { git status --short }
~~~

El SDK debe satisfacer global.json: banda 10.0.400 y parches compatibles;
10.0.401 fue el SDK usado en la preparación del 5 de octubre.
El proyecto utiliza EF Core 10.0.10 y fija dotnet-ef en esa misma versión mediante
dotnet-tools.json, ubicado en la raíz. Usa la herramienta local restaurada;
si falla la restauración, consulta el apartado de errores antes de seguir.

PowerShell no detiene automáticamente todos los ejecutables externos al
recibir un código de error. Por eso los ejemplos comprueban LASTEXITCODE.

## 2. Confirmar el servicio y la base de producción

El destino se obtiene del archivo protegido del servicio. No copies una cadena
de conexión desde User Secrets, appsettings.Development.json o una sesión local.
Muestra solamente host, puerto y nombre de base; nunca la contraseña.

~~~powershell
$serviceBefore = Get-WarehouseEpiService
$previousExecutable = Get-WarehouseEpiExecutableFromService $serviceBefore
$previousVersion = Split-Path -Leaf (Split-Path -Parent $previousExecutable)
$null = Test-WarehouseEpiReleaseDirectory (Split-Path -Parent $previousExecutable)
if ($serviceBefore.State -cne 'Running') {
    throw 'Resuelva primero por qué el servicio instalado no está funcionando.'
}

$serviceSettings = Get-Content -LiteralPath $serviceConfigPath -Raw | ConvertFrom-Json
$productionConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
$productionConnection.set_ConnectionString($serviceSettings.ConnectionStrings.Warehouse)
$database = [string]$productionConnection['Database']
$dbHost = [string]$productionConnection['Host']
$dbPort = if ($productionConnection.ContainsKey('Port')) { [string]$productionConnection['Port'] } else { '5432' }
$dbUser = if ($productionConnection.ContainsKey('Username')) { [string]$productionConnection['Username'] } else { [string]$productionConnection['User ID'] }
if ($database -cne 'warehouseEPI' -or $dbHost -notin @('localhost', '127.0.0.1') -or $dbPort -cne '5432') {
    throw 'El destino no corresponde a la producción local esperada.'
}
Write-Host ('Producción: {0}:{1} / {2}' -f $dbHost, $dbPort, $database)
Write-Host "Versión anterior: $previousVersion"

function Read-ProductionSql([string]$Sql) {
    $savedPassword = $env:PGPASSWORD
    $savedTimeout = $env:PGCONNECT_TIMEOUT
    try {
        $env:PGPASSWORD = [string]$productionConnection['Password']
        $env:PGCONNECT_TIMEOUT = '10'
        $rows = @(& $psqlPath -X -w -h $dbHost -p $dbPort -U $dbUser -d $database -A -t -v ON_ERROR_STOP=1 -c $Sql)
        if ($LASTEXITCODE -ne 0) { throw 'Falló la consulta de comprobación de producción.' }
        return @($rows | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    finally {
        $env:PGPASSWORD = $savedPassword
        $env:PGCONNECT_TIMEOUT = $savedTimeout
    }
}

$confirmedDatabase = @(Read-ProductionSql 'SELECT current_database();')
if ($confirmedDatabase.Count -ne 1 -or $confirmedDatabase[0] -cne 'warehouseEPI') {
    throw 'PostgreSQL no confirmó warehouseEPI como base conectada.'
}
$appliedBefore = @(Read-ProductionSql 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";')
$appliedBefore | Select-Object -Last 5
~~~

Resultado esperado: **localhost:5432 / warehouseEPI** y el historial real de
producción. Las bases warehouse_epi_dev y warehouse_epi_test tienen otros usos.
Anota la versión anterior para poder volver a su ejecutable.

## 3. Preparar una copia de compilación y elegir una versión nueva

El publicador exige un repositorio limpio y confirmado. Cuando existen cambios
locales, prepara una copia bajo artifacts y confirma esos archivos en esa copia.
Así puedes publicar el código actual sin confirmar, descartar ni reorganizar
los cambios del repositorio original.

Revisa primero git status: la copia incluirá archivos versionados y archivos
nuevos no ignorados. Excluye cualquier secreto antes de preparar la copia.
Usa una versión SemVer nueva, por ejemplo 0.10.12. Ese número es un ejemplo;
elige otro si ya existe. No reutilices una versión instalada o publicada.

~~~powershell
$releaseVersion = Read-Host 'Versión nueva de la aplicación, por ejemplo 0.10.12'
Assert-WarehouseEpiVersion $releaseVersion
$buildRoot = Join-Path $sourceRoot "artifacts\release-staging-$releaseVersion"
if ((Test-Path -LiteralPath $buildRoot) -or (Test-Path -LiteralPath (Join-Path $script:WarehouseEpiReleasesRoot $releaseVersion))) {
    throw 'La versión o su copia de compilación ya existe. Elija una versión nueva.'
}
$baseCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'No se pudo identificar el commit original.' }
$sourceFiles = @(& git -C $sourceRoot -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'No se pudo enumerar el código.' }
New-Item -ItemType Directory -Path $buildRoot | Out-Null

$sourceFingerprint = @(foreach ($relativePath in $sourceFiles) {
    $originalFile = Join-Path $sourceRoot $relativePath
    if (-not (Test-Path -LiteralPath $originalFile -PathType Leaf)) { continue }
    $copiedFile = Join-Path $buildRoot $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copiedFile) | Out-Null
    Copy-Item -LiteralPath $originalFile -Destination $copiedFile
    [pscustomobject]@{ path = $relativePath; sha256 = (Get-FileHash -LiteralPath $originalFile -Algorithm SHA256).Hash }
})

Invoke-NativeChecked { git -C $buildRoot init --quiet }
Invoke-NativeChecked { git -C $buildRoot add --all }
Invoke-NativeChecked {
    git -C $buildRoot -c user.name='Warehouse EPI Deploy' -c user.email='deploy@local.invalid' -c commit.gpgsign=false commit --quiet -m "Snapshot de despliegue $releaseVersion desde $baseCommit"
}
New-Item -ItemType Directory -Force -Path (Join-Path $buildRoot 'artifacts') | Out-Null
[ordered]@{ originalCommit = $baseCommit; version = $releaseVersion; files = $sourceFingerprint } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'artifacts\deployment-source.json') -Encoding utf8
Set-Location -LiteralPath $buildRoot
~~~

Esta copia tiene su propio Git; no es una rama ni un commit añadido al
repositorio original. Conserva el identificador del commit original y el
manifiesto de archivos de la copia para identificar exactamente qué se publicó.
Si cambias el código después, prepara otra copia y otra versión.

## 4. Recompilar y validar

Restaura la herramienta local en la copia y verifica formato antes de compilar
sin especificar win-x64. Esta compilación también permite ejecutar las
herramientas de EF y las pruebas con sus rutas normales. Las comprobaciones
de formato no modifican archivos y excluyen las migraciones inmutables,
igual que scripts/quality.ps1.

~~~powershell
Invoke-NativeChecked { dotnet tool restore --tool-manifest (Join-Path $buildRoot 'dotnet-tools.json') }
Invoke-NativeChecked { dotnet restore WarehouseEPI.sln --locked-mode }
Invoke-NativeChecked { git diff --check }
Invoke-NativeChecked { dotnet format whitespace WarehouseEPI.sln --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**' }
Invoke-NativeChecked { dotnet format style WarehouseEPI.sln --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**' }
Invoke-NativeChecked { dotnet format analyzers WarehouseEPI.sln --verify-no-changes --no-restore --exclude 'src/WarehouseEPI.Infrastructure/Persistence/Migrations/**' }
Invoke-NativeChecked { dotnet build WarehouseEPI.sln --configuration Release --no-restore }
~~~

Resultado esperado: compilación completada sin errores.
Directory.Build.rsp crea automáticamente un binlog único en .binlogs de esta
copia. No agregues -bl por costumbre y no borres esos registros al reintentar.

Las pruebas requieren una conexión explícita a warehouse_epi_test. El usuario
de pruebas debe poder recrear esa base y tener los permisos necesarios para
las bases temporales de las pruebas. No uses el rol mínimo de la aplicación
como identidad de administración de pruebas.
El siguiente bloque reutiliza una conexión de pruebas ya configurada o solicita
la contraseña administrativa de postgres de forma oculta. La contraseña queda
en memoria durante la ejecución y la variable de entorno se restaura al terminar.

~~~powershell
$savedTestConnection = $env:WAREHOUSE_EPI_TEST_CONNECTION
$testPasswordPointer = [IntPtr]::Zero
$testConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
$testResultsPath = Join-Path $buildRoot ('artifacts\test-results\deployment-' + [Guid]::NewGuid().ToString('N'))
try {
    if ([string]::IsNullOrWhiteSpace($savedTestConnection)) {
        $testPasswordSecure = Read-Host -AsSecureString 'Contraseña de postgres para la base de pruebas'
        $testPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($testPasswordSecure)
        $testConnection['Host'] = 'localhost'
        $testConnection['Port'] = '5432'
        $testConnection['Database'] = 'warehouse_epi_test'
        $testConnection['Username'] = 'postgres'
        $testConnection['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($testPasswordPointer)
        $env:WAREHOUSE_EPI_TEST_CONNECTION = $testConnection.ConnectionString
    }
    else { $testConnection.set_ConnectionString($savedTestConnection) }
    if ([string]$testConnection['Database'] -cne 'warehouse_epi_test') {
        throw 'La conexión de pruebas no apunta a warehouse_epi_test.'
    }
    Invoke-NativeChecked {
        dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --configuration Release --no-build --no-restore --filter 'Category!=PostgreSQLPerformance' --logger 'trx;LogFileName=deployment-tests.trx' --results-directory $testResultsPath --collect:'XPlat Code Coverage'
    }
    # VSTest puede duplicar adjuntos temporales dentro de In; conserva el reporte final.
    $resultsRoot = [IO.Path]::GetFullPath($testResultsPath).TrimEnd('\') + '\'
    foreach ($temporaryResults in @(Get-ChildItem -LiteralPath $testResultsPath -Directory -Recurse | Where-Object Name -eq 'In')) {
        if (-not $temporaryResults.FullName.StartsWith($resultsRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'El adjunto temporal está fuera del directorio de resultados.'
        }
        Remove-Item -LiteralPath $temporaryResults.FullName -Recurse -Force
    }
    & (Join-Path $buildRoot 'scripts\Test-Coverage.ps1') -ResultsDirectory $testResultsPath
}
finally {
    $env:WAREHOUSE_EPI_TEST_CONNECTION = $savedTestConnection
    $testConnection.Clear()
    if ($testPasswordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($testPasswordPointer)
    }
}
~~~

Cada intento conserva su propio directorio de resultados. La puerta de cobertura
exige al menos 85 % de líneas y 45 % de ramas, igual que scripts/quality.ps1.
Conserva el TRX y coverage.cobertura.xml; un incumplimiento detiene el procedimiento.

El despliegue habitual excluye únicamente la categoría `PostgreSQLPerformance`.
Conserva las pruebas funcionales, integración PostgreSQL, migraciones y los mismos
umbrales de cobertura. El benchmark crea 48 bases temporales y migra cada una;
ejecútalo aparte con `scripts/Test-Performance.ps1`, cuando cambien consultas o
procesos cuyo rendimiento deba medirse. No representa una prueba funcional omitida
por fallo. `scripts/quality.ps1 -IncludePerformance` conserva la validación completa.

Revisa cualquier fallo antes de seguir. Una compilación correcta no significa
que las pruebas pasaron. Un error de permisos de la base de pruebas tampoco
demuestra que la migración o la aplicación estén funcionando correctamente.

## 5. Determinar las migraciones pendientes y preparar el SQL

Este paso combina el historial leído de producción con las migraciones de la
copia compilada. Se revisan todos los archivos .cs: algunas migraciones del
proyecto tienen el atributo Migration directamente en su archivo y no cuentan
con un archivo .Designer.cs separado.

~~~powershell
$migrationProject = Join-Path $buildRoot 'src\WarehouseEPI.Infrastructure\WarehouseEPI.Infrastructure.csproj'
$startupProject = Join-Path $buildRoot 'src\WarehouseEPI.Web\WarehouseEPI.Web.csproj'
$efProjectArgs = @('--project', $migrationProject, '--startup-project', $startupProject, '--configuration', 'Release', '--no-build')
$efProductionArgs = @('--', '--environment', 'Production', "--ServiceConfigPath=$serviceConfigPath")

Invoke-NativeChecked { dotnet ef migrations has-pending-model-changes @efProjectArgs @efProductionArgs }
$migrationDirectory = Join-Path $buildRoot 'src\WarehouseEPI.Infrastructure\Persistence\Migrations'
$knownMigrations = @(Get-ChildItem -LiteralPath $migrationDirectory -Filter '*.cs' | ForEach-Object {
    $match = [regex]::Match((Get-Content -LiteralPath $_.FullName -Raw), '\[Migration\("(?<id>\d{14}_[A-Za-z0-9_]+)"\)\]')
    if ($match.Success) { $match.Groups['id'].Value }
} | Sort-Object -Unique)
if ($knownMigrations.Count -eq 0) { throw 'No se encontraron migraciones en el código.' }
$unknownApplied = @($appliedBefore | Where-Object { $_ -notin $knownMigrations })
if ($unknownApplied.Count -gt 0) { throw 'Producción contiene migraciones ausentes del código. Revise la versión antes de seguir.' }
$pendingMigrations = @($knownMigrations | Where-Object { $_ -notin $appliedBefore })
$fromMigration = if ($appliedBefore.Count -gt 0) { $appliedBefore[-1] } else { '0' }
$toMigration = $knownMigrations[-1]
if (@($pendingMigrations | Where-Object { [string]::CompareOrdinal($_, $fromMigration) -lt 0 }).Count -gt 0) {
    throw 'Hay huecos en el historial. Revise un script idempotente completo desde 0 antes de aplicarlo.'
}
$sqlPath = Join-Path $sourceRoot "artifacts\releases\migrations-$releaseVersion.sql"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sqlPath) | Out-Null
Write-Host "Migraciones pendientes: $($pendingMigrations.Count)"
$pendingMigrations
if ($pendingMigrations.Count -gt 0) {
    Invoke-NativeChecked { dotnet ef migrations script $fromMigration $toMigration --idempotent --no-transactions --output $sqlPath @efProjectArgs @efProductionArgs }
}
~~~

El mensaje **No changes have been made to the model since the last migration**
confirma que el modelo y las migraciones coinciden. No significa que producción
ya tenga esas migraciones aplicadas. La lista pendingMigrations es la que
indica los cambios faltantes en la base consultada.

Los argumentos de EF fijan Production y ServiceConfigPath explícitamente.
Generar SQL sigue siendo una preparación: el destino efectivo de la ejecución
se seleccionará con psql en el paso 8.

Si hay migraciones pendientes, abre el SQL y revisa tablas, datos, índices,
restricciones y compatibilidad con la versión anterior de la aplicación.
No renombres ni edites migraciones aplicadas. Si ves DROP, conversiones de
datos o SQL que requiere ejecutarse fuera de una transacción, prepara un
procedimiento específico antes de detener producción.
El bloque general del paso 8 utiliza una sola transacción para todo el archivo.

Después de revisar el archivo, guarda su hash en esta misma sesión:

~~~powershell
$reviewedSqlHash = if ($pendingMigrations.Count -gt 0) { (Get-FileHash -LiteralPath $sqlPath -Algorithm SHA256).Hash } else { $null }
~~~

## 6. Preparar win-x64 y publicar el paquete

Para evitar NU1004, prepara los archivos de dependencias para el runtime que
realmente se publicará. Hazlo en la copia, conservando los archivos originales.
Este restore puede modificar los tres packages.lock.json.

~~~powershell
Invoke-NativeChecked { dotnet restore src/WarehouseEPI.Web/WarehouseEPI.Web.csproj --runtime win-x64 -p:SelfContained=true -p:RestoreLockedMode=false --force-evaluate }
Invoke-NativeChecked { git diff -- src/WarehouseEPI.Core/packages.lock.json src/WarehouseEPI.Infrastructure/packages.lock.json src/WarehouseEPI.Web/packages.lock.json }
~~~

Comprueba que las diferencias correspondan al runtime. Si cambian versiones
de dependencias no previstas, revisa la causa y repite las validaciones
correspondientes. Después confirma los archivos de dependencias en la copia:

~~~powershell
Invoke-NativeChecked { git add src/WarehouseEPI.Core/packages.lock.json src/WarehouseEPI.Infrastructure/packages.lock.json src/WarehouseEPI.Web/packages.lock.json }
git diff --cached --quiet
$lockDiffExitCode = $LASTEXITCODE
if ($lockDiffExitCode -eq 1) {
    Invoke-NativeChecked { git -c user.name='Warehouse EPI Deploy' -c user.email='deploy@local.invalid' -c commit.gpgsign=false commit --quiet -m "Dependencias win-x64 para $releaseVersion" }
}
elseif ($lockDiffExitCode -ne 0) { throw 'No se pudieron comprobar los cambios de dependencias.' }
$dirtySnapshot = @(& git status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirtySnapshot.Count -gt 0) { throw 'La copia de publicación debe estar limpia.' }

& (Join-Path $buildRoot 'scripts\release\Publish-WarehouseEpiRelease.ps1') -Version $releaseVersion
$packagePath = Join-Path $buildRoot "artifacts\releases\WarehouseEPI-$releaseVersion-win-x64.zip"
Test-WarehouseEpiPackageHash $packagePath
~~~

Resultado esperado: ZIP, archivo .zip.sha256 y carpeta publicada con
release-manifest.json. La versión del ensamblado y del manifiesto deben
corresponder a releaseVersion. Comprueba también que el manifiesto registre
exactamente las migraciones detectadas en el paso 5:

~~~powershell
$publishedDirectory = Join-Path $buildRoot "artifacts\releases\WarehouseEPI-$releaseVersion-win-x64"
$releaseManifest = Get-Content -LiteralPath (Join-Path $publishedDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
if ($releaseManifest.version -cne $releaseVersion -or
    @($releaseManifest.migrationIds).Count -ne $knownMigrations.Count -or
    (Compare-Object $knownMigrations @($releaseManifest.migrationIds))) {
    throw 'El manifiesto no coincide con la versión o las migraciones revisadas.'
}
~~~

El publicador recompila para win-x64;
no copies los archivos bin/Release sobre la carpeta de un servicio activo.

## 7. Crear y comprobar un respaldo nuevo

Todavía no detengas el servicio. Crea un respaldo de la base confirmada y sus
referencias del croquis. El script valida el dump con pg_restore --list.
Para una entrega con cambios de datos o esquema importantes, ejecuta también
la validación de recuperación en una base temporal antes de seguir.

~~~powershell
$savedBackupPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $null
    Invoke-NativeChecked {
        pwsh -NoProfile -File (Join-Path $sourceRoot 'scripts\security\Invoke-WarehouseEpiBackup.ps1') -DatabaseHost $dbHost -DatabasePort ([int]$dbPort) -DatabaseName $database
    }
}
finally { $env:PGPASSWORD = $savedBackupPassword }
Assert-WarehouseEpiValidatedBackup
$backup = Get-ChildItem -LiteralPath 'C:\ProgramData\WarehouseEPI\Backups' -Filter 'warehouseEPI-*.dump' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$backup | Select-Object FullName, LastWriteTimeUtc, Length
~~~

Debe existir el dump recién creado y el ZIP pareado de referencias.
La credencial de respaldo se mantiene en BackupCredentials con ACL privada.
No la pases como argumento ni la copies a la documentación o a Git.

La validación de restauración opcional se ejecuta con
scripts/security/Invoke-WarehouseEpiRecoveryValidation.ps1 y usa una base
temporal. Consulta [OPERATIONS.md](OPERATIONS.md) para recuperación y respaldos.

## 8. Aplicar las migraciones y actualizar el servicio

Antes de detenerlo, verifica que el historial no cambió durante la preparación
y que el código original sigue correspondiendo a la copia publicada:

~~~powershell
$appliedNow = @(Read-ProductionSql 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";')
if ($appliedBefore.Count -ne $appliedNow.Count -or (@($appliedBefore) -join '|') -cne (@($appliedNow) -join '|')) {
    throw 'El historial cambió. Vuelva a determinar las migraciones y revisar el SQL.'
}
$currentFiles = @(& git -C $sourceRoot -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or (Compare-Object $sourceFiles $currentFiles)) {
    throw 'La lista de archivos cambió. Prepare otra copia de compilación.'
}
foreach ($file in $sourceFingerprint) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $file.path) -Algorithm SHA256).Hash -cne $file.sha256) {
        throw "El código cambió después de copiarlo: $($file.path). Prepare otra versión."
    }
}
Test-WarehouseEpiPackageHash $packagePath
~~~

La aplicación usa un rol mínimo. Las migraciones se aplican con postgres y la
credencial administrativa de respaldo, manteniendo la cuenta del servicio
sin privilegios de administración de PostgreSQL.

**Desde el siguiente bloque hay una interrupción del servicio.** Ejecútalo
cuando la operación pueda detenerse. El SQL revisado debe admitir una sola
transacción y ser compatible con el ejecutable anterior para poder recuperarlo.

~~~powershell
$savedMigrationPassword = $env:PGPASSWORD
$savedPassFile = $env:PGPASSFILE
$savedPgOptions = $env:PGOPTIONS
$savedConnectTimeout = $env:PGCONNECT_TIMEOUT
$serviceWasStopped = $false
try {
    $serviceWasStopped = $true
    Stop-WarehouseEpiServiceSafely
    if ($pendingMigrations.Count -gt 0) {
        if ((Get-FileHash -LiteralPath $sqlPath -Algorithm SHA256).Hash -cne $reviewedSqlHash) {
            throw 'El SQL cambió después de revisarlo.'
        }
        $env:PGPASSWORD = $null
        $env:PGPASSFILE = $backupPassFile
        $env:PGOPTIONS = '-c lock_timeout=15s -c statement_timeout=120s'
        $env:PGCONNECT_TIMEOUT = '10'
        Invoke-NativeChecked {
            & $psqlPath -X -w -h $dbHost -p $dbPort -U postgres -d $database --single-transaction -v ON_ERROR_STOP=1 -f $sqlPath
        }
    }
    $appliedAfter = @(Read-ProductionSql 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";')
    if (Compare-Object $knownMigrations $appliedAfter) {
        throw 'El historial final no coincide con las migraciones del código publicado.'
    }
    & (Join-Path $sourceRoot 'scripts\release\Update-WarehouseEpiService.ps1') -PackagePath $packagePath
}
catch {
    if ($serviceWasStopped) {
        Stop-WarehouseEpiServiceSafely
        Set-WarehouseEpiServiceBinary $previousExecutable
        Start-WarehouseEpiServiceAndVerify
    }
    throw
}
finally {
    $env:PGPASSWORD = $savedMigrationPassword
    $env:PGPASSFILE = $savedPassFile
    $env:PGOPTIONS = $savedPgOptions
    $env:PGCONNECT_TIMEOUT = $savedConnectTimeout
}
~~~

El archivo se generó con --no-transactions para que psql controle la transacción
completa mediante --single-transaction. ON_ERROR_STOP=1 evita continuar después
de un error. No mezcles este bloque con un SQL que incluya sus propios
START TRANSACTION/COMMIT: esos COMMIT romperían la transacción exterior.

Si falla la aplicación del SQL, la transacción se revierte. Si las migraciones
terminan y después falla la actualización, volver al ejecutable anterior
**no revierte las migraciones confirmadas**. No borres el historial ni ejecutes
una migración inversa sin revisar sus efectos sobre los datos.

## 9. Verificar el resultado

~~~powershell
$serviceAfter = Get-WarehouseEpiService
$activeExecutable = Get-WarehouseEpiExecutableFromService $serviceAfter
$expectedExecutable = Join-Path (Join-Path $script:WarehouseEpiReleasesRoot $releaseVersion) 'WarehouseEPI.Web.exe'
if ($serviceAfter.State -cne 'Running' -or $activeExecutable -cne $expectedExecutable) {
    throw 'El servicio no está ejecutando la versión publicada.'
}
$serviceAfter | Select-Object Name, State, StartName, PathName
$healthHost = @(([string]$serviceSettings.AllowedHosts).Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })[0]
if ([string]::IsNullOrWhiteSpace($healthHost) -or $healthHost -notmatch '^[A-Za-z0-9.-]+$') {
    throw 'AllowedHosts no contiene un host apropiado para la comprobación local.'
}
foreach ($path in @('/health/live', '/Admin/Login', '/Locations', '/Locations/Display', '/Operations/Exit?mode=wip')) {
    $statusCode = & curl.exe --silent --insecure --max-time 15 --header "Host: $healthHost" --output NUL --write-out '%{http_code}' "https://127.0.0.1$path"
    if ($LASTEXITCODE -ne 0 -or $statusCode -cne '200') { throw "Falló la comprobación HTTP de $path." }
    Write-Host "$path : $statusCode"
}
$redirectHeaders = @(& curl.exe --silent --insecure --max-time 15 --head --header "Host: $healthHost" 'https://127.0.0.1/Operations/WipIssue')
if ($LASTEXITCODE -ne 0 -or
    -not ($redirectHeaders -match '^HTTP/\S+ 302(?:\s|$)') -or
    -not ($redirectHeaders -match '^Location:\s*/Operations/Exit\?mode=wip\s*$')) {
    throw 'WipIssue no devolvió la redirección esperada a Exit?mode=wip.'
}
$appliedFinal = @(Read-ProductionSql 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";')
if (Compare-Object $knownMigrations $appliedFinal) { throw 'Quedan diferencias entre código y base.' }
[ordered]@{
    version = $releaseVersion
    snapshotCommit = (& git -C $buildRoot rev-parse HEAD).Trim()
    database = $database
    package = $packagePath
    packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    backup = $backup.FullName
    testResults = $testResultsPath
    appliedMigrations = $pendingMigrations
    previousVersion = $previousVersion
    serviceState = $serviceAfter.State
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $buildRoot 'artifacts\deployment-result.json') -Encoding utf8
Write-Host "Actualización verificada: $releaseVersion / $database"
~~~

El parámetro --insecure se usa para esta comprobación local por 127.0.0.1,
siguiendo el comprobador del servicio. En la tablet, comprueba HTTPS por el
nombre configurado y con la CA instalada.

Entra como ADMIN a **/Admin/System** y confirma versión y salud de PostgreSQL.
Prueba las pantallas afectadas por el cambio. Audita las tablas, índices,
restricciones y permisos creados por la migración.
Un servicio Running y /health/live con 200 sólo acreditan el proceso;
ese endpoint no comprueba la base de datos.

`/Operations/WipIssue` devuelve **302** con `Location: /Operations/Exit?mode=wip`
por diseño; la ruta de destino devuelve **200**. No exijas 200 a la ruta antigua
ni ocultes la redirección siguiéndola automáticamente en esta comprobación.

Guarda versión, commit de la copia, hash del paquete, respaldo usado,
migraciones aplicadas, resultado de pruebas y verificaciones HTTP.
Conserva binlogs, SQL revisado, reportes TRX y coverage.cobertura.xml.
Al finalizar, limpia los objetos que contienen la configuración:

~~~powershell
$productionConnection.Clear()
Remove-Variable serviceSettings, productionConnection
~~~

## 10. Convertir los saldos WIP históricos, si corresponde

El despliegue y el corte documental son operaciones distintas. La migración
`20261005172826_WipDocumentaryInventory` crea el esquema, pero no convierte
los saldos. Sigue [WIP_DOCUMENTARY.md](WIP_DOCUMENTARY.md#conversión-admin)
para una ejecución expresamente autorizada; no sustituyas el flujo por UPDATE
directos ni por la modificación del historial de migraciones.

1. Antes del corte, respalda y valida la restauración en una copia aislada.
   Revisa allí la vista previa con el esquema actualizado. Conserva la evidencia
   y una comparación por ubicación, producto y lote de los saldos de almacenamiento.
2. En la ventana operativa, abre `/Admin/Inventory/WipCutover` en producción con
   sesión ADMIN. Revisa saldos, placas, lotes y compromisos de órdenes. Resuelve
   cualquier advertencia antes de confirmar; una revisión obsoleta se rechaza.
3. Introduce un motivo, por ejemplo «Conversión de saldos WIP históricos a
   seguimiento documental tras despliegue 0.10.19», y el NIP ADMIN directamente
   en la aplicación. Confirma una sola operación y conserva el resultado.
4. Verifica que exista el registro en `wip_document_cutovers`, con responsable,
   motivo, identidad de operación e instantánea; contrasta los documentos y lotes
   creados con esa instantánea y comprueba que no queden saldos WIP distintos de
   cero. Revisa las placas anuladas y las asignaciones WIP desactivadas.
5. Contrasta los saldos de almacenamiento antes/después. Si hubo otras operaciones
   durante el intervalo, concilia sus movimientos por separado. Guarda evidencia
   de la conservación del almacén y del movimiento `WIP_DOCUMENT_CUTOVER`.

Las cantidades sin origen inequívoco se conservan como **Apertura del corte**;
no representan entregas nuevas ni consumo. El corte no se revierte como un ajuste
ordinario. Su ejecución debe registrarse por separado del resultado del despliegue.
Abrir la vista previa o disponer de `success=true` en el informe del despliegue
no acredita que el corte se haya confirmado.

Si el navegador muestra `ERR_CERT_AUTHORITY_INVALID`, verifica el certificado
y la confianza en la CA del servidor según [INSTALLATION_SERVER.md](INSTALLATION_SERVER.md).
No desactives la validación HTTPS como solución permanente. La sesión y el NIP
ADMIN de la aplicación son distintos de la elevación UAC de Windows.

## 11. Volver a una versión anterior

Desde la misma sesión, con previousVersion anotada en el paso 2:

~~~powershell
& (Join-Path $sourceRoot 'scripts\release\Rollback-WarehouseEpiService.ps1') -Version $previousVersion
~~~

Este script valida el ejecutable anterior, cambia el servicio y comprueba
/health/live. Conserva la versión activa y dos anteriores.
El rollback del servicio cambia archivos ejecutables, no la base.
Si el esquema ya no admite la aplicación anterior, prepara una recuperación
coordinada con el respaldo y los archivos pareados; no restaures sobre una base
activa. Sigue el procedimiento de [OPERATIONS.md](OPERATIONS.md).

## Reintentar sin repetir la preparación validada

Compila, prueba y publica mientras el servicio sigue disponible. Conserva la copia
inmutable, su manifiesto de código, TRX, cobertura, paquete y SHA-256. No vuelvas
a compilar ni ejecutar pruebas por un fallo de UAC, certificado o comprobación
HTTP si esos artefactos siguen siendo exactamente los validados. Si cambia código,
dependencias o configuración relevante para las pruebas, invalida la evidencia
afectada y prepara la validación correspondiente.

Antes de reintentar, consulta el servicio, ejecutable activo y el historial real
de migraciones. Una migración confirmada no se repite como si estuviera pendiente;
regenera el SQL desde el historial actual si todavía faltan migraciones. Conserva
un respaldo reciente y comprueba de nuevo el estado de producción.

Si la versión ya está activa y coincide con el paquete, repite únicamente las
comprobaciones del paso 9. Si quedó instalada pero inactiva, el actualizador
genérico no admite reutilizar esa carpeta: no lo relances a ciegas ni borres la
release. Valida manifiesto, hashes, preflight y compatibilidad con el esquema antes
de una activación controlada con recuperación al ejecutable anterior. Los scripts
fechados de 0.10.19 no son un mecanismo genérico de continuación automática.

## Errores habituales y cómo resolverlos

| Mensaje o síntoma | Causa y acción |
| --- | --- |
| Access denied al leer service-settings.json, Releases o al controlar el servicio | La terminal no está elevada. Abre PowerShell 7 como administrador y compruébalo con Assert-WarehouseEpiAdministrator. No abras las ACL a todos los usuarios. |
| The operation was canceled by the user | La elevación UAC fue cancelada. Confirma el estado actual del servicio y de las migraciones antes de reintentar. |
| La publicación requiere un worktree limpio y confirmado | Usa la copia del paso 3 y confirma allí las dependencias runtime. Evita descartar cambios del repositorio original para superar esta comprobación. |
| NU1004: runtime identifiers win-x64; lock file inconsistente | Ejecuta el restore específico de win-x64 del paso 6 en la copia, revisa y confirma packages.lock.json. Conserva RestoreLockedMode=true en la publicación. |
| Ya existe un artefacto o una Release para esa versión | Elige una versión nueva y una copia nueva. Si el intento dejó archivos incompletos, identifícalos antes de cualquier limpieza; conserva los binlogs y la Release activa. |
| No changes have been made to the model since the last migration | Es un resultado correcto del chequeo de modelo. Consulta __EFMigrationsHistory para saber qué está aplicado realmente. |
| EF no puede crear el DbContext, o parece cargar desarrollo | Comprueba el proyecto de inicio y pasa --environment Production y --ServiceConfigPath después del separador --. La generación de SQL no aplica cambios a la base. |
| Falta dotnet-ef o Cannot find tool manifest | Desde la raíz de la copia, comprueba que exista dotnet-tools.json, ejecuta dotnet tool restore --tool-manifest ./dotnet-tools.json y consulta dotnet tool list --local. Si falta el archivo, recupera la copia completa del código. |
| 42501: must be owner of database warehouse_epi_test | La conexión de pruebas usa una identidad que no posee esa base. Configura una identidad de pruebas con los permisos necesarios; no des permisos de administración al rol de producción para arreglar pruebas. |
| 42501 al crear una tabla o aplicar una migración | Se está usando el rol mínimo de la aplicación. Aplica el SQL revisado con la identidad administrativa del paso 8. |
| 42501 al consultar una tabla nueva desde la aplicación | Audita SELECT/INSERT/UPDATE/DELETE de warehouse_epi_app y los privilegios predeterminados de quien creó la tabla. Consulta scripts/security/provision-postgresql-role.sql; no otorgues SUPERUSER al rol de la aplicación. |
| duplicate key al agregar un rol o datos de catálogo | Revisa los datos y el historial de producción. No borres roles usados ni insertes filas en __EFMigrationsHistory a ciegas para forzar el despliegue. |
| El historial cambió, hay migraciones desconocidas o huecos | Vuelve a consultar producción y comprueba el commit de la copia. Regenera y revisa el SQL; un script fechado de otra entrega no sirve como procedimiento genérico. |
| Falló el respaldo o falta el ZIP de referencias | Revisa el archivo de credenciales, su host/puerto, las herramientas PostgreSQL y los registros del respaldo. No apliques SQL sin el nuevo respaldo validado y sus referencias. |
| Lock timeout o statement timeout al aplicar SQL | La transacción debe fallar y revertirse. Revisa bloqueos y duración esperada; ajusta los límites sólo después de revisar la migración. |
| Preflight de producción falló | Comprueba certificado, claves de Data Protection, directorio de logs, permisos y conectividad PostgreSQL. El preflight verifica conectividad, no acredita que todas las migraciones estén aplicadas. |
| El servicio no arranca o los puertos 80/443 están ocupados | Identifica qué proceso ocupa los puertos. No ejecutes otra instancia Production con dotnet run mientras el servicio está activo. Consulta OPERATIONS.md. |
| Health devuelve 400 o 404 | Usa loopback y un encabezado Host incluido en AllowedHosts. /health/live devuelve 404 desde la LAN por diseño. |
| Running y health 200, pero una pantalla falla con 500 | Revisa PostgreSQL en /Admin/System, el historial de migraciones, permisos de tablas nuevas y los logs sanitizados de ProgramData. |
| Una prueba espera una firma de método o HTML anterior | Revisa si cambió el contrato o quedó desactualizada la prueba. No conviertas un fallo en éxito únicamente por excluir esa prueba. |

La herramienta local dotnet-ef está fijada en 10.0.10 en dotnet-tools.json,
en la raíz del repositorio. Para restaurarla desde la copia de compilación:

~~~powershell
Invoke-NativeChecked { dotnet tool restore --tool-manifest (Join-Path $buildRoot 'dotnet-tools.json') }
Invoke-NativeChecked { dotnet tool list --local }
Invoke-NativeChecked { dotnet ef --version }
~~~

Para futuras entregas, mantén la versión del manifiesto alineada con
Microsoft.EntityFrameworkCore.Design en Directory.Packages.props y vuelve
a restaurar las herramientas locales.

## Despliegue confirmado el 6 de octubre de 2026 — 0.10.19

El informe local `artifacts/releases/production-deployment-0.10.19.json`
registró `success=true` a las **10:38:35 de America/Matamoros**
(`2026-10-06T15:38:35Z`), con el servicio Running y el ejecutable
`C:\ProgramData\WarehouseEPI\Releases\0.10.19\WarehouseEPI.Web.exe`.
Este es un registro de esa ejecución, no una consulta del estado actual.

- Base: `localhost:5432 / warehouseEPI`; 63 migraciones aplicadas en total.
- Migraciones de esta entrega: `20261005172826_WipDocumentaryInventory` y
  `20261006120129_AddRackWipAssociations`.
- Validación: 1.283 pruebas .NET y 248 pruebas JavaScript aprobadas.
- Respaldo: `C:\ProgramData\WarehouseEPI\Backups\warehouseEPI-20261006-153458.dump`,
  con restauración aislada verificada.
- Paquete: `artifacts/release-staging-0.10.19/artifacts/releases/WarehouseEPI-0.10.19-win-x64.zip`.
- SHA-256 del paquete: `475A399B48921663BD69123E8A952FB9D344F940182DD1CC45CCAB0D24A60FC9`.
- SHA-256 del SQL: `9A97938B28A3E9BBE6598F7A1B39519F566DEEC4343E8098B27793E9770609EB`.

El primer intento restauró el ejecutable anterior porque una comprobación
exigía 200 a `/Operations/WipIssue`. Las migraciones ya estaban confirmadas.
Se corrigió la comprobación para admitir el 302 esperado y se activó el paquete
instalado; el informe final verificó también el destino con 200.
Los scripts fechados bajo `artifacts/releases` son evidencia local ignorada por
Git, no procedimientos genéricos que deban repetirse para otras entregas.

**Corte WIP pendiente de verificación:** al terminar el despliegue había 14 filas
con saldo WIP distinto de cero y `wipCutoverExecuted=false`. Después se abrió la
vista previa ADMIN, pero no se ha recibido ni verificado evidencia de confirmación.
No registrar la conversión como completada hasta ejecutar las comprobaciones
del paso 10; conservar el informe original del despliegue y añadir evidencia
separada del corte.

## Caso preparado el 5 de octubre de 2026

**Registro histórico; no es una entrega validada para ejecutar.** Registró
13 pruebas fallidas. Resuelve los fallos y completa la validación del paso 4
en una copia y versión nuevas antes de preparar otro despliegue.

Se preparó el paquete **0.10.11-local.20261005.1** desde una copia del código,
con dos migraciones que faltaban en la inspección inicial:

- 20261002190000_AddProductionRole.
- 20261005131046_AddLocationRackFormats.

Los scripts fechados quedaron en artifacts/releases y son artefactos locales
ignorados por Git. Están ligados a ese paquete y a su copia de código:
no son scripts genéricos para recompilar los cambios de otra fecha.

**Preparar SQL para revisión:**

~~~powershell
pwsh -NoProfile -File 'C:\warehouse-EPI\artifacts\releases\Prepare-Migrations-20261005.ps1'
~~~

Ese comando genera prepared-migrations-20261005.sql con transacciones propias
por migración. No lo sustituyas dentro del bloque con --single-transaction
del paso 8.

**Ejecutar la entrega preparada, desde PowerShell como administrador:**

~~~powershell
pwsh -NoProfile -File 'C:\warehouse-EPI\artifacts\releases\Deploy-Production-20261005.ps1'
~~~

El script verifica los archivos del código contra la copia, comprueba
localhost:5432 / warehouseEPI y las dos migraciones esperadas, crea un respaldo,
aplica pending-migrations-20261005.sql en una transacción y actualiza el servicio.
Ese SQL se generó sin transacciones internas. Si el código o el historial
cambiaron desde la preparación, el script debe detenerse: prepara otra entrega.

El primer intento elevado fue cancelado por UAC antes de comenzar el despliegue.
Eso no confirma el estado de intentos posteriores. Consulta, si existen:

~~~powershell
Get-Content -LiteralPath 'C:\warehouse-EPI\artifacts\releases\production-deployment-20261005.json'
Get-Content -LiteralPath 'C:\warehouse-EPI\artifacts\releases\production-service-update-20261005.log'
Get-Content -LiteralPath 'C:\warehouse-EPI\artifacts\releases\production-deployment-errors-20261005.log'
~~~

En el JSON, success=true acredita la ejecución completa de ese script.
success=null indica una etapa en curso; success=false incluye step, rollback
y migrationsApplied. Si el archivo no existe, ese script no llegó a escribir
su estado; comprueba el servicio y el historial real antes de concluir nada.

La validación del paquete preparado registró 293 pruebas correctas y 13 fallos:
nueve por permisos de la base de pruebas y cuatro por comprobaciones de firmas
o HTML anteriores. Las ocho pruebas adicionales de acceso por rol pasaron.
Los reportes quedaron en artifacts/releases/validation-20261005.
Estos resultados son de esa preparación; no sustituyen las pruebas de una
compilación futura ni una validación completa satisfactoria.

## Referencias del repositorio

- [DEVELOPMENT.md](DEVELOPMENT.md): entorno, pruebas y migraciones.
- [OPERATIONS.md](OPERATIONS.md): servicio, registros, respaldos y recuperación.
- [Publish-WarehouseEpiRelease.ps1](../scripts/release/Publish-WarehouseEpiRelease.ps1).
- [Update-WarehouseEpiService.ps1](../scripts/release/Update-WarehouseEpiService.ps1).
- [Rollback-WarehouseEpiService.ps1](../scripts/release/Rollback-WarehouseEpiService.ps1).
- [Invoke-WarehouseEpiBackup.ps1](../scripts/security/Invoke-WarehouseEpiBackup.ps1).
