# Seguimiento integral de órdenes de producción

Decisiones operativas actualizadas: 16 de septiembre de 2026. P1–P6 están implementadas en código. P5/P6 tienen verificación automatizada; las migraciones P4–P6, el despliegue y la aceptación física se reportan por separado.

## 1. Propósito

Este documento define el seguimiento operativo de una orden de producción desde que se solicita fabricar un producto hasta que el producto terminado se recibe en bodega. El flujo integra productos, rutas, recetas, materiales, ubicaciones WIP, lotes, movimientos de inventario, procesos y analítica.

La meta es que una orden responda, con movimientos y responsables verificables, estas preguntas:

- ¿Qué producto se fabricará y en qué cantidad?
- ¿Qué materiales se necesitan y cómo se calculó cada cantidad?
- ¿En qué ubicaciones y lotes está disponible cada material?
- ¿A qué WIP debe enviarse cada material?
- ¿Qué cantidades están pendientes, surtidas, consumidas, devueltas o reservadas?
- ¿En qué proceso está cada lote de producción?
- ¿Cuánto producto bueno, retrabajo y merma produjo cada proceso?
- ¿Cuánto producto terminado recibió realmente bodega?
- ¿Qué lotes de materia prima formaron parte de un lote terminado?
- ¿Dónde se producen atrasos, faltantes o diferencias contra la receta?

## 2. Idea operativa

Un producto terminado tiene una ruta secuencial, por ejemplo:

```text
Corte → Costura → Martillado → Sellado → Empaque → Bodega
```

También tiene una receta versionada. La receta declara cuánto producto terminado se obtiene con una cantidad concreta de materiales y en qué proceso se incorpora cada material.

Ejemplo:

```text
Producto terminado: Bolsa industrial
Cantidad base producida: 10 piezas

Tela             25 m    se incorpora en Corte
Hilo              2 kg   se incorpora en Costura
Ojillos           40 pz  se incorporan en Martillado
Adhesivo           1 kg  se incorpora en Sellado
```

Una orden por 100 piezas escala la receta sin mezclar unidades:

```text
cantidad requerida = cantidad de receta × cantidad autorizada ÷ cantidad base

Tela      = 25 × 100 ÷ 10 = 250 m
Hilo      =  2 × 100 ÷ 10 =  20 kg
Ojillos   = 40 × 100 ÷ 10 = 400 pz
Adhesivo  =  1 × 100 ÷ 10 =  10 kg
```

Los metros, kilos y piezas se mantienen en balances independientes. No se suman entre sí ni se convierten automáticamente.

## 3. Responsabilidad de cada configuración

| Configuración | Responsabilidad |
|---|---|
| Producto terminado | Identidad del producto, ruta activa y receta activa. |
| Ruta | Secuencia de procesos por los que avanza el producto. |
| Receta | Cantidad base, materiales, cantidades y proceso de incorporación. |
| Material | Ubicación principal de almacén y destino WIP predeterminado por proceso. |
| Proceso | Conjunto de áreas, filas o racks WIP permitidos. |
| Orden | Copia histórica de la ruta, receta y destinos resueltos para una fabricación. |
| Operación | Ubicaciones, lotes, cantidades, turno y responsable realmente utilizados. |

### 3.1 Las dos ubicaciones predeterminadas de un material

Un material puede tener dos referencias de ubicación con funciones distintas:

1. **Ubicación principal de almacén:** rack donde normalmente se recibe o guarda el material.
2. **WIP predeterminado de producción:** ubicación a la que normalmente se traslada para utilizarlo en un proceso.

Ejemplo:

```text
Material: Tela negra
Ubicación principal: A-03-02
Proceso: Costura
WIP predeterminado: WIP-COSTURA
```

Estas ubicaciones son preferencias operativas; no significan que el inventario exista simultáneamente en ambas. Las existencias reales siempre proceden de los balances por producto, ubicación y lote.

Un material puede tener destinos diferentes según el proceso:

```text
Tela negra + Costura → WIP-COSTURA
Tela negra + Acabado → WIP-ACABADO
```

El destino no se repetirá en cada receta. La receta seleccionará material y proceso; el sistema resolverá el WIP desde la configuración central del material.

### 3.2 Resolución del destino WIP

El sistema resolverá el destino en este orden:

1. WIP predeterminado configurado para el material y el proceso.
2. WIP predeterminado general del proceso, si se incorpora esa opción.
3. Único WIP permitido del proceso, cuando solamente exista uno.
4. Selección manual antes de liberar la orden, cuando existan varias opciones y ninguna predeterminada.

El destino debe pertenecer a las áreas, filas o racks WIP permitidos para el proceso. Un destino eliminado, bloqueado, inactivo o reclasificado no podrá utilizarse en una orden nueva.

## 4. Ciclo completo de la orden

### 4.1 Creación

El ADMIN crea una orden seleccionando producto terminado, cantidad objetivo, fecha requerida y referencia opcional. El producto debe estar activo; la orden puede conservarse en borrador aunque la ruta o la receta todavía estén incompletas.

Al crear la orden, el sistema copia únicamente la configuración completa disponible:

- producto y unidad del terminado;
- etapas y secuencia de la ruta;
- versión activa de la receta;
- cantidades previstas escaladas;
- proceso de incorporación de cada material;
- configuración WIP resuelta para cada material.

Si la ruta aún no existe, la orden queda sin etapas fotografiadas. Si la receta tiene materiales sin etapa o no es compatible con la ruta, queda sin versión fotografiada ni plan de materiales. Una revisión ADMIN posterior puede incorporar por primera vez esos datos cuando estén completos; nunca sustituye una fotografía existente.

Cambiar posteriormente el catálogo no debe alterar una orden existente. Una receta modificada crea una versión y solamente afecta órdenes nuevas.

### 4.2 Borrador y revisión

En borrador se revisan la meta, los materiales previstos, los destinos WIP y las alertas. La orden no reserva inventario ni genera movimientos mientras permanezca en borrador.

Bloqueos para liberar:

- producto o receta no disponible;
- material inactivo;
- proceso fuera de la ruta;
- destino WIP sin resolver o incompatible;
- cantidades inválidas;
- ruta sin etapas.

