# Instalación de Warehouse EPI en un servidor Windows

El responsable del desarrollo entrega un ZIP **Server** ya compilado. IT no
necesita clonar Git, instalar el SDK de .NET, compilar ni configurar User Secrets.
El asistente prepara la base, configuración privada, permisos, servicio y
respaldos. Sirve para una instalación nueva o para migrar los datos actuales.

## 1. Preparar el servidor

IT prepara una máquina Windows de 64 bits con:

- PowerShell **7.4 o posterior** y `curl.exe`.
- PostgreSQL **18** local, en `C:\Program Files\PostgreSQL\18`, puerto `5432`.
  Debe conocer la contraseña de `postgres`. La base `warehouseEPI` todavía no
  debe existir. El administrador PostgreSQL debe poder crear bases temporales
  para comprobar restauraciones.
- Un nombre DNS, por ejemplo `almacen.empresa.com`, dirigido al servidor.
- Un certificado HTTPS vigente para ese nombre, con clave privada **RSA**,
  instalado en `Cert:\LocalMachine\My`, y su thumbprint. Los dispositivos
  deben confiar en su emisor; IT se encarga de obtenerlo y renovarlo.
- Puertos `80` y `443` libres. La aplicación sirve HTTPS directamente con
  Kestrel. IT habilita `443` únicamente desde la red autorizada o VPN; puede
  habilitar `80` en ese mismo perímetro para la redirección a HTTPS.

Esta ruta conserva el despliegue Windows actual, también en una VM de nube.
Linux, PostgreSQL remoto/administrado, IIS, proxies y balanceadores requieren
otra configuración y no forman parte de este asistente. La aplicación conserva
consultas sin inicio de sesión concebidas para una red privada.

Para **migrar**, el responsable de la instalación anterior entrega además:

- Un ZIP nuevo de migración y su `.sha256`, creados con
  `scripts/security/New-WarehouseEpiMigrationBackup.ps1`. Incluye PostgreSQL,
  referencias del croquis y logo.
- La misma `Security:PinLookupKey`, por un canal seguro separado. No se coloca
  dentro del paquete ni se envía por Git o documentación.
- Un NIP conocido de la base anterior para comprobar que la clave corresponde.

La operación sin conexión está pendiente: en nube, el almacén dependerá de su
conexión al servidor. Dimensionar CPU, memoria y disco requiere medir el volumen
de datos y usuarios; este paquete no fija una capacidad de servidor.

## 2. Copiar el paquete y ejecutar el asistente

Copiar el ZIP Server y su `.sha256` desde el medio autorizado. Antes de
descomprimir, comprobar que el hash coincide con el que entregó desarrollo:

```powershell
Get-FileHash .\WarehouseEPI-<version>-server.zip -Algorithm SHA256
Get-Content .\WarehouseEPI-<version>-server.zip.sha256
```

Descomprimir en una carpeta local. Abrir **PowerShell 7.4 o posterior como administrador**,
entrar en esa carpeta y ejecutar:

```powershell
pwsh -NoProfile -File .\Install.ps1
```

El asistente pide nombre DNS, thumbprint y si se trata de una base nueva o una
migración. La contraseña de PostgreSQL se escribe oculta una sola vez. En una
migración también pide el ZIP y un NIP de comprobación. Usa la clave original
de `pinlookupkey.json` si se recuperó junto al ZIP de un respaldo manual;
comprueba que su SHA-256 corresponda al paquete. Si ese archivo no existe,
pide la clave original de forma oculta. Para
una base nueva genera las claves y pide crear el primer administrador.

Comprobar solo el paquete y las herramientas, sin privilegios elevados,
contraseñas ni cambios en el servidor:

```powershell
pwsh -NoProfile -File .\Install.ps1 -CheckOnly
```

`-CheckOnly` no prueba DNS, certificado, red, credenciales o restauración; esos
controles ocurren durante la instalación y la aceptación de IT.

El asistente valida los hashes, rechaza bases existentes ajenas al procedimiento,
prueba la restauración de una migración en una base temporal, hace un respaldo
antes de aplicar el SQL revisado del paquete y compara las migraciones con la
Release. Instala el servicio y exige que responda el health local.

