# Auditoría UI/UX del flujo de órdenes de trabajo

Fecha: 16 de septiembre de 2026. Revisión de la rama `main`, base Git `7df80a8`, incluyendo el trabajo local P4–P6 y los demás cambios presentes en el árbol. El hash identifica la base, no una versión que contenga todos los archivos auditados.

## 1. Dictamen y alcance de la evidencia

El flujo tiene controles valiosos de NIP, versiones, reservas e idempotencia, pero su interfaz todavía presenta desconexiones que afectan la ejecución diaria. La prioridad es reparar retrabajo, sugerencias de consumo, recuperación de errores y recepción; después reorganizar la pantalla alrededor de la tarea del operador.

La auditoría cubre creación, planificación, liberación, búsqueda, surtimiento, preparación, identificación del lote, resultado y consumo, entrega entre procesos, recepción en bodega, devolución/merma, retrabajo, cierre, historial y consulta de reportes. Incluye Razor, PageModels, JavaScript, CSS, navegación, contratos de servicios y pruebas de UI existentes.

Niveles de evidencia:

- **C — Código:** comportamiento demostrado por lectura del recorrido vista → handler → servicio. No equivale a una prueba navegada.
- **J — JavaScript ejecutado:** reproducción aislada del script actual con un DOM mínimo simulado mediante Node. No mide layout ni comportamiento real de navegador.
- **V — Validación pendiente:** contraste, tamaños renderizados, rendimiento, tacto, HID, cámara, impresión y experiencia real con datos representativos.

Se consultó el inventario del navegador disponible: no había pestañas abiertas de Warehouse EPI. No se inició la aplicación ni se ejecutaron operaciones contra la base operativa. No se hicieron capturas de pantallas reales ni pruebas físicas. La cobertura funcional de esta auditoría es amplia, pero el cierre visual y operativo necesita la matriz del apartado 7.

Esta entrega añade únicamente este informe. No aplica las mejoras propuestas, no cambia permisos y no migra ni despliega. Los resultados automatizados de P5/P6 documentados anteriormente no se presentan como pruebas nuevas de esta auditoría.

## 2. Recorrido actual y fricciones por etapa

| Etapa / responsable | Recorrido existente | Fricción principal |
|---|---|---|
| Crear / ADMIN | Producción → Órdenes → formulario de borrador → redirección a Work | Formulario siempre ocupa la cabecera; selector extenso; no anticipa unidad y preparación del producto. |
| Planificar / ADMIN | Work → planificación → revisión de destinos → motivo/NIP → guardar | Terminología técnica, sección extensa y acción de liberar separada del resultado de la revisión. |
| Liberar / ADMIN | Work → Control ADMIN → seleccionar Liberar → NIP | Acciones que no dependen del estado; campo de cantidad adicional sin utilidad en las opciones visibles. |
| Priorizar / bodega y producción | Listado Producción o Surtimientos | Dos criterios de cola diferentes; tarjetas de producción no muestran fecha ni siguiente tarea específica. |
| Surtir / bodega | Cola → Preparar → orígenes → NIP/guardar → NIP/confirmar | Buen contexto por línea, pero falta resumen visible de selección y recuperación explícita de respuesta incierta. |
| Iniciar fabricación / operador | Work → crear lote único si falta → seleccionar lote | Paso manual con cantidad fija; conservar como decisión operativa hasta evaluar su integración con el primer avance. |
| Registrar avance / operador | Work → Resultado y consumo → proceso/turno/materiales/cantidades/NIP | Captura entre información de consulta; sugerencias duplicadas por surtimiento y edición manual sobrescrita. |
| Entregar / recibir entre procesos | Work → Entregar, recibir o conciliar | Elegir acción, origen, destino y entrega por separado; entregas con rótulos insuficientes. |
| Recibir en bodega / operador | Work → cantidad/ubicación/NIP → comprobante genérico | Sin resolución de destino compartido; el comprobante pierde el enlace contextual a la orden. |
| Devolver o registrar merma | Work → etapa → Consumir o devolver material | Alternativa de consumo independiente del resultado; selección total por defecto; confirmación suma unidades distintas. |
| Retrabajo / operador y ADMIN | Execution → Atender retrabajo → Work | El enlace sólo lleva orden/lote y la captura no activa `IsRework`. |
| Cerrar / operador y ADMIN | Execution → acción/motivo/NIP | Lista fija de acciones; retenciones no muestran selección vigente; pendientes sin acción directa por elemento. |
| Consultar / operación y ADMIN | Historial en Work, Trace, Execution y Reportes | Información repartida, topes silenciosos y convenciones de fechas/estados distintas. |

No se inventó un número de clics ni un tiempo promedio: dependen del número de materiales, permisos, incidencias y dispositivo. El piloto debe medirlos.

## 3. Hallazgos prioritarios

Prioridades: **P1** = resolver antes del piloto productivo; **P2** = mejorar claridad, continuidad y eficiencia; **P3** = refinamiento posterior. La prioridad expresa impacto operativo, no una vulnerabilidad de seguridad demostrada.

### OT-01 · P1 · El retrabajo no se activa desde el formulario de lote [C]