Un faltante de existencia genera una alerta, pero su tratamiento debe ser una decisión operativa explícita. El sistema no debe inventar saldo ni sustituir materiales automáticamente.

### 4.3 Liberación y solicitudes de surtimiento

Liberar la orden crea pendientes identificables de surtimiento para bodega, agrupados por proceso y destino WIP, y reserva el material para la orden antes de su traslado. Crear la solicitud y la reserva no mueve inventario físico. La reserva debe indicar folio de orden y una descripción breve del motivo; se distinguirán los estados **Reservado en almacén** y **Reservado en WIP**. El traslado conserva la asociación con la misma orden.

La cantidad solicitada y la cantidad efectivamente reservada se mostrarán por separado. La liberación puede continuar con faltantes. El sistema reserva únicamente la existencia libre real disponible y mantiene separado el faltante para que bodega lo reserve después mediante una acción explícita. No presenta el faltante como reserva respaldada ni sustituye materiales automáticamente.

Cada solicitud contiene:

- orden y lote de producción, cuando ya esté asignado;
- producto terminado y cantidad autorizada;
- material, unidad y cantidad requerida;
- cantidad surtida y pendiente;
- proceso que utilizará el material;
- WIP de destino;
- prioridad y fecha requerida;
- ubicaciones y lotes de origen sugeridos.

Estados de una solicitud:

| Estado | Significado |
|---|---|
| Pendiente | Todavía no se inicia el surtimiento. |
| En surtimiento | Un operador está preparando material. |
| Parcial | Se confirmó una parte y todavía queda saldo pendiente. |
| Sin existencia | El inventario libre no cubre lo requerido. |
| Bloqueada | Falta configuración o existe una restricción operativa. |
| Lista en WIP | La cantidad requerida fue trasladada al destino. |
| Cancelada | La solicitud fue cancelada mediante una acción auditada. |

### 4.4 Alerta y cola de trabajo para bodega

Operaciones tendrá una entrada **Surtimientos a producción** hasta arriba, con una alerta numérica de **órdenes con surtimiento pendiente**, incluyendo las parcialmente surtidas. Una orden con varios materiales o solicitudes pendientes cuenta una sola vez. El contador se actualizará periódicamente y disminuirá al completar o cancelar los pendientes de una orden. Sin pendientes, el acceso permanece visible sin alerta. La carga de trabajo mostrará primero las órdenes prioritarias o vencidas, con prioridad, orden, proceso, material, cantidad y destino.

Ejemplo:

```text
ORD-2026-0045 · Bolsa industrial · requerida hoy

Costura
Tela negra       Requerido 250 m   Surtido 200 m   Pendiente 50 m
Destino          WIP-COSTURA
Estado           Parcial
```

La primera versión utilizará alertas dentro de la aplicación y actualización periódica de la cola. No depende de correo, mensajería ni servicios externos.

Cuando bodega completa un surtimiento, Producción verá el proceso como **Material listo en WIP**. Si el surtimiento es parcial o existe faltante, ambas áreas verán la misma condición y el saldo pendiente.

### 4.5 Sugerencia de ubicaciones de origen

El sistema buscará el material en los balances reales y propondrá ubicaciones en este orden:

1. ubicación principal de almacén con saldo libre;
2. lotes que deban salir primero según la política vigente;
3. otras ubicaciones activas, no bloqueadas y con saldo libre;
4. nunca material reservado para otra orden.

La pantalla mostrará código de ubicación, lote y cantidad disponible. Si una ubicación no alcanza, se podrá completar desde varias ubicaciones o lotes.

Ejemplo:

```text
Se necesitan 50 m de Tela negra

A-03-02  Lote MP-1045  Disponible 25 m
A-04-01  Lote MP-1081  Disponible 15 m
B-01-04  Lote MP-1090  Disponible 60 m
```

### 4.6 Captura guiada del traslado a WIP

Antes de preparar el traslado, bodega podrá elegir material libre ya existente en un WIP permitido, surtir completo desde almacén o combinar ambas fuentes. No se obligará a utilizar el saldo WIP existente: puede ser necesario conservarlo para una orden extraordinaria. Asignar material ya presente en WIP crea su reserva para la orden sin generar una transferencia ficticia. El material reservado para otra orden no se considera libre; cualquier reasignación requerirá un ajuste auditado y conciliado.

El operador:

1. abre o escanea la solicitud;
2. escanea la ubicación de origen;
3. escanea el material;
4. indica la cantidad; el sistema resuelve los lotes internos según la política aplicable, sin exigir su selección al operador;
5. confirma o escanea el WIP propuesto;
6. confirma el surtimiento con NIP.

La confirmación crea un movimiento real de inventario:

```text
A-03-02 → WIP-COSTURA
25 m de Tela negra, lote MP-1045
Reserva: ORD-2026-0045
```

El flujo conservará búsqueda con sugerencias, Enter/HID, cámara y selección manual. Un escaneo identifica; nunca confirma por sí solo. La operación debe ser idempotente y transaccional.

### 4.7 Producción por lotes y procesos

Cada orden tendrá **un solo lote de producción**, seguido como un mismo grupo. No se dividirá en varios lotes ni se ofrecerá división opcional. El avance puede registrarse parcialmente durante varios días, conservando fecha, proceso, cantidad, turno y responsable de cada operación.

Los consumos pueden proceder de distintos lotes de materia prima, todos vinculados al único lote de producción de la orden. La trazabilidad identifica los materiales usados por la orden; no identifica qué piezas individuales recibieron cada material.

En cada proceso se registra, con un solo NIP:

- cantidad de entrada procesada;
- cantidad buena;
- retrabajo;
- merma;
- turno y operador;
- materiales y lotes realmente consumidos;
- motivo cuando el consumo difiera de la receta.

El consumo toma material reservado en WIP para esa orden. No puede consumir reservas de otra orden ni exceder la cantidad pendiente de una asignación.

El avance entre procesos registra entregas y recepciones identificadas. No crea inventario ficticio de productos intermedios. Una etapa posterior solamente puede procesar la cantidad recibida para ese lote.

### 4.8 Recepción del producto terminado

El único lote de producción de la orden se relaciona con un único lote de inventario del producto terminado. Las recepciones parciales, incluso en días y ubicaciones diferentes, mantienen ese mismo lote terminado. Los consumos y recuperaciones posteriores amplían la cadena auditada de la misma orden.

