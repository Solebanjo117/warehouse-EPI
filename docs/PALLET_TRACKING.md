# Seguimiento operativo de pallets

## Estado de entrega

Implementación en código y migración aditiva `20260917162528_OperationalPalletTracking`. No se ha aplicado a la base operativa ni desplegado el servicio. Las pruebas PostgreSQL utilizan bases temporales independientes.

## Uso

- Los formularios operativos no solicitan captura manual de pallets. Entradas, recepciones documentales y recepciones de producción dejan toda la cantidad inicialmente como saldo sin placa.
- `/Operations/PalletLabels` es el único flujo operativo que identifica e imprime placas. Permite empezar por producto o por ubicación mediante los buscadores operativos; el segundo selector muestra únicamente combinaciones con stock positivo. Cada resultado conserva SKU, ubicación y stock total antes de **Imprimir placa**.
- La carga inicial muestra los 10 movimientos auditables más recientes de todo el almacén, sin filtrar por la consulta, con fecha local, tipo, producto, cantidad, ruta, responsable y estado de corrección. Cada fila ofrece **Imprimir placa**: los movimientos ya vinculados abren su placa actual; entradas o ajustes antiguos sin vínculo preparan la placa mediante un POST con antiforgery, idempotencia y bloqueo antes de redirigir a la previsualización.
- El operador no elige entre crear o reimprimir ni captura una cantidad: existe una sola acción **Imprimir placa** por producto. Cuando hay saldo identificable y ya existe una placa positiva en esa ubicación, el sistema lo incorpora a la misma placa; no crea una placa para la diferencia. La redirección baja hasta `#label-preview` y la impresión física sigue siendo una acción explícita.
- En salida, transferencia, WIP y producción, el sistema utiliza primero las placas positivas más antiguas y después el saldo sin placa. Respeta reservas y preparaciones; en una orden solo utiliza material asignado a esa orden.
- En una transferencia, solamente la porción ya identificada se une a la placa activa más antigua del mismo producto en el destino. Si no existe una placa destino, una placa trasladada completa conserva su identificador y una porción crea una placa vinculada. La porción sin placa permanece sin placa en el destino.
- Un traslado de exactamente todo el saldo positivo a pallet vacío conserva el identificador y cambia su ubicación. Un traslado parcial genera otra placa; un destino existente del mismo producto permite unión. No se admiten transferencias en la misma ubicación.
- Se permiten diferencias negativas con advertencia: trasladar 120 desde una placa con 100 deja origen −20 y destino 120. Esto no permite exceder reservas ni autorizaciones de producción. Cero significa **Agotada**; negativo significa **Con diferencia**. Las placas se conservan.
- La consulta `/Operations/PalletLabels/Tracking` presenta saldo, ubicación, estado, historial reciente, responsable, movimientos, placas relacionadas y órdenes vinculadas. Creación, previsualización y reimpresión usan exclusivamente la plantilla publicada `PLT-LICENSE-PLATE`; `PLT-TRACKED-PALLET` se conserva almacenada para compatibilidad histórica, pero este flujo no la resuelve. La impresión actual se solicita expresamente y contiene códigos separados de producto y placa.
- Abastecimiento resuelve por antigüedad y persiste las placas durante la preparación; la confirmación reutiliza exactamente esa composición aunque cambie el orden disponible después. La asignación WIP sin traslado conserva las placas. Consumo, merma y devolución operan automáticamente sobre las porciones reservadas para la orden.
- Conteos y ajustes capturan únicamente el total físico por producto. El ajuste automático actualiza la placa vigente al total contado y registra el antes/después contra el movimiento; por ejemplo, 46 → 50 imprime 50 y no una placa adicional de 4. Si existen duplicados positivos sin reservas, se conserva la placa más antigua, las secundarias quedan agotadas y la consolidación se audita. Una duplicidad con material reservado o preparado se bloquea para evitar reasignarlo silenciosamente.
- Las correcciones restauran los efectos sobre placas; las dependencias posteriores deben reversarse primero. No se borran placas ni eventos.
- Un ADMIN puede anular con NIP y motivo una identificación nueva únicamente si su único evento vigente es `Identification`. La placa queda anulada y vuelve a computar como saldo sin placa; inventario y lotes no cambian.