La configuración y datos persistentes quedan en `C:\ProgramData\WarehouseEPI`.
La cuenta `NT SERVICE\WarehouseEPI` ejecuta el servicio. El asistente deja
respaldo diario a las **02:00** y prueba de restauración los domingos a las
**03:00**, según la hora del servidor; IT debe confirmar su zona horaria. Los
scripts de las tareas se copian a `Maintenance`, por lo que no dependen de la
carpeta donde se descomprimió el instalador. Estos respaldos son locales: IT
debe configurar su copia externa cifrada y comprobar también la protección
del logo y de los archivos del croquis.

Conservar también `Security:PinLookupKey` en el gestor de secretos o respaldo
cifrado de IT. La base restaurada depende de esa clave aunque el servidor se
pierda; el asistente la guarda en la configuración privada y no la imprime.

### Respaldo inmediato desde la aplicación

Un ADMIN abre **Administración → Respaldos**, elige una contraseña de 12 a 128
caracteres, la confirma y pulsa **Crear respaldo ahora**. Puede continuar la
operación mientras se genera. Al terminar, **Descargar respaldo** entrega un
archivo `.webackup` cifrado que incluye PostgreSQL, logo, fondos del croquis y
la `Security:PinLookupKey` efectiva de esa instalación. La contraseña se
necesita para abrirlo y se entrega a IT por separado.

El servicio usa su conexión actual y permisos de lectura, sin credenciales
administrativas de PostgreSQL. Mantiene una transacción Repeatable Read y
exporta una instantánea compartida con `pg_dump --snapshot`; copia únicamente
los archivos referenciados en esa instantánea y comprueba sus hashes. Si falta
un archivo o cambia durante la copia, el trabajo falla y no ofrece una descarga
incompleta. El dump se comprueba con `pg_restore --list`: esto verifica su
estructura, **no sustituye una prueba de restauración**.

Los archivos quedan fuera de `wwwroot`, en
`C:\ProgramData\WarehouseEPI\ManualBackups`, con permisos para SYSTEM,
Administradores y `NT SERVICE\WarehouseEPI`. Se conservan los cinco respaldos
manuales más recientes. Los temporales se eliminan al completar o fallar el
trabajo; una terminación forzada puede dejar temporales privados que IT debe
retirar después de verificar que no hay un trabajo activo. Cada trabajo tiene
un límite de 30 minutos. Los respaldos programados diarios siguen su propio
procedimiento y retención.

La instalación o actualización del servicio crea la carpeta y sus permisos.
Para habilitarla en una instalación anterior, IT puede ejecutar una sola vez
`scripts/security/Initialize-WarehouseEpiManualBackups.ps1` en PowerShell 7.4
como administrador. PostgreSQL 18 debe incluir `pg_dump.exe` y `pg_restore.exe`;
si están en otra ubicación, configurar `Backups:PgDumpPath` y
`Backups:PgRestorePath` con rutas absolutas de ejecutables confiables. La cuenta
de la aplicación necesita lectura de todas las tablas y secuencias respaldadas,
incluida `__EFMigrationsHistory`; el aprovisionamiento existente ya la concede.

IT abre el archivo en una **carpeta nueva y privada**:

```powershell
pwsh -NoProfile -File .\scripts\security\Open-WarehouseEpiManualBackup.ps1 -PackagePath C:\Transferencia\respaldo.webackup -DestinationDirectory C:\RecuperacionEPI
```

La herramienta pide la contraseña oculta, verifica la autenticidad antes de
descifrar y recupera `WarehouseEPI-migration.zip`, su `.sha256` y la clave
pareada `pinlookupkey.json`. Protege la carpeta por ACL y verifica el manifiesto
de migración. Esa carpeta contiene datos y la clave **en claro**: conservarla
privada. El ZIP recuperado funciona con el asistente de instalación y con los
scripts de restauración existentes. IT debe ensayar la restauración en un
destino aislado antes de usarlo para migrar. Credenciales de PostgreSQL y el
certificado HTTPS se configuran en el destino; no se incluyen en el respaldo.