La recepción en bodega crea el movimiento real de entrada y actualiza el avance principal de la orden:

```text
avance = recibido en bodega ÷ cantidad objetivo
```

Los excedentes se muestran por separado. No se promedian porcentajes de procesos como si fueran cantidades físicas equivalentes.

### 4.9 Cierre y correcciones

Se permitirá cerrar la fabricación con cantidad menor o mayor a la meta, registrando la diferencia, el motivo y la autorización correspondiente. El retrabajo normalmente se atiende días después y debe poder permanecer pendiente, identificado y vinculado a la misma orden y lote, sin obligar a mantener abierta la fabricación principal.

**Decisión confirmada para P5:** OPERATOR confirma el cierre principal con NIP; una diferencia exige autorización ADMIN sobre la versión y cantidades revisadas. ADMIN selecciona reservas concretas para el retrabajo y confirma el cierre definitivo. Si queda cantidad que ya no se fabricará, ADMIN ajusta previamente la meta, sin reducir por debajo de lo procesado o recibido. Como criterio de conciliación definitiva, no deben existir:

- entregas pendientes de recibir o conciliar;
- retrabajo pendiente;
- producto bueno sin destino;
- material reservado sin consumir o devolver;
- solicitudes de surtimiento abiertas;
- diferencias sin motivo y autorización.

Las correcciones se realizan mediante reversos auditados. Si existen operaciones posteriores dependientes, deben resolverse en orden inverso. No se elimina la cadena histórica.

### 4.10 Ajustes de una orden liberada

Se permitirán cambios después de liberar la orden. Cada ajuste conservará valor anterior, valor nuevo, motivo, responsable y fecha. La ruta, receta y destinos copiados no cambiarán por editar el catálogo; un cambio explícito de la orden debe quedar registrado como una revisión histórica.

Los ajustes deberán conciliar cantidades, solicitudes, reservas, destinos y operaciones dependientes. No se borrarán movimientos ejecutados para acomodar una nueva meta. ADMIN ajusta fecha, meta, autorizado y cantidades de los materiales existentes con historial anterior/nuevo. No puede reducir por debajo de lo ejecutado, sustituir materiales ni cambiar producto/ruta. Después del cierre principal sólo se agregan necesidades de retrabajo; otros cambios requieren reapertura ADMIN.

### 4.11 Retrabajo y merma

Retrabajo y merma tendrán un apartado propio en la operación, los registros y la analítica. El retrabajo diferido conservará orden y lote original, cantidades pendientes, recuperadas y descartadas, fechas, proceso, responsables y materiales adicionales consumidos. No se contará la misma cantidad como producto bueno nuevamente en cada intento.

Se distinguirá merma de producto de desperdicio de materia prima, conservando cada unidad por separado. Los registros permitirán analizar recuperación, pendientes, tiempo hasta atender el retrabajo, merma y desviación del consumo contra receta.

El retrabajo se atiende en el mismo proceso de origen; lo recuperado continúa mediante entregas y recepciones por la ruta existente. OPERATOR registra intentos con NIP y ADMIN autoriza el descarte. La fecha original del pendiente no se reinicia con cada intento. La captura con fecha anterior queda expresamente pospuesta.

## 5. Movimientos de inventario

| Acción | Movimiento real |
|---|---|
| Surtir material | Transferencia de rack o área de almacén hacia WIP. |
| Consumir material | Salida desde el WIP reservado para la orden. |
| Regresar material | Transferencia desde WIP hacia una ubicación de bodega. |
| Devolver a proveedor | Salida desde WIP con referencia documental. |
| Recibir terminado | Entrada del producto terminado en su lote de producción. |
| Corregir | Movimiento compensatorio y relación explícita con la operación original. |

El motor de inventario sigue siendo la única fuente de movimientos y saldos. Producción conserva referencias a esos movimientos; no mantiene un saldo paralelo.

## 6. Experiencia visual de la orden

La orden mostrará el recorrido:

```text
Materiales → Procesos → Producto terminado → Bodega
```

El encabezado incluirá folio, producto, meta, recibido en bodega, estado, prioridad y alertas accionables.

Los materiales se agruparán por proceso de incorporación y mostrarán:

- previsto;
- solicitado;
- surtido;
- disponible en WIP;
- consumido;
- devuelto;
- pendiente;
- lotes y ubicaciones al expandir.

Cada proceso mostrará recibido, pendiente de procesar, bueno disponible, retrabajo, merma y entregas pendientes. En laptop se usará recorrido horizontal; en tablet, tarjetas apiladas con controles de al menos 44 px.

## 7. Analítica de producción

La analítica se construirá desde eventos, movimientos, resultados y reservas vigentes. No se guardarán totales duplicados cuando puedan derivarse de la cadena auditada.

### 7.1 Materiales

- previsto, surtido, consumido, devuelto y pendiente;
- diferencia y porcentaje contra receta;
- inventario libre y reservado;
- existencia por WIP;
- faltantes por material, orden y proceso;
- materiales con mayor merma o desviación.

### 7.2 Procesos

- cola y carga actual por proceso;
- cantidad recibida, procesada y disponible;
- tiempo de espera antes de comenzar;
- tiempo de procesamiento;
- rendimiento bueno;
- retrabajo y merma;
- cuellos de botella y órdenes detenidas.

### 7.3 Órdenes y producto terminado

- avance recibido en bodega;
- cumplimiento de fecha;
- tiempo total de fabricación;
- rendimiento real frente a receta;
- producción por producto, turno y operador;
- órdenes abiertas, pausadas, vencidas y bloqueadas;
- recepciones parciales y excedentes.

### 7.4 Trazabilidad

- desde lote terminado hacia lotes de materiales consumidos;
- desde lote de material hacia lotes terminados relacionados;
- movimientos, ubicaciones, responsables y fechas de cada vínculo;
- reversos y correcciones visibles dentro de la misma cadena.

Los reportes tendrán filtros GET, paginación, zona horaria del almacén y exportación consistente con los filtros visibles.

## 8. Estado actual

### Implementado en código