**Evidencia:** `src/WarehouseEPI.Web/Pages/Operations/Production/_Traceability.cshtml:26` ofrece `BatchResult.ReworkCaseId`, pero no un campo `BatchResult.IsRework`. `Work.cshtml.cs:23` envía el booleano, cuyo valor inicial es `false`. `ProductionTraceabilityService.cs:211` sólo selecciona y descuenta el caso cuando ese valor es verdadero. El enlace «Atender retrabajo» de `Execution.cshtml` sólo lleva orden y lote.

**Efecto:** elegir un pendiente no convierte la captura en un intento de retrabajo. Si la primera pasada ya terminó, puede ser rechazada por disponibilidad o cierre principal; si aún tiene disponibilidad, se evalúa como producción ordinaria. No se afirma que ya existan registros incorrectos en la base.

**Propuesta:** acción explícita «Registrar avance» / «Atender retrabajo», heredando caso, proceso y lote desde el pendiente. Validar la coherencia del modo y el caso en servidor.

**Aceptación:** un pendiente de 5 permite registrar 3 recuperados, conserva 2 pendientes y crea un intento; funciona después del cierre principal sin aumentar la primera pasada.

### OT-02 · P1 · La receta se propone completa por cada surtimiento del mismo material [C,J]

**Evidencia:** `_Traceability.cshtml:26` repite el cociente `plan.Planned / o.AuthorizedQuantity` en cada `MaterialIssues` del producto/proceso. `wwwroot/js/production-traceability.js:16` multiplica ese cociente por la cantidad procesada para cada línea.

**Reproducción aislada:** mismo material en dos vínculos, cociente 2, disponibilidad 100 por vínculo y 10 procesadas: propone 20 + 20 = 40, cuando el requerimiento total es 20.

**Efecto:** la propuesta induce consumo excesivo o un rechazo por diferencia. El servicio compara el consumo agregado contra plan y pide motivo, pero esa validación no vuelve correcta la sugerencia.

**Propuesta:** calcular una necesidad por material/proceso y distribuirla entre vínculos disponibles, haciendo visible el total y conservando el origen elegido. Explicar cualquier diferencia entre el plan autorizado y la receta histórica.

**Aceptación:** repartir entre dos o más surtimientos nunca multiplica la necesidad; mostrar separado lo requerido, propuesto, disponible y realmente capturado.

### OT-03 · P1 · Las sugerencias sobrescriben cambios manuales [C,J]

**Evidencia:** `production-traceability.js:11` vuelve a seleccionar materiales y escribir cantidades con cada `input` de procesadas y cada cambio de proceso.

**Reproducción aislada:** capturar consumo manual 7, desmarcar esa línea y cambiar procesadas de 10 a 11 transforma el consumo a 22 y vuelve a marcarla.

**Propuesta:** usar «Proponer consumo» explícito o conservar campos editados manualmente; avisar de la diferencia sin reemplazarla. Mostrar antes la cantidad procesada y después su propuesta de materiales.

**Aceptación:** corregir procesadas no pierde consumos ni selecciones manuales; una nueva propuesta requiere una acción consciente.

### OT-04 · P1 · Un fallo de red se comunica como un guardado que no ocurrió [C]

**Evidencia:** `production-supply-preparation.js:53` informa «todavía no fue guardada» ante cualquier excepción de `fetch`. El servidor pudo confirmar el guardado antes de perderse la respuesta. Existe `Prepare.cshtml.cs:63` / `OnGetResultAsync` para consultar una confirmación, pero el script no integra esa recuperación; confirmar entrega usa un POST normal.

**Efecto:** se comunica una certeza que el cliente no tiene. El usuario debe decidir si repite, recarga o abandona sin una guía fiable. No se afirma duplicación automática: existen controles de idempotencia y versión en servicios.

**Propuesta:** estados visibles «Enviando», «Confirmado» y «No pudimos comprobar el resultado». Conservar el identificador y datos no sensibles, limpiar NIP y consultar resultado/preparación antes de repetir. La consulta de guardado debe cubrir su operación; no asumir que el endpoint de confirmación también lo cubre.

**Aceptación:** cortar la respuesta después del commit permite recuperar el registro y continuar sin una segunda operación; si no hubo commit, reintentar de forma segura.

### OT-05 · P1 · La recepción no resuelve una ubicación compartida dentro de la orden [C]

**Evidencia:** `ProductionContracts.cs` admite `ApprovedSharedAssignments`; `ProductionService.cs:162` las pasa al motor. `Work.cshtml.cs:21` no las envía, el formulario de Warehouse no las captura y `Message` indica confirmar desde el flujo de Entrada.

**Efecto:** cuando el destino requiere compartir ubicación, la operación se bloquea sin un camino que conserve explícitamente la recepción de producción, su orden y lote. Una entrada genérica no es una solución equivalente.

**Propuesta:** incorporar la advertencia y autorización de compartir al mismo formulario y comando, con resumen del conflicto y revalidación.

**Aceptación:** recibir una parcialidad en un destino compartido crea una única entrada vinculada al lote y actualiza el avance de la orden; cancelar no modifica inventario.

### OT-06 · P1 · Una validación fallida pierde partes de la captura [C]

**Evidencia:** `Work.cshtml.cs:23` limpia ModelState; los checkboxes y cantidades de `BatchResult.Materials` se generan manualmente sin recuperar la lista enviada en `_Traceability.cshtml:26`. Las retenciones de `Execution.cshtml:73` se construyen con cantidades `0` y casillas sin marcar, sin repoblar `Input.Retentions`. En preparación, `HandleAsync` conserva cantidades, pero no renueva explícitamente las versiones tras conflicto.