Formato portátil v1: cabecera `WEPBK001`, salt de 16 bytes, IV de 16 bytes,
iteraciones UInt32 little-endian, contenido ZIP cifrado AES-256-CBC/PKCS7 y
HMAC-SHA256 de cabecera + ciphertext. PBKDF2-SHA256 con 600000 iteraciones
deriva 64 bytes (32 para AES, 32 para HMAC). El ZIP cifrado contiene únicamente
`migration.zip` y `secrets.json`; este último enlaza el SHA-256 del paquete con
la PinLookupKey. Al crear el respaldo, la aplicación no conserva la contraseña,
ni escribe la clave en un archivo temporal sin cifrar, ni expone errores crudos
de PostgreSQL.

### Importar todo desde la aplicación

Un ADMIN abre **Administración → Respaldos → Cargar respaldo**, selecciona el
`.webackup` y coloca su contraseña. La aplicación valida el cifrado, los hashes,
la base y los archivos del logo/croquis, y muestra fecha, versión y tamaño.
Durante esta revisión puede continuar la operación. Se admite hasta **2 GB**
por archivo; los contenidos expandidos tienen límites adicionales. No se
importan ZIP de migración ni archivos `.dump` directamente desde esta pantalla.

Para confirmar se requiere el **NIP de un ADMIN activo incluido en el respaldo**,
marcar que se reemplazarán todos los datos y escribir **IMPORTAR**. La revisión
vence en 30 minutos; cargar otra copia descarta la revisión anterior de ese
administrador. La restauración sustituye toda la información: usuarios/NIP,
catálogos, existencias, movimientos, producción, reportes, configuración del
negocio, logo y croquis. Las capturas posteriores a la fecha del respaldo no
se mezclan con la copia importada. El DNS, certificado y credenciales PostgreSQL
del servidor destino se mantienen; la **PinLookupKey se toma del respaldo**.

Después de confirmar, se bloquean las operaciones y aparece una página de
mantenimiento. Una tarea local, **WarehouseEPI-Restore**, atiende la solicitud
en el siguiente minuto. Restaura primero en una base candidata con una cuenta
temporal sin privilegios de administrador, comprueba las migraciones exactas de
la Release instalada, el NIP y los archivos vinculados. Una copia de otra
estructura requiere preparar la versión compatible con IT; la pantalla no
ejecuta migraciones del esquema automáticamente.

Solo entonces se detiene el servicio, se guarda el estado anterior y se
cambia la base. Se actualiza la clave de NIP y se renuevan las claves de sesión;
todos deben volver a ingresar con los NIP del respaldo. La página intenta
reconectarse durante el reinicio. Si falla un cambio o el arranque, se intenta
recuperar la base, archivos, configuración y sesiones anteriores. El registro
permite recuperar una interrupción sin repetir la importación destructiva.
La pantalla avisa cuando termina; ante una recuperación incompleta se requiere
revisión de IT antes de reanudar capturas.

La instalación nueva activa esta tarea automáticamente. En una instalación
anterior, después de actualizar a una Release con esta pantalla, IT ejecuta
una sola vez **PowerShell 7.4 como administrador** desde los scripts entregados:

```powershell
pwsh ./scripts/security/Install-WarehouseEpiRestoreTask.ps1 -Force
```

El instalador coloca scripts protegidos en `C:\ProgramData\WarehouseEPI\Maintenance`;
la aplicación no puede modificarlos ni leer el `postgresql-backup.pgpass`.
La tarea se ejecuta como SYSTEM y requiere la configuración estándar de este
asistente: PostgreSQL 18 local, `warehouseEPI`, rol `warehouse_epi_app` y las
carpetas estándar en ProgramData. Para iniciar se exigen al menos 6 GB libres;
se comprueban también el espacio disponible y un límite de 10 GB para la
base candidata. Dimensionar el servidor para la base actual, la candidata,
archivos temporales y las copias de recuperación; 6 GB no es una estimación del
espacio total necesario. Si el agente no reporta actividad reciente, la
confirmación queda deshabilitada y la página indica que IT debe habilitarlo.