- rutas secuenciales por producto;
- procesos asociados a áreas, filas y racks WIP;
- recetas versionadas integradas en la ficha del producto, incluida una lista de materiales incompleta antes de definir la ruta;
- cantidad base y cálculo proporcional de materiales;
- copia de ruta y receta completas a órdenes nuevas y finalización auditada de fotografías ausentes durante el borrador;
- lotes de producción y lote de inventario terminado;
- resultados parciales, retrabajo y merma;
- consumos reales vinculados a lotes de materiales;
- surtimientos WIP vinculados a orden y proceso;
- reservas por orden, devoluciones y reversos;
- entregas y recepciones entre procesos;
- recepción parcial del terminado en bodega;
- consulta de trazabilidad en ambos sentidos;
- mapa visual básico de la orden y filtros de listado.

Estas funciones tienen migraciones pendientes de aplicación y validación física pendiente. Su presencia en código no equivale a despliegue operativo.

### Pendiente de implementar y validar

- P6: tablero analítico completo, métricas y exportaciones de producción.
- P7: validación visual/física integral, piloto, migraciones operativas y despliegue.
- Fuera de P5: captura con fecha anterior, sustituciones de materiales y seguimiento del material en tránsito.

### 8.1 Contraste con el código — 15 de septiembre de 2026

| Parte | Estado en desarrollo |
|---|---|
| P1–P4 | Configuración WIP, fotografía, solicitudes, reservas y preparación guiada existentes; preservados por P5. |
| Lote único | Las nuevas órdenes no admiten segundo lote; el lote abarca lo autorizado y conserva el mismo lote terminado. Las órdenes históricas mantienen sus lotes sin fusión. |
| Cierre | `PrincipalClosed` permite retrabajo posterior; `Closed` conserva el cierre definitivo. El cierre con diferencia identifica operador y ADMIN; la reapertura es ADMIN. |
| Retrabajo | Pendiente con origen, intentos efectivos, recuperación, descarte y antigüedad; retornos al mismo proceso y continuación por la ruta. |
| Reservas | Selección ADMIN de almacén/WIP para retrabajo; liberación de reservas de almacén no retenidas, devolución o anulación explícita del sobrante WIP y solicitudes adicionales. |
| Ajustes | Revisión ADMIN de meta, autorizado, fecha y cantidades de materiales; invalida preparaciones y concilia pendientes/reservas sin modificar el catálogo. |
| Merma | Operación de material separada del consumo en producción, con salida real, lotes, NIP y reverso auditado. |
| Motivos | Catálogo ADMIN por categoría y opción Otro con comentario; descripción conservada en cada operación. |
| Fecha anterior | Pospuesta; se conserva la fecha real de registro. |
| Despliegue | No acreditado por presencia de código ni pruebas. Consultar el historial de la base objetivo dentro de P7. |

### 8.2 Decisiones y compatibilidad

- Liberar con faltantes y toma informativa siguen siendo decisiones cerradas de P3.
- Cierre principal por OPERATOR, autorización ADMIN de diferencias, selección explícita de reservas, cierre definitivo y reapertura ADMIN.
- Retrabajo en el proceso de origen; descarte ADMIN; merma de materia prima por OPERATOR con NIP.
- Ajustar meta antes de cerrar fabricación no completada; conservar meta original y respetar cantidades ejecutadas.
- Se mantienen abiertas para fases posteriores las sustituciones y la representación de material en tránsito.
- Los retrabajos históricos sólo se vinculan desde resultados efectivos. La migración se detiene ante orígenes ambiguos o cantidades inconciliables; no deduce historia desde saldos actuales.
## 9. Fases de implementación

### Fase P1 — Configuración central de materiales y WIP

**Objetivo:** definir una sola vez hacia dónde se envía normalmente cada material.

**Estado al 11 de septiembre de 2026:** implementada en código; migración y validación visual/física pendientes.

- Crear reglas material + proceso → WIP predeterminado.
- Permitir WIP predeterminado opcional en el proceso.
- Administrar las reglas desde Crear, Editar y Detalles del producto material.
- Limitar destinos a WIP permitidos para el proceso.
- Mostrar alertas por destinos inactivos, bloqueados o incompatibles.
- Agregar migración incremental y SQL revisable.

**Aceptación:** diez recetas que utilicen el mismo material y proceso heredan la misma configuración sin repetirla.

La implementación usa el catálogo existente de procesos y agrega reglas auditadas por material/proceso. El destino puede ser un área WIP, un rack WIP completo o una posición exacta; una fila completa continúa siendo una asociación del proceso, pero no un destino predeterminado. Crear producto guarda producto y reglas en una sola transacción, mientras que Editar usa un formulario independiente. Las referencias que pierden elegibilidad se conservan con su causa visible y pueden mantenerse sin cambios, quitarse o sustituirse con NIP ADMIN, motivo, control de versión e idempotencia.

La migración incremental es `20260911184026_MaterialWipDefaults` y el SQL revisable está en `docs/sql/20260911184026_MaterialWipDefaults.sql`. La prueba PostgreSQL aislada recorrió la cadena completa de migraciones y validó persistencia e historial. P1 no crea reservas, solicitudes, saldos, movimientos ni fotografías de orden; esas responsabilidades comienzan en P2 y P3. No se aplicó esta migración a la base configurada ni se reinició o publicó el servicio.

### Fase P2 — Resolución y fotografía de planificación en la orden

**Objetivo:** convertir una orden en un plan histórico completo y estable.

**Estado al 14 de septiembre de 2026:** núcleo de planificación y consulta desplegable de receta implementados en código; migración y validación visual/física pendientes.

- Resolver destinos WIP al crear o revisar el borrador.
- Copiar receta, ruta, cantidades y destinos a la orden.
- Mostrar faltantes o configuraciones incompletas.
- Impedir la liberación cuando un destino obligatorio no esté resuelto.
- Preservar órdenes existentes cuando cambie el catálogo.
- Agregar en la lista de productos una consulta desplegable de los materiales de la receta activa, sin abandonar el listado.

**Aceptación:** una orden conserva sus destinos aunque después cambie el WIP predeterminado del material. Desde el listado de productos se puede consultar rápidamente la receta activa, sus materiales, cantidades, unidades, procesos y destinos WIP sin confundir esos valores con existencias reales.