**Efecto:** después de NIP inválido o conflicto, el operador puede tener que reconstruir consumos o retenciones. El formulario de preparación puede volver a enviar una versión vencida; actualizarla automáticamente tampoco es adecuado sin revisión del cambio.

**Propuesta:** reconstruir por identificadores estables, conservar cantidad/selección/motivo, limpiar NIP, mostrar diferencias y ofrecer «Revisar datos actualizados». Volver a abrir el panel que falló y enfocar su error.

**Aceptación:** un NIP inválido conserva toda la captura no sensible; un conflicto muestra qué cambió y permite una nueva revisión sin bucle de errores ni sobrescribir datos ajenos.

### OT-07 · P1 · Hay dos caminos de consumo sin una explicación de su vínculo con el lote [C]

**Evidencia:** Work muestra «Consumir o devolver material» por etapa incluso para órdenes por lote. `OnPostMaterialAsync` llama a `ProductionMaterialService.ApplyAsync`; el registro combinado usa `ProductionTraceabilityService.RecordResultAsync`, que además crea los vínculos de consumo del resultado/lote.

**Efecto:** un operador puede consumir primero por la acción independiente y luego intentar registrar el resultado con consumo sugerido. El primer camino no crea por sí mismo la asociación `ProductionBatchMaterialConsumption` del resultado. La reserva protege disponibilidad, pero no explica cuál camino usar.

**Propuesta:** para producción por lote, hacer principal el consumo ligado al avance; conservar devolución, proveedor y merma como acciones diferenciadas. Cualquier consumo independiente permitido debe explicar su propósito y cómo se conciliará con la trazabilidad. Revisar órdenes históricas por separado.

**Aceptación:** el operador puede registrar avance y consumo una sola vez y rastrear cada material al lote; no necesita inventar un motivo de desviación para compensar un consumo previo hecho por otra pantalla.

### OT-08 · P1 · Retener reservas exige reconstruir el estado actual a ciegas [C]

**Evidencia:** Execution ofrece una selección de reservas cuya advertencia indica que lo no seleccionado de almacén se libera. La consulta carga retenciones para advertencias, pero la vista no las usa para marcar cantidades/casos vigentes; cada fila comienza sin selección y con cantidad cero.

**Efecto:** revisar una selección ya hecha se parece a crearla desde cero y facilita omisiones. El impacto no debe tratarse como un simple checkbox visual.

**Propuesta:** mostrar selección vigente, disponible, retenido y propuesta; revisión explícita de lo que se conservará, liberará o requerirá devolución antes de confirmar.

**Aceptación:** abrir una selección existente la representa fielmente; cambiar una línea no libera otras sin una advertencia concreta y revisable.

## 4. Mejoras de eficiencia, claridad y consistencia

### OT-09 · P2 · Work prioriza consulta e historia por encima de la siguiente tarea [C,V]

`Work.cshtml:6` inserta `_Traceability` antes del mensaje de éxito/error; ese parcial incluye flujo completo, procedencia, resultados históricos y captura. Después aparecen planificación, resumen, etapas, materiales e historia adicional. En tablet el flujo se apila y aumenta el recorrido previo al formulario.

**Mejora:** cabecera operativa única con orden/producto/lote/estado; siguiente acción; captura del proceso seleccionado; materiales pertinentes; historia bajo consulta. Mostrar éxito y error junto a la acción. **Aceptación:** desde abrir una orden se reconoce qué está pendiente y cómo registrarlo sin atravesar toda la historia. Medir scroll y tiempo con tres o más procesos.

### OT-10 · P2 · Estados y permisos no gobiernan la oferta de acciones [C]

`Execution.cshtml:38` ofrece las seis acciones siempre y arranca en «Cerrar fabricación principal». Work permite seleccionar liberar/pausar/reanudar desde un menú fijo y muestra una cantidad adicional aunque esas opciones no la usan. El servidor sí valida roles y estados: no se deduce una evasión de permisos.

**Mejora:** acciones pertinentes al estado y pendientes, con «Requiere ADMIN» explícito y mecanismo de autorización por NIP conservado. Evitar exigir sesión ADMIN adicional donde el contrato permite confirmar por NIP. **Aceptación:** una orden cerrada ofrece consulta/reapertura autorizada; una pausada explica cómo reanudar; no se pide completar un formulario destinado a ser rechazado.

### OT-11 · P2 · Entregas y recepciones entre procesos requieren selección redundante [C]

`_Traceability.cshtml:30` reúne entregar, recibir, devolver y perder; origen/destino son selects independientes. Las entregas se identifican como lote + cantidad pendiente, sin proceso emisor/receptor ni fecha en el texto de opción.

**Mejora:** «Entregar» desde el proceso emisor; «Recibir» desde una tarjeta de entrega que herede lote, emisor y receptor. Mostrar fecha, pendiente y responsable. Conciliación de diferencia como acción ADMIN explícita. **Aceptación:** dos entregas del mismo lote y cantidad se distinguen sin ensayo/error y no es necesario recapturar sus procesos.

### OT-12 · P2 · Las colas no muestran una prioridad operativa común [C]

