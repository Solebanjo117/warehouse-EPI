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
mantiene una baseline explícita de reglas heredadas (migraciones inmutables,
comparaciones culturales existentes y convenciones de nombres de pruebas); no
debe extenderse para cambios nuevos y se reducirá durante la fase 10.3.

En GitHub Actions, el workflow `Quality` usa una instancia PostgreSQL efimera y
solamente la base `warehouse_epi_test`. Sus artefactos incluyen resultados TRX,
cobertura Cobertura y el SQL idempotente de migraciones.

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
