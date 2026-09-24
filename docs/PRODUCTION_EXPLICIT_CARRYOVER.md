# Arrastre explícito por semana

## Operación

En **Programa semanal → Arrastre inicial**, selecciona las cantidades de pendientes anteriores que entrarán al lunes. La selección empieza vacía. Cada fila identifica SKU, unidad, semana y renglón de origen; Corte, Costura y Ready to Pack se capturan por separado. No se suman áreas porque pueden representar trabajo pendiente sobre las mismas piezas.

En borrador, las selecciones se revisan y guardan junto con el programa nuevo. Cada selección modificada de origen/área cuenta como un cambio del máximo de 100. El grupo completo usa una transacción serializable y un identificador idempotente. Deshacer recupera la selección guardada; cero la retira. Al copiar otra semana se pueden preparar sus productos, sus pendientes de arrastre o ambos: cada SKU de arrastre se elige por separado y comienza con todo el disponible por origen/área, editable antes de incorporar. Una semana origen en borrador solo aporta productos. Las selecciones existentes se conservan. La preparación local está separada por usuario y semana y nunca guarda el NIP.

Una semana abierta permite corregir la selección con ADMIN, NIP de la sesión y motivo. La revisión muestra saldo inicial y cierre antes/después. No se pueden retirar cantidades consumidas (incluidas capturas por conciliar) o comprometidas en semanas posteriores. Las semanas cerradas son de consulta. Los orígenes abiertos se identifican como provisionales.

## Cálculo

La modalidad explícita mide **trabajo pendiente**, separado de la disponibilidad física:

`saldo del área = arrastre admitido del área + programación acumulada aplicable − capturas efectivas acumuladas de la semana`.

Programar 100 piezas genera 100 pendientes en cada proceso aplicable a la ruta. No exige haber capturado Corte para mostrar trabajo pendiente en Costura. El saldo conserva el signo para no contar nuevamente capturas adelantadas al cambiar de día; los pendientes positivos, adelantos y extras se identifican por separado. T1 y T2 se calculan con sus propios cortes. Los reversos no cuentan como producción efectiva.

La captura materializa únicamente órdenes propias o raíces admitidas por área; los extras y registros por conciliar conservan su flujo existente. Incorporar arrastre no crea otra orden ni producción o movimientos de inventario. Las reservas para semanas posteriores no están disponibles para consumirlas silenciosamente en el origen.

Tabla y balance, revisión de cambios, cierre y exportación comparten el cálculo. La exportación añade **Arrastre inicial**, con origen e identificador de renglón y versión revisada. Los totales permanecen separados por unidad y área.

## Persistencia y compatibilidad

La migración `20260924144853_ExplicitWeeklyCarryover` incorpora `production_schedule_weeks.explicit_carryover` y `production_week_openings`. Cada admisión conserva semana destino, semana origen, renglón raíz, producto, área, cantidad, huella de disponibilidad y versión. Las revisiones existentes auditan guardados y correcciones (`opening-corrected`).

Las semanas creadas desde Programa semanal usan la modalidad explícita. La migración convierte semanas manuales no cerradas sin capturas ni actividad de producción vinculada. Conserva la modalidad anterior en históricos importados, semanas cerradas y semanas con actividad, incluidos reversos. Las antiguas intenciones de `production_carryover_plans` permanecen como evidencia: no se convierten en saldos iniciales ni se muestran como seleccionadas en la modalidad nueva.

Se revalidan disponibilidad y versiones al guardar, publicar y capturar. Un cambio en el origen exige revisar; no se reduce ni sustituye automáticamente la selección. Una operación con respuesta incierta se comprueba o reintenta con el mismo identificador antes de habilitar otra confirmación.

## Referencia del Excel

Se revisó `Production Schedule Report 2026.xlsx`, hoja `09-21 TO 09-27`, estructura `T6:AK7`: programación nueva, apertura por área, producción por turno y pendientes. Las fórmulas de continuidad pertenecen a la misma hoja/semana; no autorizan trasladar automáticamente otra semana. El archivo se usó como referencia y no se modificó ni importaron sus anotaciones ambiguas.

## Verificación

Las pruebas `ProductionExplicitCarryoverTests` cubren selección 40 de 152, independencia entre áreas, cero sin selección, reintentos, correcciones, consumo, balance diario/cierre/exportación, capturas por conciliar y límite combinado de 100/101. El escenario se ejecuta también en PostgreSQL aislado mediante `ProductionBalanceEditPostgreSqlTests`. Las regresiones de modalidad anterior usan fixtures explícitamente históricos.

Las pruebas JavaScript de `production-week-openings.test.cjs` cubren selección, deshacer, decimales, recuperación, cambios en origen y respuesta perdida sin almacenar NIP. La validación física con tablet Android, teclado abierto y escáner se realiza por separado y no se sustituye por estas pruebas.

La migración debe aplicarse con el procedimiento normal de despliegue antes de usar el código nuevo. Las pruebas no aplican migraciones a la base operativa.