Producción se ordena por creación descendente (`ProductionQueryService.cs:35`) y muestra «Trabajar» para todas las órdenes. La fecha está en el modelo pero no en la tarjeta. Surtimientos sí ordena por urgencia/fecha. El filtro «Proceso activo» incluye recepciones históricas, y «Solo con alertas» no representa la misma lógica de umbrales P6; el filtro y contador tampoco usan idénticas condiciones para órdenes cerradas con diferencia.

**Mejora:** mostrar siguiente tarea y razón de atención: recibir, avanzar, surtir, atender retrabajo o conciliar; separar abiertas/cerradas; precisar el significado de proceso y alertas. **Aceptación:** una orden con trabajo urgente no queda enterrada por altas nuevas; cada alerta y filtro tiene una definición visible y consistente. No unificar consultas sólo por tener nombres parecidos.

### OT-13 · P2 · La cola puede quedar desactualizada aunque el contador no cambie [C]

`production-supply-queue.js:12` sólo recarga si cambia el número de órdenes pendientes; una parcialidad, prioridad o problema puede cambiar manteniendo ese número. Errores de actualización se omiten. La consulta obtiene todas las líneas pendientes sin paginar (`ProductionSupplyService.cs:37`).

**Mejora:** indicador de última consulta, botón Actualizar y versión de contenido; avisar cuando hay novedades sin perder captura. Agrupar por orden/proceso y paginar en servidor. **Aceptación:** una parcialidad de otra tablet se refleja o se anuncia aun cuando siga habiendo el mismo número de órdenes. Medir carga con volumen representativo antes de afirmar lentitud.

### OT-14 · P2 · La preparación tiene pasos nominales, pero poca revisión visible [C]

Prepare ofrece guardar preparación y después confirmar con otro NIP; son acciones diferentes y eso se explica correctamente. Sin embargo, ambos pasos están presentes y sus rótulos son estáticos. El total calculado por `production-supply-preparation.js:23` sólo se escribe en una región `visually-hidden`. «Registrar preparación» en la cola se parece a «Guardar preparación» aunque el primero registra participación informativa.

**Mejora:** resumen visible por origen, total, pendiente y destino; avance real de los pasos; distinguir «Marcar que estoy preparando» de «Guardar selección». **Aceptación:** antes de guardar/entregar se entiende exactamente cuánto y de dónde; no se elimina el NIP de cada acto ni se convierte la toma en un bloqueo exclusivo sin decisión de negocio.

### OT-15 · P2 · La confirmación de materiales suma unidades incompatibles [C]

`production-material-order.js:29` suma cantidades de todas las líneas seleccionadas y presenta «cantidad total». Work selecciona por defecto las líneas y propone todo su pendiente. Un material medido en metros y otro en piezas no admiten ese total físico.

**Mejora:** revisión por material y unidad; selección/alcance explícitos para consumo, devolución y merma; cantidades máximas visibles; verbo concreto al confirmar. **Aceptación:** 5 m y 3 pzas aparecen separados, nunca como «total 8»; una merma no queda preseleccionada como todo el saldo sin revisión clara.

### OT-16 · P2 · Cantidades y validaciones no tienen un contrato visual uniforme [C,J,V]

El script de resultados usa `parseFloat`; el de ajustes reemplaza coma por punto y usa `Number`; el de materiales usa `Number` sin reemplazo. La reproducción de `0,25` en el script de resultados deja vacías ambas propuestas. Varios handlers de Work limpian ModelState antes del servicio; no conservan los errores originales de conversión. No se ha reproducido un valor persistido incorrecto ni establecido aquí una política regional definitiva.

**Mejora:** una política explícita de decimales y unidades, validar antes de calcular, conservar errores de conversión y mostrar la igualdad procesada = buena + retrabajo + merma. **Aceptación:** probar `0.25`, `0,25`, `1,500`, cero, negativos, 4/5 decimales y piezas enteras; nunca reinterpretar un dato ambiguo silenciosamente. Verificar navegador → binding → servicio.

### OT-17 · P2 · Creación y listado ADMIN dificultan la búsqueda a escala [C]

`Orders.cshtml` mantiene Nueva orden expandida sobre el listado. Producto es un select sin búsqueda ni resumen de unidad, ruta y receta. `Orders.cshtml.cs:15` obtiene hasta 100 órdenes, sin total ni paginador; productos con ruta se limitan a 2000 en la consulta.

**Mejora:** listado primero y «Nueva orden» explícita; selector buscable que muestre unidad y configuración requerida; paginación y filtros por estado/fecha. **Aceptación:** recuperar la orden 101 mediante navegación normal y distinguir un producto listo de uno con configuración incompleta antes de crear/liberar.

### OT-18 · P2 · El comprobante final rompe la continuidad de producción [C]

`Work.cshtml.cs:21` redirige al comprobante de inventario; `Operations/Receipt.cshtml:21` no trata `ProductionReceipt` de manera específica, por lo que «Nueva operación» deriva a Entrada genérica. No ofrece un enlace de retorno a la orden/lote.

**Mejora:** comprobante «Recepción de producción» con orden, lote, parcialidad recibida y pendiente; acciones «Volver a la orden», «Recibir otra parcialidad» y etiqueta/pallet cuando corresponda. **Aceptación:** continuar recepción o cierre sin volver a buscar el folio ni crear una entrada fuera del flujo.