## Saldo libre para identificar

La pantalla muestra al operador producto, ubicación y stock total. Internamente, el saldo identificable excluye reservas activas de abastecimiento, material WIP perteneciente a órdenes y composiciones guardadas en preparaciones abiertas. La identificación bloquea la ubicación, recalcula esos valores y valida la versión esperada antes de distribuir la cantidad entre lotes por el orden interno vigente. Si hay una placa positiva la amplía; solo crea una cuando no existe ninguna. Crear, ampliar, consolidar o anular una identificación no registra un movimiento de inventario.

## Activación de placas antiguas

1. Tras aplicar la migración en un paso autorizado separado, abrir Seguimiento de placas → Activar una placa documental existente.
2. Capturar el folio de entrada o identificador original, ubicación y cantidad comprobadas físicamente, y NIP de operador o administrador.
3. El servidor valida saldo sin placa suficiente, distribuye los lotes disponibles según el orden vigente y conserva el identificador. Si falta saldo, conciliar previamente mediante ajuste; la activación nunca crea inventario.
4. El evento se identifica como verificación inicial. La asignación de lotes no constituye prueba de trazabilidad física anterior a esa verificación.

Las entradas históricas no se convierten automáticamente. Los enlaces por folio continúan disponibles y permiten elegir placas relacionadas. Los contratos explícitos antiguos de distribución se conservan para compatibilidad interna, pruebas y reversos; ningún formulario operativo los envía. El flujo histórico de producción sin lotes conserva su comportamiento.

## Persistencia y concurrencia

El inventario por producto, ubicación y lote sigue siendo la fuente de verdad. Para cada combinación, `inventario = cantidades identificadas + saldo sin placa`. Los caminos operativos automáticos mantienen una sola placa positiva por producto y ubicación mediante bloqueo transaccional; las placas agotadas o anuladas permanecen para conservar el historial.

`pallet_plates`, `pallet_plate_lots` y `pallet_plate_events` almacenan estado, composición y snapshots de operaciones/reversos. Las identificaciones sin movimiento de origen obtienen la fecha del evento `Identification`, conservan responsable nulo y muestran `Sin identificación de operador`; su referencia documental queda vacía. Versiones, bloqueos de ubicaciones y huellas de idempotencia protegen reintentos y operaciones concurrentes. Las selecciones también se persisten en abastecimiento y material entregado.

La migración original añade columnas/tablas y una plantilla operativa; no calcula existencias actuales a partir de entradas históricas. La migración aditiva `20260918173248_AllowAnonymousPalletIdentification` permite responsable nulo únicamente donde el modelo de eventos lo admite. Ninguna de estas migraciones se aplica automáticamente. Los valores JSON iniciales son listas vacías. No elimina ni sustituye la plantilla documental.

## Verificación y aceptación pendiente

Las pruebas `PalletTrackingTests`, `PalletTrackingPostgreSqlTests`, `PalletTrackingRouteTests` y la regresión focal de movimientos, recepción, producción, abastecimiento, WIP y conteos deben cubrir identificación total/parcial, antigüedad, saldo sin placa, unión/división, negativos, reservas, preparación persistida, versiones, idempotencia, anulación, reversos, historial y permanencia de las rutas de consulta/activación.

Verificación del flujo centralizado: compilación aislada correcta sin advertencias; **106/106** pruebas focales en memoria/HTTP, **3/3** pruebas PostgreSQL de placas (incluida concurrencia de identificación) y **4/4** pruebas PostgreSQL de material de producción. La validación sintáctica del JavaScript nuevo y `git diff --check` también finalizaron correctamente.

Pendientes de aceptación: recorrido visual completo en navegador sobre una instalación de prueba migrada; prueba física de tablet, lector HID, cámara y legibilidad de ambos códigos impresos. Las pruebas HTTP y PostgreSQL no sustituyen esas comprobaciones. Aplicación de migración y despliegue son pasos separados y no se realizaron como parte de este cambio.
