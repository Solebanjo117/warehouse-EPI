# Programa semanal de producción

Las semanas nuevas usan **Arrastre inicial explícito**: sin selección, el saldo inicial es cero. Consulta [Arrastre explícito por semana](PRODUCTION_EXPLICIT_CARRYOVER.md) para preparación, correcciones, cálculo y migración. Las referencias a «intención de arrastre» de este documento describen únicamente la modalidad anterior conservada para históricos.

La semana operativa inicia el lunes y termina el domingo. En semanas en borrador, «Programa por día» incluye una cuadrícula de SKU por lunes a domingo, con vista de un día para tablet. Buscar o escanear enfoca una fila existente cuando hay una sola; si hay varias, permite elegir. El pegado desde Excel acepta `SKU + lunes…domingo` y `Día + SKU + Cantidad`, con pedidos y notas opcionales. Ambas formas abren una preparación revisable. Al copiar una semana anterior se pueden elegir productos, arrastre o ambos. Por defecto se preparan SKU y días con cantidades vacías; incluir cantidades anteriores es opcional. El arrastre ofrece los pendientes disponibles de esa semana por SKU y área, con cantidades editables y sin sobrescribir selecciones anteriores. Las repeticiones de SKU en programación permanecen separadas.

La preparación del borrador permite agregar, modificar y quitar renglones guardados. Hasta 100 cambios diarios se confirman juntos en una transacción, con versiones y auditoría; la publicación sigue separada. La preparación se recupera en el mismo navegador por usuario y semana, sin NIP. Si la semana cambió, se muestra el valor actual frente al propuesto y se exige conciliar antes de confirmar. Después de un reintento se comprueba el identificador de operación para evitar duplicados. En semanas abiertas permanece el formulario de captura por grupo con NIP ADMIN; en semanas cerradas sólo hay consulta. «Productos de la semana» reúne los renglones nuevos de la tabla `WEEKLY PART NUMBER PLAN` del Excel, ordenados por día y paginados sin fusionar SKU repetidos; cada fila enlaza con su día. «Resumen del plan» muestra los siete días y el total por unidad. La pantalla de configuración general de procesos y turnos se abre desde «Más».

Los renglones de programación nueva pueden eliminarse desde su día si no tienen capturas históricas ni producción vinculada. La eliminación marca el renglón como cancelado y conserva su revisión auditada. En una semana abierta también cancela la orden y libera solicitudes y reservas; si hay material surtido, solicita NIP ADMIN y revierte los movimientos dependientes en una transacción. Si no puede revertirlos con seguridad, conserva la programación. Las aperturas importadas y las semanas cerradas permanecen en consulta.

## Correspondencia con Production Schedule Report 2026.xlsx

| Columna o tabla del Excel | Programa semanal |
| --- | --- |
| Week Start Date / Week End Date | Encabezado de semana, lunes a domingo |
| Day | Pestaña del día y fecha editable del renglón |
| WEEKLY PART NUMBER PLAN | Vista «Productos de la semana», con una fila por renglón nuevo y acceso al día correspondiente |
| Part Number, Qty | Producto y cantidad, con unidad del catálogo |
| Order Number 1, 2, 3 | Referencias de pedido independientes |
| NOTES | Notas del renglón |
| Tipo | Tipo original conservado y visible, aun si está vacío |
| Column1, Column2 | Anotaciones conservadas, con tipo de origen Text, Date o Number |
| Total Qty, Customer Order Qty, Inventory Stock Qty | Resumen diario y semanal por unidad |
| Customer Lines, Stock Lines | Conteo de renglones nuevos, sin fusionar SKU repetidos |
| Arrastre semana anterior | Apertura importada separada de programación nueva |
| Arrastre programado | Intención de trabajar pendiente anterior, separada del saldo físico |

Un renglón nuevo cuenta como cliente si cualquiera de las tres referencias contiene texto no vacío; de lo contrario cuenta como inventario. Esta regla corrige la fórmula del Excel que clasifica únicamente por Order Number 1. Las cantidades de unidades distintas se muestran por separado. El arrastre importado y el programado no se agregan a la producción nueva.

Los textos como «YA CORTADO» se conservan como evidencia. No generan movimientos ni asignan automáticamente un proceso. La importación del archivo histórico sigue limitada a sus cinco semanas iniciales de 2026; ahora admite fechas de domingo y preserva los metadatos originales.

La migración `SevenDayProductionSchedule` extiende las semanas existentes al domingo y cambia la restricción de fechas. La reversión se bloquea si existen líneas, capturas o arrastres programados en domingo; esos datos deben revisarse antes de volver a un calendario de seis días.