### OT-19 · P2 · Idioma, fechas e historia varían entre pantallas [C,V]

Index y Orders imprimen enums de estado; Surtimientos imprime prioridad; Work imprime tipos de evento. La interfaz mezcla etapa/proceso, meta/objetivo y lenguaje interno como «P2 · Fotografía». Work y Trace usan `ToLocalTime()` del host; Execution y reportes usan el reloj del almacén.

**Mejora:** vocabulario compartido en español, fechas del almacén y explicación breve de primera pasada, retrabajo, cierre principal y definitivo. Distinguir evento efectivo/revertido y enlazar su corrección. **Aceptación:** la misma operación tiene fecha y estado coherentes en captura, historia y reporte. Si host y almacén comparten zona puede no verse diferencia hoy; verificar otra configuración.

### OT-20 · P2 · Hay accesibilidad pendiente más allá del contraste [C,V]

Los formularios Material repiten `asp-for` por etapa y generan IDs iguales; hay labels sin asociación y campos manuales sin nombre accesible claro. Execution no usa `production-page`, por lo que no hereda su regla específica de 44 px. Los campos de handoff/recepción usan `col-4/5/3` incluso en pantallas estrechas. En el escaneo de preparación, «no encontrado» sólo se anuncia en una región invisible. No se midieron píxeles ni contraste renderizado.

**Mejora:** IDs por etapa/formulario, etiquetas asociadas, error visible y anunciado, foco al campo que falla, panel de captura abierto tras error y apilado responsive. **Aceptación:** recorrido completo con Tab/Enter/Escape, nombre accesible único, targets de 44 px medidos y operación cómoda a 800×1280 y 1024×768, en ambos temas. Conservar búsqueda manual cuando falle cámara/HID.

### OT-21 · P2 · Consulta histórica y trazabilidad tienen límites poco visibles [C]

`ProductionTraceabilityService.cs:472` limita procedencia a 200 vínculos sin paginador en Trace. `Execution.cshtml.cs:73` obtiene 50 auditorías sin explicar ese corte. Work carga historia/resultados completos y Trace muestra datos como texto sin enlace contextual a la orden.

**Mejora:** historial paginado o progresivo, total y periodo visibles, enlaces de orden/lote/movimiento y filtros; resumir en la operación y llevar el detalle a consulta. **Aceptación:** se puede encontrar un registro anterior al límite sin confundir un conjunto parcial con toda la historia. El impacto de rendimiento debe medirse, no inferirse como un tiempo concreto.

### OT-22 · P2 · Reportes: las fechas editables y la impresión pueden inducir una lectura incompleta [C,V]

`Reports/Production/Index.cshtml.cs:79` sólo usa From/To cuando Period es custom; las fechas permanecen editables en la vista y el JS sólo implementa imprimir. Cambiar fechas dejando «Últimos 30 días» hace que el servidor las sustituya. `window.print()` imprime las filas cargadas; el CSS oculta el paginador mientras Excel/CSV consultan un conjunto mayor.

**Mejora:** al editar fechas seleccionar Personalizado o deshabilitarlas hasta elegirlo; indicar «Imprimir página actual» o proporcionar impresión completa con límite explícito. El error de exportación superior al límite debe volver a filtros con mensaje accionable, en vez de sólo un BadRequest de texto.

**Aceptación:** el intervalo visible coincide con el aplicado y el documento declara cuántas filas incluye de cuántas; probar más de 25 filas y límite de exportación. Validar impresión física después.

## 5. Elementos que conviene conservar

- Surtimiento contextual por línea con origen, destino, reserva propia/ajena, WIP libre y alternativa manual.
- Separación entre preparar y confirmar movimiento, con comprobante posterior.
- Un lote nuevo por orden, recepciones parciales y continuidad del retrabajo después del cierre principal.
- Registro conjunto de resultado y consumo, una vez corregida su captura y sugerencia.
- Revisiones, retenciones, cierres y reversos auditados, con autorización en servidor.
- Filtros GET, paginación pública de Producción, scripts locales y temas mediante tokens.
- Reportes ADMIN con comparación contra plan y receta, y unidades independientes.
- Búsqueda de destinos de planificación con flechas, Enter, Escape y anuncio de resultados.

La revisión encontró estos mecanismos; no certifica toda la integridad transaccional del sistema ni sustituye las pruebas de PostgreSQL.

## 6. Diseño objetivo y secuencia propuesta

El recorrido principal debe poder leerse como **abrir pendiente → revisar contexto → capturar cantidad/resultado → revisar efecto → confirmar con NIP → ver comprobante y continuar**.

Una ficha operativa de orden debería presentar: identificación y estado; tarea recomendada; proceso y lote seleccionados; captura pertinente; pendientes accionables; consulta de materiales e historia bajo demanda. La planificación detallada pertenece al borrador; no debe competir con cada registro diario. Las herramientas ADMIN deben seguir disponibles mediante una entrada explícita y autorizada.