La orden fotografía por material el tipo, la referencia y el código visible del destino WIP, junto con el origen de la resolución: regla del material, valor general del proceso, único destino concreto del proceso o excepción manual. La precedencia no sustituye silenciosamente una referencia configurada que esté bloqueada, inactiva o incompatible: la deja sin resolver para que el ADMIN atienda el bloqueo. Una fila completa continúa siendo una asociación permitida del proceso, no un destino final de la orden.

Crear el borrador intenta resolver los destinos dentro de la misma operación. La revisión sólo completa datos ausentes y no recalcula fotografías existentes; un ADMIN puede elegir para el borrador otro destino válido con motivo, NIP, control de versión e idempotencia sin modificar P1. Liberar vuelve a validar receta, ruta, materiales, cantidades, procesos y destinos en el servidor. La existencia actual se presenta por separado como disponibilidad de almacén y libre en WIP; un faltante genera advertencia, no reserva ni bloqueo.

La migración incremental es `20260914122652_Phase132ProductionPlanningSnapshot` y el SQL revisable está en `docs/sql/20260914122652_Phase132ProductionPlanningSnapshot.sql`. Conserva órdenes anteriores sin reconstruir destinos: una orden no borrador sin fotografía se identifica como histórica. P2 no crea solicitudes, reservas ni movimientos. No se aplicó esta migración a la base configurada, no se publicó ni se reinició el servicio.

**Complemento UX implementado en código de P2 — receta desplegable en productos:** cada producto terminado con receta activa tiene un control de expansión en el listado ADMIN. El resumen muestra versión y cantidad base de la receta y, por material, SKU, descripción, cantidad, unidad, proceso de incorporación, destino WIP efectivo, fuente y estado de configuración. Un producto sin receta muestra `Sin receta activa`. En tablet se mantiene una sola fila abierta y el contenido se carga al desplegar para conservar el rendimiento. La consulta y la edición continúan siendo exclusivamente ADMIN; la edición permanece en la ficha completa. Este resumen no muestra stock ni permite editar o confirmar operaciones, porque las cantidades de receta expresan requerimientos y no existencia disponible.

**Configuración integrada en Productos:** Crear producto ofrece `Guardar producto` o `Guardar y configurar receta`; la segunda opción continúa en el módulo `Receta y materiales` del mismo producto. En Editar, un ADMIN puede crear la primera ruta sin abandonar la ficha, indicando nombre, procesos, orden y NIP. Una vez creada, la ruta permanece en lectura y se habilita el editor versionado de receta existente. Los accesos `Configurar receta` y `Editar receta` desde listado y ficha llevan al mismo módulo. La configuración sigue siendo opcional para materias primas, no permite reemplazar una ruta activa y no agrega entidades ni migraciones.

### Fase P3 — Solicitudes y cola de surtimiento para bodega

**Objetivo:** avisar a bodega exactamente qué debe preparar y hacia dónde enviarlo.

- Generar solicitudes al liberar la orden.
- Reservar material para la orden antes del traslado e identificar folio, motivo y ubicación de la reserva.
- Agregar contador y pantalla **Surtimientos a producción**.
- Mostrar prioridad, fecha, proceso, material, pendiente y destino.
- Permitir toma, surtimiento parcial, reanudación y cancelación auditada.
- Mostrar faltantes y bloqueos accionables.

**Aceptación:** bodega puede identificar una orden pendiente sin abrir manualmente cada orden de producción.

**Implementada en código el 14 de septiembre de 2026; despliegue pendiente.** `CONTEXT.md` registra que P3 fue reaplicada el 15 de septiembre después de una reversión durante P4. Es evidencia documental: recrear el esquema no acredita recuperar los datos anteriores ni verifica el estado actual de la base. Las órdenes liberadas a partir de P3 generan solicitudes agrupadas por proceso y destino y reservas por ubicación y lote hasta la existencia libre disponible. Las órdenes que ya estaban liberadas permanecen en el flujo anterior. Requerido, cancelado, entregado, pendiente, reservado y faltante se conservan separados.

La cola pública **Surtimientos a producción** muestra una sola alerta por orden pendiente, prioridad normal o urgente, fecha requerida, proceso, material, cantidades y destino. La toma es informativa y permite continuidad por otro operador. Bodega reserva existencia nueva mediante una acción explícita; ADMIN cambia prioridad o cancela pendiente con NIP y motivo. Las órdenes pausadas conservan solicitudes y reservas, pero no admiten entrega.

P3 conecta cada línea con la salida WIP existente. La confirmación revalida versión, pendiente, proceso, producto y destino; crea el movimiento y su vínculo, libera la reserva de almacén utilizada y actualiza la solicitud en la misma transacción. Se permite el saldo negativo con advertencia cuando no invade reservas ajenas. Una salida general, transferencia, ajuste o corrección no puede utilizar cantidades reservadas para otra orden.

P3 conserva compatibilidad con la salida WIP anterior y comparte con P4 los cálculos y protecciones de reservas.

### Fase P4 — Preparación guiada y movimiento Rack → WIP

**Objetivo:** ejecutar el surtimiento con ubicación y lote reales.

**Implementada en código el 15 de septiembre de 2026; migración y SQL generados, despliegue y validación física pendientes.**

- Recomendar racks y lotes con saldo libre.
- Preservar Enter/HID, cámara y selección manual.
- Confirmar origen, producto, lote, cantidad y destino.
- Trasladar material y actualizar su reserva de almacén a WIP en una sola transacción, conservando la orden.
- Permitir asignar material libre ya existente en WIP, surtir completo desde almacén o combinar fuentes.
- Evitar consumo doble, exceso y uso de reservas ajenas.
- Informar a Producción cuando el material esté listo.

La página `/Operations/ProductionSupply/Prepare` abre cada material pendiente sin recapturar la orden. Distingue saldo físico, reserva propia, reservas ajenas y cantidad utilizable; prioriza la reserva de la solicitud y la ubicación principal. Permite elegir varios orígenes de almacén, material libre en el destino WIP o una combinación. El operador selecciona ubicaciones y cantidades; el servidor resuelve lotes FEFO.

Guardar una preparación exige NIP, conserva responsable, fecha, destino, fuentes y versión y puede continuarse desde otra tablet. El borrador no crea movimientos ni reservas adicionales. Al continuar se vuelve a consultar disponibilidad y una versión distinta impide sobrescribir cambios. Descartar exige NIP y motivo y conserva un evento auditado.

