# Conciliación de apertura del programa diario

## Propósito

La importación inicial conserva las tablas de programa y las capturas históricas del Excel. La semana destino queda en borrador. No publica órdenes ni crea movimientos de inventario.

La apertura se concilia antes de confirmar. La previsualización muestra las líneas finales que se escribirán; la confirmación no calcula ni añade arrastres nuevos.

## Flujo ADMIN

1. Selecciona el XLSX o abre uno de tus borradores guardados.
2. Resuelve configuración, vínculos de catálogo y filas inválidas.
3. Revisa `Conciliar apertura`, inicialmente filtrado a los SKU que requieren revisión.
4. Para cada discrepancia, registra la cantidad física que inicia en Corte, Costura o Ready to Pack y un motivo. Escribir cero en las tres áreas declara explícitamente que no existe pendiente.
5. Revisa las líneas finales de la semana destino y confirma. Si la revisión o sus dependencias cambian, vuelve a validar.

Cada borrador pertenece al ADMIN que lo creó. Cada guardado conserva una revisión inmutable. Solo el propietario puede continuar, descartar, eliminar o confirmar. Abrir la revisión no la modifica.

Desde la lista `Borradores e importaciones guardadas`, el propietario puede eliminar un borrador en revisión, listo o descartado, previa confirmación. Se borran el archivo y todas sus revisiones y no se puede deshacer. Las importaciones confirmadas no muestran esa acción y el contexto de datos rechaza su borrado: siguen siendo el registro de la carga.

## Fuentes y reglas

El lector reconoce las cabeceras en español e inglés y usa la tabla de cierre `PendienteProximaSemana` o `PendingNextWeek`, incluidos sus sufijos numéricos. Si hay más de un cierre candidato, el ADMIN elige uno. El cierre se toma como lo dejaron los operadores: los datos históricos no se concilian. Solo bloquea lo que no se puede leer: una tabla ausente, ambigua o con columna faltante, una fila duplicada o una celda vacía, con texto, error o fórmula sin resultado guardado. Un producto que no aparece en el cierre abre en cero y un pendiente negativo cuenta como cero, igual que el `MAX(0, …)` del Excel.

La evidencia conserva hoja, tabla, fila, celda, cabecera, valor almacenado y fórmula. Las columnas `NOTES`, `Column1` y `Column2` se presentan como evidencia y nunca se convierten en producción.

Los pendientes por proceso no se triplican. El pendiente de Ready to Pack es el total sin terminar y un área anterior no puede tener más pendiente que la siguiente, así que manda Ready to Pack: `S' = min(S, R)` y `C' = min(C, S')`. Los paquetes físicos son:

- Corte: `C'`.
- Costura: `S' - C'`.
- Ready to Pack: `R - S'`.

Con pendientes coherentes `C ≤ S ≤ R` equivale a `C`, `S - C` y `R - S`. El importador no usa rutas: siempre considera las tres áreas, y al publicar el programa cada orden se ajusta a la ruta opcional del producto. Tipo vacío, notas auxiliares, adelantos, arrastre del lunes y diferencias de saldo quedan como evidencia sin bloquear. Un arrastre ya escrito en la semana destino se sustituye por el del cierre. La conciliación manual ADMIN sigue disponible y tiene prioridad.

## Protección al confirmar

La huella revisada cubre el archivo, algoritmo, soluciones, líneas finales y dependencias de catálogo y configuración. La confirmación usa una transacción serializable y verifica propietario, versión, huella, archivo, semanas existentes y operación idempotente. Un reintento idéntico devuelve el resultado confirmado; una operación o archivo reutilizado con contenido distinto se bloquea.

La migración `20260921190902_PersistentProductionImportReconciliation` agrega `production_import_drafts` y `production_import_revisions`. No se aplicó a `warehouseEPI` como parte de esta implementación.

## Límites de aceptación

La conciliación acredita la forma en que el sistema guardará el archivo, no la existencia física del material. La aceptación operativa requiere que un ADMIN confirme las cantidades pendientes por proceso y que se pruebe el flujo en navegador, tablet e instancia de planta.

## Control del archivo inicial de 2026

La auditoría de solo lectura del archivo recibido verificó la huella SHA-256 `871670AB1838DFC029200FB633158A0C6BA5C0B51DD2153C1E34C2379BADC189`, sin modificarlo. Sus controles son 166 líneas, 270 capturas, 19 líneas y 3141 piezas en la semana del 21/09, y 28 saldos de cierre positivos en 10 SKU, por un total de 2402 en Corte, 2336 en Costura y 2288 en Ready to Pack.

El archivo se bloquea para confirmación hasta conciliar los SKU. El control no infiere paquetes físicos ni aplica una migración, no importa datos y no accede a la base operativa.