| Bloque propuesto | Hallazgos | Entrega esperada |
|---|---|---|
| A. Corregir conexiones funcionales | OT-01 a OT-08, OT-15/16 | Retrabajo accesible, consumo correcto, captura recuperable, recepción compartida y retenciones revisables. |
| B. Simplificar el trabajo diario | OT-09 a OT-14, OT-18 | Ficha de operación, siguientes acciones, entregas contextuales y colas actualizadas. |
| C. Homogeneizar consulta y accesibilidad | OT-17, OT-19 a OT-22 | Listados escalables, vocabulario, fechas, teclado/tacto, historia e impresión claras. |
| D. Pilotar y ajustar | Todos | Evidencia navegada y física; tiempos/errores observados; cierre de cada criterio. |

A no debe esperar un rediseño grande. Mantener Razor Pages, Bootstrap y el motor existente. Las propuestas que afectan comandos —modo de retrabajo, asociación de consumo, recuperación e integración de aprobación de ubicación— necesitan pruebas funcionales, además de cambios visuales.

No decidir automáticamente crear el lote al liberar, fusionar confirmaciones, eliminar NIP, sustituir materiales, permitir fechas anteriores ni operar offline. Son cambios de reglas o alcance, no consecuencias necesarias de una mejora visual.

## 7. Matriz de aceptación en navegador y piloto

| Escenario | Evidencia que se necesita |
|---|---|
| Crear borrador válido / configuración incompleta | Unidad, receta/ruta, motivo de bloqueo y acción de corrección comprensibles. |
| Liberar con faltante permitido | Advertencia diferenciada de bloqueo; solicitud y reserva explicables. |
| Preparar desde dos ubicaciones y WIP existente | Resumen por origen, persistencia, relevo y confirmación sin duplicados. |
| Dos tablets en la misma solicitud | Conflicto accionable; conserva captura y muestra cambio real. |
| Guardado o confirmación con respuesta perdida | Recuperar resultado o reintentar con el mismo identificador. |
| Producir con el mismo material surtido en varias líneas | Propuesta única distribuida y consumo real preservado. |
| NIP inválido / cantidad inválida | Foco y error junto a la captura; cantidades conservadas; NIP vacío. |
| Primera pasada con bueno, merma y retrabajo | Igualdad de cantidades visible; motivos y permisos pertinentes. |
| Retrabajo parcial y varios intentos | Caso/proceso/lote heredados; no duplica primera pasada; antigüedad conservada. |
| Dos entregas iguales del mismo lote | Identificación inequívoca de emisor, receptor, fecha y saldo. |
| Recepción parcial / destino compartido | Una entrada asociada a producción; aprobación y comprobante contextual. |
| Devolución sobrante / reposición / merma | Efecto sobre surtimiento explícito; unidades independientes. |
| Cierre con pendientes / retención / reapertura | Causa y acción directa; estado previo visible; autorización correcta. |
| Orden histórica sin lote o con varios lotes | Conserva contrato anterior; vista acumulada claramente distinguida. |
| Más de 100 órdenes / 200 vínculos / 50 auditorías | Navegación completa, total y topes visibles; medir rendimiento. |
| Fechas personalizadas / medianoche del almacén | Mismo intervalo y hora en operación, historia y reportes. |
| Imprimir/exportar más de una página | Conteo y alcance explícitos, filtros y unidades conservados. |
| Teclado/HID, cámara, captura manual, sin Internet | Foco progresivo, alternativas y ausencia de dependencia de CDN; no confundir LAN con modo offline. |

Ejecutar en laptop y tablet Android real, con temas claro/oscuro. Usar órdenes de prueba identificadas, incluyendo una ruta representativa con varios procesos, surtimientos parciales y retrabajo diferido. Medir tiempo hasta encontrar el pendiente, tiempo de registro, correcciones, recapturas tras error y desplazamiento necesario; establecer metas después de obtener esa línea base.

## 8. Verificación realizada y cobertura pendiente

Se ejecutó el archivo real `production-traceability.js` mediante Node/VM con controles simulados, sin acceder a base de datos. Resultados:

```text
10 procesadas, ratio 2, dos vínculos del mismo material:
  propuestas [20, 20], total 40, esperado por receta 20.
Cantidad manual 7 y checkbox desmarcado; cambiar procesadas a 11:
  cantidad resultante 22, checkbox true.
Procesadas "0,25":
  propuestas vacías.
Contrato de la vista de lote:
  ReworkCaseId presente; campo IsRework ausente.
```

Se inspeccionaron pruebas de contrato de planificación y trazabilidad/P5. Verifican marcado, atributos y presencia de controles; por sí solas no prueban que el POST del navegador active retrabajo, que varias líneas compartan correctamente una propuesta, ni que un error preserve captura. No se reejecutó .NET porque esta entrega no modifica aplicación y esas pruebas no demostrarían los recorridos de UX pendientes.

Antes del cierre se requieren pruebas funcionales del formulario real para OT-01/02/05/06/07/08; pruebas de red incierta e idempotencia para OT-04; verificaciones de consulta para OT-12/13/17/21/22; y evidencia visual/física para OT-09/14/20 y la matriz anterior.

Observación documental: el apartado «Estado actual → Pendiente» de `PRODUCTION_ORDER_TRACKING.md` todavía enumera P6 como pendiente aunque su sección específica la marca implementada. También CONTEXT conserva una mención histórica a analítica P6 fuera de P5. Conviene armonizar esos resúmenes al ejecutar el siguiente bloque; esta auditoría no los modificó.