Confirmar exige otro NIP y permite reducir las cantidades realmente entregadas por origen. Toda la operación usa una transacción: crea los traslados necesarios, asigna WIP existente sin movimiento ficticio, vincula lotes, libera reservas de almacén, actualiza pendientes y persiste una confirmación común con sus movimientos y asignaciones. El identificador estable impide duplicados y rechaza un reintento con contenido diferente.

Después de confirmar, la pantalla muestra un comprobante con los identificadores de confirmación y operación, cantidad entregada, pendiente vigente, destino y número de traslados y asignaciones WIP. El resultado también puede consultarse mediante el handler `Result` usando el identificador estable. Los enlaces P3 anteriores que contienen una línea de solicitud redirigen a esta preparación; si un enlace antiguo sólo identifica una etapa con varias líneas, abre la cola para elegir el material.

El cambio de destino se guarda únicamente para la línea pendiente seleccionada, exige NIP ADMIN, motivo, compatibilidad y versión, y obliga a revisar preparaciones abiertas. Una asignación WIP sin movimiento puede anularse parcialmente con NIP ADMIN sólo por su cantidad no consumida ni devuelta; libera la reserva y reabre el pendiente sin modificar el saldo físico.

Las devoluciones a bodega de material ligado a una solicitud distinguen **Reposición**, que incrementa el pendiente sin reservar automáticamente, y **Sobrante**, que mantiene el surtimiento histórico sin abrir otro pendiente. La vista de producción calcula **Material listo en WIP** mientras el surtimiento está completo y no comenzó el consumo; después muestra **Surtimiento completo** junto con consumido y disponible.

La migración incremental `20260915143955_Phase134GuidedProductionSupply` amplía el vínculo de material con producto, WIP, cantidad, procedencia y lotes; agrega preparaciones, fuentes y confirmaciones comunes. El backfill se detiene con diagnóstico si un vínculo histórico no puede reconstruirse desde su movimiento y sus cambios de saldo. No reconstruye historia desde existencias actuales. El SQL revisable está en `docs/sql/20260915143955_Phase134GuidedProductionSupply.sql`.

**Aceptación:** cada cantidad reservada para una orden en WIP está respaldada por un traslado confirmado o por una asignación explícita sobre saldo libre real, con lotes, responsable y operación común trazables.

### Fase P5 — Ejecución integrada de producción

**Implementada y verificada en código el 15 de septiembre de 2026; despliegue y aceptación operativa pendientes.**

- `/Operations/Production/Work` conserva el registro conjunto de resultado y consumo, agrega selección del pendiente y motivos, y mantiene los avances posteriores al cierre principal.
- `/Operations/Production/Execution` muestra meta original/vigente, recibido, diferencias, pendientes de retrabajo y antigüedad; concentra cierre principal/definitivo, reapertura, ajustes, reservas y solicitudes adicionales.
- `/Admin/Production/Reasons` administra motivos de merma, retrabajo, diferencia y ajuste con NIP ADMIN, versión, idempotencia e historial. Desactivar un motivo no cambia los registros previos.
- La creación de lote usa la cantidad autorizada completa e incrementa la versión de la orden para impedir dos creaciones simultáneas. Una orden histórica con varios lotes no admite otro.
- El cierre principal exige conciliar cantidades por procesar, entregas y producto bueno; permite retrabajo pendiente y reservas retenidas explícitamente. Los intentos, surtimientos y recepciones posteriores conservan ese cierre.
- El cierre definitivo exige resolver retrabajo, solicitudes y reservas. Corregir resultados de una orden cerrada exige reapertura ADMIN y resolver dependencias posteriores.
- Las revisiones conservan cantidades anteriores/nuevas y no alteran receta ni ruta. Aumentar crea pendiente; reducir cancela lo pendiente y libera sólo reservas propias. El sobrante físico se devuelve por el flujo existente.
- Merma de material crea salida real desde WIP y conserva lotes, responsable y motivo; sus cantidades se distinguen del consumo y pueden revertirse con historial.
- La migración `20260915161501_Phase135ProductionExecution` y su SQL revisable amplían el esquema sobre P4. Conservan estados anteriores y meta original; reconstruyen retrabajos abiertos sólo desde resultados efectivos inequívocos. El descenso se bloquea si existe historia P5.

**Aceptación:** una orden puede cerrar fabricación principal, atender retrabajo posteriormente, surtir/consumir material autorizado, recibir el recuperado en el mismo lote y cerrar definitivamente sin reservas ni cantidades pendientes.

**Evidencia automatizada de P5:** compilación Release con cero advertencias/errores; modelo EF sin diferencias respecto de la migración; sintaxis de `production-execution.js` y `git diff --check` correctos. Pasaron 88 pruebas de producción/UI, una corrida focal de 10 pruebas P5, 9 pruebas PostgreSQL conjuntas de planificación/material/P5 y dos comprobaciones finales de cierre/permisos e intentos sucesivos. Las corridas se solapan; no son un total acumulable. SQL revisable: `docs/sql/20260915161501_Phase135ProductionExecution.sql`.

La integración PostgreSQL verifica creación concurrente de un único lote, cierre principal, recuperación y recepción posterior, cierre definitivo, reconstrucción de historia inequívoca y rechazo de orígenes ambiguos. Las pruebas de P4 conservan surtimiento mixto; la trazabilidad usa ahora dos lotes de materia prima en el mismo lote de producción y conserva el bloqueo de reversos con dependencias posteriores.
Las pruebas automatizadas y las migraciones sobre `warehouse_epi_test` no acreditan aplicación a la base operativa, despliegue ni aceptación física. No se inicia, detiene ni publica el servicio como parte de P5.
### Fase P6 — Analítica y alertas operativas

**Objetivo:** medir rendimiento, faltantes, tiempos y cuellos de botella.

**Implementada y verificada en código el 16 de septiembre de 2026; migración, despliegue, navegador y aceptación operativa pendientes.**

- Construir proyección común para tablero, listado y exportación.
- Agregar métricas de materiales, procesos, órdenes y lotes.
- Incorporar filtros por fecha, producto, proceso, turno, estado y alerta.
- Crear vistas de desviación contra receta, merma, retrabajo y cumplimiento.
- Integrar las alertas con la carga de trabajo existente.