La validación deja temporalmente el paquete descifrado y su clave en
`ManualBackups\.restore\<id>`, accesible exclusivamente al servicio,
Administradores y SYSTEM. Se retiran al terminar, fallar o vencer la revisión.
Las copias anteriores se conservan en **Recovery\<id>**, accesibles solo a
Administradores y SYSTEM, junto con `journal.json`, el dump, los archivos y la
configuración privada. La base anterior se conserva como
`warehouseEPI_before_<id>` sin acceso desde el rol de la aplicación. Las copias
no se eliminan automáticamente: IT define su retención y copia externa cifrada.
No entregar ni publicar las carpetas `.restore` o `Recovery`; contienen secretos.

Si el servidor queda detenido, IT revisa la tarea, `agent-error.json` en `.restore`
y `failure.json`/`journal.json` en `Recovery\<id>`. No borrar bases, configuración
ni el marcador `maintenance.json` para reintentar. Una recuperación incompleta
requiere revisar qué base conserva el estado anterior y sus archivos pareados,
recuperar el conjunto y comprobar el servicio antes de retirar mantenimiento.
Probar este procedimiento en una VM con datos representativos antes del corte.

## 3. Verificar y hacer el cambio

Antes de cerrar la instalación:

1. Abrir `https://<nombre-DNS>/` desde una tablet sin advertencias de certificado.
2. Probar administrador y NIP, comparar existencias e historial, y verificar
   reportes, cámara, lector e impresión.
3. Revisar `/Admin/System`, las tres tareas programadas y la copia externa.
   Conservar versión, hashes y resultado de restauración, sin secretos.

Para una migración, probar primero con una copia. En el corte final, detener la
captura en el origen, crear un respaldo final y restaurarlo en un **destino
nuevo y vacío**. Un piloto que ya contiene `warehouseEPI` no se sobrescribe.
Mantener el origen sin nuevas capturas como alternativa de regreso hasta
aceptar la nueva instalación; ambos servidores no deben recibir movimientos
al mismo tiempo.

Si falla, corregir el problema indicado y volver a ejecutar **el mismo paquete**.
El estado permite continuar una preparación confirmada. Si la creación o
restauración quedó interrumpida antes de confirmarse, el asistente se detiene
para revisión de IT y nunca borra una base para reintentar. No borrar la base,
claves o configuración para resolver un error. Una instalación activa se
actualiza mediante `scripts/release/Update-WarehouseEpiService.ps1` y conserva
el procedimiento de rollback de Releases.

## Preparación del paquete: solo desarrollo

Desde un checkout limpio con la versión aprobada y verificada, publicar la
Release y generar el SQL completo idempotente **del mismo commit**. Revisar SQL
y probar una base nueva y una copia representativa antes de entregar el paquete.

```powershell
pwsh ./scripts/release/Publish-WarehouseEpiRelease.ps1 -Version <version>
dotnet ef migrations script --idempotent --project src/WarehouseEPI.Infrastructure --startup-project src/WarehouseEPI.Web --output artifacts/schema-reviewed.sql
pwsh ./scripts/server/New-WarehouseEpiServerBundle.ps1 -PackagePath ./artifacts/releases/WarehouseEPI-<version>-win-x64.zip -SchemaPath ./artifacts/schema-reviewed.sql
```

El ZIP Server se crea en `artifacts/server`, con la Release, SQL, instalador,
scripts de mantenimiento y esta guía. El empaquetador compara los IDs del SQL
con los incluidos por el publicador de Release. No admite Releases antiguas sin
esa metadata. Los respaldos y secretos se entregan aparte.

Para comprobar empaquetado, validaciones y el flujo del asistente en Windows
con PostgreSQL 18 instalado, sin modificar bases ni servicios:

```powershell
pwsh -NoProfile -File ./tests/powershell/ServerInstallation.Tests.ps1
```

Estas pruebas usan paquetes y servicios simulados; la aceptación en una VM
nueva y con datos representativos sigue siendo necesaria antes del corte real.

La guía detallada `INSTALLATION_NEW_LAPTOP.md` se conserva para instalaciones
desde código y configuraciones avanzadas.