## 9. Implementación del bloque A — 16 de septiembre de 2026

Esta sección actualiza el estado de los hallazgos; las secciones anteriores conservan la evidencia original de la auditoría. Se trabajó sobre el árbol local de main, con cambios P4–P6 existentes, sin migraciones nuevas ni despliegue.

Decisiones aplicadas: punto decimal exclusivamente (sin miles, coma ni notación científica; máximo cuatro decimales); consumo independiente rechazado en el POST de órdenes por lote; las órdenes históricas sin lote conservan su consumo anterior.

| Hallazgo | Corregido en código | Verificado automáticamente | Validado en navegador / operador |
|---|---|---|---|
| OT-01 | Modo explícito, caso/lote/proceso contextual y coherencia validada en servidor. | POST real con antiforgery: primera pasada y recuperación de 3; caso incoherente rechazado. Pruebas existentes de cierre principal/retrabajo en PostgreSQL. | Pendiente. |
| OT-02/03 | Propuesta por material/proceso, distribuida por selección/antigüedad/id; edición manual conservada; retrabajo sin propuesta de receta. | JavaScript real en DOM simulado: dos vínculos, prioridad, faltante, cambio manual, proceso incompatible y retrabajo. | Pendiente. |
| OT-04 | Consulta de guardado por evento y línea; distingue modificación posterior; consulta de confirmación; recuperación en sesión sin NIP y reintento con la misma operación. | Servicio: prueba persistida tras edición posterior y consulta aislada por línea. JS: respuesta perdida, recuperación y reintento con nuevo NIP sin cambiar identificador. | Pendiente pérdida de red real después del commit. |
| OT-05 | Aprobaciones de ubicación compartida dentro de recepción, invalidadas al cambiar destino; cancelación sin POST. | POST real: conflicto sin movimiento, aprobación con una entrada vinculada, repetición sin segunda entrada. | Pendiente. |
| OT-06 | Validación por formulario, texto inválido conservado, NIP limpiado, filas por identidad, panel/error enfocado y revisión explícita de versiones actuales. | Binder y POST real con coma; pruebas de concurrencia de preparación y versiones en servicios. | Pendiente foco, conflictos y recaptura en tablet. |
| OT-07 | Consumo independiente oculto y rechazado por handler en órdenes por lote; advertencia de antecedentes sin reasignarlos. | POST directo rechazado; retrabajo sin consumo adicional; regresión del motor de materiales. | Pendiente con órdenes históricas representativas. |
| OT-08 | Retenciones actuales precargadas, captura de error conservada, disponibilidad visible y revisión de cantidades conservadas/liberadas/devueltas. | Regresión de servicios de retención y cierre. La revisión visual del formulario no se considera probada por estas pruebas. | Pendiente. |
| OT-15 | Materiales sin selección inicial ni propuesta de todo el saldo; confirmación separada por origen y unidad. | Contratos de materiales y comprobación de sintaxis. | Pendiente revisión de unidades mixtas. |
| OT-16 | Binder de cantidades limitado a producción y validación cliente; unidades enteras en servicios; igualdad del resultado visible. | Casos de punto/coma/miles/negativos/exponente/precisión y POST con entrada inválida. | Pendiente teclado Android y HID. |

### Evidencia reproducible

- JavaScript: `node --test tests/javascript/production-block-a.test.cjs`: 8 pruebas correctas, incluyendo simulaciones de respuesta perdida. No equivalen a navegador o corte de red físico.
- Pruebas nuevas de cantidades, guardado y POST: 14 correctas. El host HTTP de prueba usa HTTPS y AllowedHosts=localhost; mantiene antiforgery activo.
- Regresión final de producción: 95 pruebas correctas en Release, incluidas las 14 focales anteriores; no deben sumarse dos veces. Se excluyeron PostgreSQL (ejecutado aparte), reportes y la página de configuración de producto.
- PostgreSQL: 9 pruebas correctas de ejecución, planificación y materiales. El fixture reconstruye exclusivamente warehouse_epi_test y aplica allí las migraciones existentes; no usa la base operativa como destino.
- Compilación Release correcta sin advertencias ni errores. Debug encontró binarios bloqueados por la aplicación en ejecución; no se detuvo el proceso.

No se inició ni reinició la aplicación. La instancia existente no demuestra estos cambios porque no se desplegó. No hubo validación visual ni participación de un operador externo. El bloque A queda implementado en código; su aceptación operativa continúa pendiente junto con los bloques B, C y D.


## 10. Implementación del bloque B — 16 de septiembre de 2026

El bloque B reorganiza el trabajo diario conservando Razor Pages, rutas, NIP por operación, antiforgery, versiones, idempotencia y las recuperaciones del bloque A. No añade entidades ni migraciones. Los parámetros GET seleccionan contexto visual; los servicios vuelven a validar los comandos.

