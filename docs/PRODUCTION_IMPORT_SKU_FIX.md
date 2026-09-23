# Corrección de identificación de SKU en la importación diaria

Fecha: 2026-09-21.

## Diagnóstico

Los registros del servidor de las 10:37:38 y 10:38:32 registraron
`ArgumentException: An item with the same key has already been added. Key: bp20`
en la previsualización de `/Admin/Production/ScheduleImport`.

El importador empleaba la misma normalización para encabezados del Excel y SKU.
Esta eliminaba caracteres que forman parte de la identidad del producto:
`BP20`, `BP-20` y `BP 20` se convertían en `bp20`. La construcción del diccionario
fallaba antes de procesar las hojas. La misma operación existía en confirmación.

El diagnóstico anterior había reconocido las cinco semanas del archivo real en
una base aislada, pero no había aplicado una corrección al importador.

## Corrección

- Usar para SKU únicamente recorte de espacios exteriores y mayúsculas
  invariantes, conforme al contrato del catálogo.
- Conservar guiones, espacios interiores, acentos y signos. Los SKU distintos
  continúan representando productos distintos; no se elige el primero ni se
  fusionan productos.
- Aplicar la misma regla al catálogo, programa, ejecución y arrastre, tanto en
  previsualización como al confirmar.
- Mantener la normalización flexible exclusivamente para encabezados, áreas,
  turnos, días y nombres de hojas.
- Si un SKU no coincide con el catálogo, mostrar el bloqueo existente de SKU
  sin resolver. Una diferencia de puntuación requiere corregir el dato de origen.
- Volver a comprobar la disponibilidad de los SKU antes de guardar, para que
  un producto desactivado o renombrado después de previsualizar no provoque una
  excepción ni una importación parcial.

## Localización

Se sigue `docs/LOCALIZATION.md`. Se reutiliza `SKU sin resolver: {0}.`, ya
integrada con `ProductionDailyText` y recursos `ProductionTexts` ES/EN. Los SKU
conservan su valor original como argumento; no se agregan claves interpoladas.

## Verificación y operación

Las pruebas de regresión cubren coexistencia de SKU con distinta puntuación,
recorte y mayúsculas, asociación por ProductId de programa/capturas/arrastre,
reintento idempotente, rechazo de una coincidencia con puntuación distinta y
desactivación posterior a la previsualización sin escrituras parciales.

La corrección no requiere migración ni modificaciones de productos existentes.
Para que la instalación operativa la utilice, debe publicarse la compilación
corregida mediante el procedimiento de despliegue. La revisión de código y las
pruebas aisladas no equivalen a desplegarla ni a confirmar el archivo en la base
operativa.

Verificación automatizada completada: compilación correcta y 30 pruebas
aprobadas, 0 fallidas, 0 omitidas (módulo diario y localización). Se ejecutó:

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --artifacts-path .artifacts/daily-production -p:UseAppHost=false --no-restore --filter "FullyQualifiedName~ProductionDailyModuleTests|FullyQualifiedName~LocalizationTests" --logger "trx;LogFileName=import-sku-fix.trx" --results-directory .artifacts/import-results
```

Estado de entrega: corrección en código verificada; despliegue e importación en
la base operativa pendientes. No se aplicaron migraciones ni se reinició el servicio.