**Aceptación:** los totales del tablero pueden explicarse mediante órdenes, eventos y movimientos concretos.

`/Reports/Production` es una consulta exclusivamente ADMIN con vistas de órdenes, materiales, retrabajo/merma y registros. Conserva filtros GET por periodo, búsqueda, producto, proceso, turno, estado y alertas; pagina en servidor y permite Excel, CSV e impresión. El diseño adopta la densidad operativa de HermeX POS dentro de Razor Pages, Bootstrap, temas y restricciones de tablet de Warehouse EPI.

El cumplimiento usa la recepción efectiva en bodega contra la meta vigente y muestra la meta original cuando fue ajustada. Materiales separa plan original, plan autorizado, surtido, consumo efectivo, devoluciones, desperdicio, pendiente, desviación contra plan y desviación técnica contra la receta histórica proporcional a la entrada procesada de primera pasada. No suma unidades distintas. Registros muestra la cronología y señala originales revertidos sin incluirlos como efectivos.

Cada proceso admite umbrales opcionales de inactividad y antigüedad de retrabajo. Vacíos muestran antigüedad sin declarar atraso; una orden pausada no genera alerta de inactividad. La cola pública incluye órdenes con cierre principal y pendientes posteriores sin exponer la analítica ADMIN.

La migración incremental es `20260916142819_Phase136ProductionAnalytics`; agrega únicamente ambos umbrales y su restricción positiva. El SQL revisable está en `docs/sql/20260916142819_Phase136ProductionAnalytics.sql`. La verificación focal incluye las cuatro consultas sobre PostgreSQL, filtros y cumplimiento, tipos/sanitización de Excel, límite de 10,000 filas, autorización/contrato Razor, configuración de procesos, cola pública y navegación. Además, la corrida amplia de producción aprobó 106 pruebas.

### Fase P7 — Validación, migración y despliegue

**Objetivo:** habilitar el flujo en la operación real sin perder historial.

- Probar migraciones incrementales en PostgreSQL aislado.
- Revisar SQL y procedimiento de respaldo.
- Ejecutar compilación y pruebas focales e integrales.
- Validar laptop y tablet en temas claro y oscuro.
- Validar lector HID, cámara e impresión física.
- Realizar piloto con una ruta y materiales representativos.
- Aplicar migraciones y publicar en una ventana de despliegue separada.

**Aceptación:** el piloto completa una orden desde surtimiento hasta recepción y su trazabilidad coincide con los movimientos físicos.

## 10. Criterios de aceptación integral

- Una orden escala correctamente una receta con materiales de unidades distintas.
- El destino WIP se configura una vez por material y proceso.
- Bodega recibe una solicitud clara al liberar la orden.
- La solicitud indica dónde buscar el material y a qué WIP llevarlo.
- Los surtimientos parciales mantienen un pendiente exacto.
- Ningún movimiento se confirma por el simple hecho de escanear.
- Una operación fallida no deja movimiento, reserva o vínculo parcial.
- Dos operadores no pueden surtir o consumir dos veces el mismo pendiente.
- El material reservado para una orden no puede utilizarse en otra.
- El consumo real conserva producto, lote, ubicación, proceso y responsable.
- El lote terminado conserva la procedencia de todos sus lotes de material.
- Las recepciones parciales mantienen el mismo lote terminado.
- Una orden conserva un solo lote aunque avance durante varios días.
- La reserva identifica orden y motivo desde almacén y conserva su vínculo al pasar a WIP.
- El operador puede elegir WIP libre existente, surtimiento completo desde almacén o combinación de ambos.
- Los ajustes conservan valores anteriores y concilian solicitudes y reservas.
- El cierre con diferencias conserva el retrabajo diferido y su trazabilidad para atención posterior.
- Las órdenes existentes no cambian cuando se edita una receta o destino.
- Las correcciones conservan la cadena original y el inventario conciliado.
- Los reportes reproducen los mismos filtros en pantalla y exportación.
- Los indicadores analíticos se pueden rastrear hasta movimientos y eventos.

## 11. Alcance excluido inicialmente

- coproductos;
- ensambles con ramas paralelas;
- seguimiento individual por pieza;
- división de una orden en varios lotes de producción;
- conversiones automáticas entre unidades;
- costos contables de producción;
- integración con máquinas;
- notificaciones por correo o servicios externos;
- sustitución automática de materiales.

Estos puntos requieren decisiones y modelos adicionales y no deben bloquear el primer circuito operativo de orden, surtimiento, proceso y recepción.

## 12. Estimación de ejecución

Partiendo del código existente, las fases P1 a P6 requieren aproximadamente entre ocho y doce días laborables de desarrollo concentrado. P7 requiere uno o dos días adicionales con acceso a la base de prueba y dispositivos físicos. La estimación puede cambiar después del piloto si aparecen reglas operativas nuevas.

Esta estimación es preliminar y debe revisarse al dimensionar la matriz de excepciones siguiente; no constituye un plazo validado para el alcance actualizado.

## 13. Facilidad de uso y flexibilidad operativa

**Acuerdo de diseño:** la operación diaria debe ser lo más fácil posible, con pocos pasos y solamente los datos necesarios para la acción actual. La configuración de productos queda fuera de este criterio de simplificación del flujo diario. La flexibilidad debe cubrir casos extraordinarios sin llenar el recorrido habitual de controles.

El flujo principal de almacén será **abrir pendiente → recoger material → llevar a WIP → confirmar con NIP al entregar**. Orden, proceso y destino se heredan de la solicitud; se propone el origen y no se exige seleccionar lotes internos. Producción seguirá **abrir orden → registrar avance → confirmar**, mostrando retrabajo, merma y ajustes cuando correspondan.

Las alternativas serán contextuales: **No pude surtir / Reportar problema**, **Cambiar origen**, **Surtir parcialmente**, **Usar material en WIP**, **Continuar donde me quedé** y **Registrar ajuste**. Reportar un problema conserva el pendiente y lo hace visible al responsable dentro de la aplicación. La confirmación muestra cantidad entregada, destino, orden y saldo pendiente, con una acción para continuar.

