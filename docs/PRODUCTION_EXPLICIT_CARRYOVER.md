# Arrastre inicial por SKU y área

En Programa semanal, cada SKU tiene un único total inicial para Corte, Costura y Ready to Pack. Las tres áreas se pueden editar en cualquier semana no cerrada, incluidas las importadas, aunque no exista semana anterior, el origen no tenga pendientes o el total sea cero. Las cantidades son no negativas y respetan la unidad y la precisión de cuatro decimales. Las áreas no se suman entre sí.

## Preparación y copia

Buscar un SKU o copiar productos lo incorpora a la misma tabla de planeación. Si hay varios pedidos del mismo SKU, el arrastre se edita una sola vez en su fila principal. También aparecen productos que tienen solamente arrastre, sin programación nueva.

Copiar semana y marcar Traer arrastre precarga el pendiente de trabajo por área de la semana seleccionada. Los saldos negativos aportan una sugerencia de cero. La cantidad escrita sustituye el total del área y puede superar la sugerencia. Repetir la copia conserva cantidades ya editadas, incluyendo ceros; un total previamente guardado tampoco se sustituye por otra precarga. No se dividen cantidades entre copiado y manual.

Los controles para incluir arrastre por producto y Traer arrastre permiten excluir sugerencias de la copia conservando los valores preparados. Los tres campos permanecen editables, también al copiar solo productos. Escribir manualmente un área incluye su total en el guardado y lo protege frente a exclusiones o copias posteriores; las sugerencias no editadas mantienen sus exclusiones. La recuperación conserva esta distinción y los ceros. En preparaciones anteriores sin esta información, se conservan los valores incluidos y las exclusiones explícitas hasta editar el área. La recuperación local conserva cantidades, ceros y selecciones por usuario y semana; no guarda el NIP. Las preparaciones del contrato anterior se recuperan consolidando sus renglones de origen por SKU y área.

Se puede guardar solamente arrastre. Cada área modificada cuenta como un cambio junto con los cambios de programación, sin el antiguo límite conjunto de 100. Los cambios de una semana abierta requieren motivo, revisión y NIP ADMIN correspondiente a la sesión. Borradores mantienen sus permisos existentes y las semanas cerradas son de consulta.

## Total definitivo y balance

La tabla production_initial_balances conserva cantidad absoluta y versión, con clave única por semana, producto y área. El cero se guarda explícitamente. Si hay un registro, su cantidad es el total definitivo; las admisiones antiguas y las aperturas importadas de otras fechas no se vuelven a sumar a esa área. Sin registro, los históricos conservan su cálculo anterior hasta su primera edición.

Saldo firmado del área = total inicial + programación nueva acumulada aplicable − producción efectiva acumulada de la semana.

La corrección permite reducir el total por debajo de lo producido o de compromisos posteriores. Conserva las capturas y muestra la diferencia con el saldo firmado, adelantos y extras existentes. Los reversos no cuentan como producción efectiva. Programa, balance diario y semanal, indicadores, cierre y exportaciones consumen el mismo total. Las copias futuras toman el pendiente resultante de la semana corregida.

Editar arrastre modifica planeación; no crea órdenes, capturas ni movimientos de inventario. Los vínculos históricos de production_week_openings y las asignaciones físicas permanecen para trazabilidad y conciliación. La disponibilidad física se calcula por separado y no se inventa a partir del total inicial.

El guardado es transaccional e idempotente, con versiones de semana y área y auditoría de cantidades antes/después, incluido el valor calculado antes de la primera edición. Un conflicto conserva la preparación y exige actualizar y revisar. Los reintentos de contratos anteriores conservan su huella idempotente.

## Exportación

La hoja Arrastre inicial incluye una fila por SKU y área, unidad, cantidad definitiva y versión, incluyendo ceros. Las hojas de balance y cierre muestran el saldo corregido. Las líneas y aperturas importadas conservadas en el programa original son evidencia histórica; no incrementan el total definitivo ni representan existencias nuevas.

## Migración

La migración 20261001173021_EditableInitialCarryover crea production_initial_balances y consolida las admisiones explícitas existentes de semanas no cerradas por semana, producto y área. No duplica cantidades, elimina vínculos ni modifica semanas cerradas. Los históricos sin admisión explícita mantienen el cálculo previo hasta su primera edición.

La migración está preparada para revisión y fue comprobada en PostgreSQL aislado. No se aplica a la base operativa ni se publica, inicia o reinicia la aplicación operativa como parte de esta modificación.

## Verificación

ProductionInitialBalanceTests y ProductionInitialBalancePostgreSqlTests cubren semanas nuevas, borradores, abiertas e importadas; guardado sin programación ni origen; cero persistente; cantidades superiores a sugerencias; decimales por unidad; independencia de áreas; saldo inferior a lo producido; aperturas históricas con distintas fechas; versiones; límites; reintentos anteriores; consolidación de migración; concurrencia y rollback. ProductionInitialBalanceRouteTests comprueba ADMIN, revisión, NIP, SKU con varios pedidos y copias desde saldo cero.

production-initial-balances.test.cjs cubre precisión, copia repetida, recuperación y consolidación de preparaciones anteriores. production-initial-balances.browser.cjs usa fixtures y solicitudes interceptadas, sin servidor operativo: edición y recuperación, un total por área, Enter y foco, consulta de semana cerrada y tamaños 768/899/900/1199/1200/1440 en temas claro, oscuro y sistema. La validación física de tablets y escáner sigue siendo una comprobación separada.

## Antecedentes

La migración 20260924144853_ExplicitWeeklyCarryover introdujo admisiones por origen. Ese modelo mantiene los vínculos físicos y la compatibilidad de los endpoints anteriores; sus restricciones de origen no limitan los nuevos totales editables.

El archivo Production Schedule Report 2026.xlsx se usó como referencia histórica de programación, apertura por área, producción por turno y pendientes. No se modificó ni se importaron sus anotaciones ambiguas.