| Hallazgo | Corregido en código | Verificado automáticamente | Validado en navegador / operador |
|---|---|---|---|
| OT-09 | Cabecera, tarea y consulta en parciales; una captura visible; borradores separados por tarea dentro de la página; NIP limpiado al cambiar; aviso al salir; éxito con continuación explícita. | JavaScript ejecutable: conservación de cantidades y checkbox, limpieza del NIP y aviso de salida. POST real: error, recuperación y estado pausado. | Pendiente: tres procesos, temas, resoluciones y persona externa. |
| OT-10 | Política compartida de estados entre consultas y servicios; acciones de NIP ADMIN accesibles sin sesión adicional; planificación del borrador; pausa/reanudación y cierre/reapertura; revisión de impedimentos con enlaces. | Estados y rechazo de comandos manipulados, POST pausado con antiforgery, regresión de cierre y planificación. | Pendiente: comprender la recomendación, los bloqueos y los cierres sin asistencia. |
| OT-11 | Entregas identificadas con procesos, lote, hora del almacén, responsable y saldo; recepción contextual; conciliación ADMIN separada; captura fallida reabierta con revisión de versiones. Se alinea la disponibilidad de una entrega con el material bueno devuelto al emisor. | Servicio: dos entregas de igual cantidad diferenciadas por identificador, metadatos de proceso/responsable y nueva entrega después de devolución conciliada. Regresión de trazabilidad. | Pendiente: otra tablet modifica el saldo mientras se captura. |
| OT-12 | Colas por urgencia, fecha requerida, creación e identificador. Producción abre activas; filtros de proceso usan pendientes efectivos. Evaluación compartida de alertas con P6: contador, filtro y detalle; pendientes separados de atraso e inactividad con umbral. Surtimientos pagina 25 órdenes antes de cargar detalles. | PostgreSQL: 27 órdenes en páginas 25/2 sin división ni duplicados; orden coincidente en ambas colas, filtros, página fuera de rango, pausas, ausencia de umbral y exclusión de proceso histórico completado considerando reversos. | Pendiente: comprensión de prioridad y razón de atención en tablet. |
| OT-13 | Snapshot firmado del contenido filtrado para ambas colas; consulta cada 30 segundos con pestaña visible y sin superposición; actualización manual y aviso por captura; error conserva información previa. Filtros y página acompañan preparación y regreso. | PostgreSQL: cambia la firma por cantidad sin cambiar el contador. JavaScript: cambios, pestaña oculta, solicitudes concurrentes, error de red y confirmación antes de descartar captura. | Pendiente: dos dispositivos y corte/restablecimiento de red real. |
| OT-14 | Participación informativa «Marcar que estoy preparando»; tres pasos visibles; revisión por origen/unidad/destino; selección modificada exige guardar; confirmación muestra entrega real y pendiente resultante; recuperación A integrada. | JavaScript: modificar origen bloquea confirmar; parcialidad calcula pendiente; pruebas de respuesta incierta y reintento del bloque A. Servicios PostgreSQL de preparación/concurrencia. | Pendiente: recorrido manual, HID/cámara, guardar y confirmar con un NIP por acto. |
| OT-18 | Comprobante ProductionReceipt enlazado desde el evento persistido, con orden/lote, parcialidad, acumulado y pendiente consultados al abrir. Retorno contextual, otra parcialidad, etiqueta/pallet y advertencia de antecedentes/correcciones. Otros tipos conservan sus acciones. | HTTP real: recepción compartida, comprobante vinculado, enlaces a orden/otra parcialidad y ausencia de Nueva operación genérica. Reintento no duplica movimiento. | Pendiente: abrir comprobantes actuales e históricos en navegador. |

### Evidencia automática del bloque B

- 110 pruebas focales .NET de producción y reportes, incluidas las regresiones del bloque A y POST reales con antiforgery. Resultado: tests/WarehouseEPI.Tests/TestResults/production-block-b.trx.
- 2 pruebas PostgreSQL de consultas B/P6: paginación, prioridad, snapshots, alertas, umbrales, histórico de procesos y reversos. Resultado: tests/WarehouseEPI.Tests/TestResults/production-block-b-postgresql.trx.
- 9 pruebas PostgreSQL de regresión de ejecución, planificación y materiales del bloque A. Resultado: tests/WarehouseEPI.Tests/TestResults/production-block-a-regression-postgresql.trx.
- 15 pruebas de JavaScript ejecutable con DOM simulado en tests/javascript/production-block-a.test.cjs y production-block-b.test.cjs. Incluyen propuestas, conservación de captura, recuperación incierta, colas y preparación.
- Comprobación de sintaxis de scripts de producción y compilación Release con UseAppHost=false. Los fixtures PostgreSQL reconstruyen exclusivamente warehouse_epi_test; no se aplican migraciones a la base operativa.

### Aceptación pendiente

No se inició, detuvo, reinició ni desplegó la aplicación. La instancia existente no constituye evidencia de la versión modificada. Las pruebas HTTP y DOM simulado no equivalen a validación visual o física.

En una instancia autorizada con esta versión, completar 800×1280 y 1024×768, ambos temas, Tab/Enter/Escape, foco de errores y controles táctiles de al menos 44 px. Una persona ajena al desarrollo debe encontrar un pendiente, completar su tarea, corregir un NIP/cantidad errónea y continuar sin buscar nuevamente el folio. Incluir concurrencia entre tablets, respuesta incierta, órdenes históricas y recepción parcial. Registrar identidad del escenario, resultado y evidencia antes de cambiar «Pendiente» a «Validado en navegador».

Estado: corregido en código y verificado automáticamente en los escenarios indicados; aceptación visual y operativa pendiente. El módulo completo no se declara listo: continúan separados los bloques C y D.