Se contemplarán prioridad con motivo, aviso de preparación por otro operador, devolución o reasignación auditada de sobrantes, lista de retrabajos pendientes con antigüedad y motivos rápidos de merma o retrabajo. Las notas adicionales se pedirán cuando sean necesarias para explicar la excepción.

## 14. Matriz de excepciones operativas

Esta matriz es un requisito transversal de diseño para P2–P7. Debe revisarse antes de implementar cada flujo afectado y ampliarse con los casos del piloto. No pretende enumerar todas las situaciones posibles: los casos no previstos deben poder reportarse, conservar su estado y resolverse mediante acciones auditadas, sin forzar movimientos ficticios ni borrar historia.

Las respuestas siguientes describen el comportamiento requerido o a diseñar; no acreditan implementación. Los permisos específicos y reglas todavía abiertas deben resolverse antes de programar la acción correspondiente.

| Parte | Excepciones a contemplar | Respuesta que debe diseñarse |
|---|---|---|
| Orden | Urgencia, orden extraordinaria, pausa, reanudación, cancelación parcial, cambio de cantidad o fecha. | Prioridad y motivo visibles; revisiones auditadas; conciliación de solicitudes, reservas y operaciones ya ejecutadas. |
| Plan y configuración | Cambio de material, receta, ruta o destino después de liberar; WIP bloqueado, inactivo o incompatible. | Conservar plan original y revisión; validar dependencias y destinos alternativos. La sustitución manual de material requiere definir autorización y trazabilidad; no habilita sustitución automática. |
| Reserva | Existencia insuficiente, diferencia física, material bloqueado, dos órdenes compitiendo por saldo o reasignación urgente. | Separar solicitado, reservado y faltante; proteger reservas ajenas y registrar toda reasignación. La liberación puede continuar con faltantes: el sistema reserva únicamente la existencia libre real disponible y mantiene separado el faltante para que bodega lo reserve después mediante una acción explícita. No presenta el faltante como reserva respaldada ni sustituye materiales automáticamente. |
| Preparación | Material no encontrado, cantidad insuficiente, ubicación inaccesible, varios orígenes, WIP libre disponible o surtimiento completo desde almacén. | Reportar problema sin perder el pendiente; permitir alternativas válidas y cantidades parciales, conciliando reservas sin duplicarlas. |
| Entrega | Material u origen equivocado, WIP incorrecto o lleno, diferencia entre cantidad preparada y entregada, entrega parcial. | Verificar antes de confirmar; ofrecer corrección o destino alternativo permitido con motivo. Tras confirmar, usar correcciones auditadas. |
| Viaje y relevo | Viaje interrumpido, material físicamente recogido pero aún no entregado, cambio de turno, solicitud abandonada o varios pedidos en un viaje. | Conservar progreso y responsable; definir toma, liberación y relevo. Queda pendiente definir representación del material en tránsito y conveniencia del viaje agrupado, sin perder la cantidad de cada orden. |
| Producción | Falta de material, consumo adicional, proceso detenido, captura equivocada o registro tardío. | Registrar motivo, responsable y fechas de operación y registro cuando difieran; conciliar consumo y avance. No inventar recepción en una etapa para permitir un avance. |
| Retrabajo | Atención días después, recuperación parcial, varios intentos, retorno a otro proceso, consumo adicional o descarte definitivo. | Mantener orden y lote, pendientes y antigüedad; registrar cada intento y su resultado sin duplicar producto bueno. Definir proceso de retorno y autorización de descarte. |
| Merma | Desperdicio de material, producto dañado, detección tardía o corrección de una cantidad o causa. | Separar producto de materia prima y sus unidades; registrar motivo y efectos reales en inventario, resultados y análisis. Evitar contabilizar dos veces un descarte procedente de retrabajo. |
| Recepción y cierre | Recepciones parciales, cantidad menor o mayor a la meta, sobrantes, reservas sin resolver, retrabajo pendiente o corrección posterior al cierre. | Conservar un lote terminado por orden, mostrar excedentes, conciliar sobrantes y definir cierre principal/definitivo y permisos para correcciones posteriores. |
| Uso del sistema | Doble confirmación, respuesta incierta, pérdida de conexión, dos operadores sobre el mismo pendiente o escáner no disponible. | Idempotencia, control de concurrencia, consulta del resultado antes de repetir y búsqueda/selección manual. No mostrar éxito sin confirmación persistida ni suponer funcionamiento sin conexión. |

### 14.1 Contrato de cada excepción

Antes de implementar una excepción se documentará:

1. Disparador, estado actual y mensaje comprensible para el operador.
2. Acciones disponibles, qué puede hacer OPERATOR y cuándo interviene ADMIN.
3. Motivo, responsable, fechas y valores anteriores/nuevos que deben conservarse.
4. Efectos en movimientos, reservas, solicitudes, avance, lote y reportes.
5. Forma de reanudar, corregir o revertir, incluyendo dependencias posteriores.
6. Comportamiento ante doble envío, concurrencia o interrupción y evidencia de aceptación.

Una excepción sin acción segura disponible debe conservar el pendiente y permitir escalarla al responsable. La flexibilidad no autoriza eliminar historia, consumir reservas ajenas silenciosamente, cambiar unidades ni eludir confirmaciones operativas.

### 14.2 Integración con las fases y aceptación

- **P2:** revisiones de orden y cambios de planificación.
- **P3:** faltantes, reservas, prioridades, contador por órdenes y toma/relevo de solicitudes.
- **P4:** preparación y entregas parciales, alternativas de origen/WIP, interrupciones y confirmaciones seguras.
- **P5:** ajustes de ejecución, sobrantes, retrabajo diferido, merma y cierre con diferencias.
- **P6:** pendientes y causas de excepción, antigüedad, recuperación, desperdicio y desviaciones rastreables hasta sus registros.
- **P7:** validar casos normales y excepcionales en PostgreSQL y dispositivos, incluyendo reintentos y dos operadores. Ajustar la matriz a lo observado en el piloto.

El piloto incluirá una orden de 100 piezas con un único lote, material procedente de dos lotes internos, surtimiento parcial, avances en varios días, merma, retrabajo diferido, devolución de sobrante y recepciones parciales. Debe comprobar cantidades y responsables a lo largo de toda la cadena y que reportar o resolver una excepción no añade pasos innecesarios al recorrido normal.
