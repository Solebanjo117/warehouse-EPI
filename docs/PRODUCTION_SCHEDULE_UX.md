# Configuración del programa semanal

La cabecera compartida `_ScheduleHeader` ordena título y acciones generales, contexto, navegación y contenido. El selector muestra el rango completo de fechas sin repetir el estado. «Nueva semana» permanece visible y «Otras acciones» reúne importación, exportación, áreas y turnos. Copiar productos a una semana abierta conserva su autorización actual; una semana cerrada ofrece consulta y reapertura.

Programa, Productos y Resumen mantienen las rutas `program`, `week` y `summary`. La revisión `review` es un paso de Programa y mantiene ese enlace activo con `aria-current="page"`. Las tres secciones caben a 360 px y sus enlaces tienen una altura mínima de 44 px.

La fila de contexto es el único elemento superior adherido. Su contenedor abarca todo el contenido. Por debajo de 900 px queda debajo de `--app-topbar-height` (64 px si no está definido); desde 900 px utiliza `top: 0`. Hasta 900 px vuelve a ser estática mientras el foco esté en ella o en el espacio de trabajo. La navegación y el título se desplazan normalmente.

## Crear una semana

«Nueva semana» muestra una lista mensual de lunes con su rango completo hasta domingo. La fecha inicial es el primer lunes libre desde la semana actual del almacén; la búsqueda consulta fechas directamente y no depende del listado de 60 semanas. Borradores, semanas abiertas y cerradas aparecen como «Ya creada», con selección deshabilitada y «Ver semana».

Elegir un lunes disponible habilita el editor de la misma pantalla: SKU, cantidades de lunes a domingo, pedidos, notas, copia y arrastre. La preparación todavía no crea una semana en la base de datos. «Revisar y abrir semana» valida y muestra el resumen; «Guardar y abrir semana» pide un solo NIP ADMIN al final. Una revisión vacía indica expresamente que se abrirá sin programación ni arrastre.

Cambiar el lunes traslada productos, cantidades por día, pedidos y notas a la fecha nueva. Limpia revisión y NIP y reconsulta copia y arrastre. Un origen incompatible conserva su cantidad y exige corregirlo o descartarlo; los productos copiados pueden conservarse como preparación manual. Navegar de mes limpia la selección, suspende la edición y conserva los datos hasta elegir otro lunes. La recuperación local se guarda por usuario como preparación nueva e incluye la fecha, sin almacenar el NIP.

Razor renderiza el primer mes; `WeekOptions` consulta otros meses y `NewWeekContext` obtiene fuentes y configuración para el lunes. Copia y arrastre aceptan una fecha de destino sin `WeekId`. Durante las consultas se suspende la edición; se ofrecen reintentos y se ignoran respuestas antiguas. Fechas inválidas recibidas directamente no se convierten a otro lunes.

`NewWeekReview` valida sin escrituras. El POST `CreateWeek` conserva `NewWeek.WeekStart`, `NewWeek.OperationId`, `NewWeek.Pin` y el contexto, y añade el programa y la huella de revisión. Una transacción serializable crea el borrador interno, guarda renglones y arrastres y publica órdenes, lotes y auditoría; cualquier fallo revierte el conjunto. La idempotencia vincula toda la preparación. `NewWeekOperation` comprueba el resultado para el usuario de la sesión cuando se pierde la respuesta. La creación concurrente ofrece «Ver semana» y conserva la preparación, sin agregarla a la semana existente. El éxito abre automáticamente el programa creado.

El Programa muestra siempre las siete columnas. Se retiraron Semana / Por día, el selector de día y el cambio automático por ancho o `SelectedDay`. Los enlaces antiguos siguen entrando al programa semanal. En móvil se conserva la tabla, SKU adherido y desplazamiento horizontal sincronizado.

La verificación de este flujo usa PostgreSQL temporal aislado y fixtures Razor/JSON con Edge headless; no utiliza la base operativa ni reinicia el servicio. Evidencias en `artifacts/ui/week-preparation/`. Tablet Android, lector HID y teclado físico siguen pendientes.

Verificación del flujo unificado (1 de octubre de 2026): compilación sin errores y 62 pruebas .NET focales aprobadas, sin omisiones; cinco usan PostgreSQL aislado para reversión, arrastres y concurrencia, incluido el reintento simultáneo de la misma operación. Pasaron 46 pruebas JavaScript del editor y 16 de arrastres. Edge headless comprobó 126 combinaciones de semana nueva, configuración incompleta y cerrada, ES/EN, Claro/Oscuro/Sistema y anchos de 360 a 1280 px, más 29 comprobaciones de preparación, calendario, recuperación, copia, conflictos, reintentos y doble envío. Incluye controles de 44 px, contraste del mes, radios por teclado y ausencia de desplazamiento horizontal de la página al editar. La regresión de semanas existentes verificó barras nativas sincronizadas, búsqueda y consulta cerrada con 36 combinaciones de programa y 54 de búsqueda. El resultado .NET está en `artifacts/week-preparation/results/week-preparation-final.trx`; el informe, las capturas y los resultados de navegador están en `artifacts/ui/week-preparation/`. No hay despliegue ni reinicio del servicio; esta emulación de navegador no sustituye la prueba física Android.

## Preparar y abrir

«Limpiar todo» reemplaza «Descartar preparación» junto al contador de cambios. Aparece cuando existe preparación o edición pendiente, incluso si una copia sin cantidades muestra cero cambios. Tras confirmar, elimina únicamente la preparación local del usuario y semana actuales y vuelve al programa guardado: descarta copias, arrastres preparados, notas, cantidades, eliminaciones pendientes y totales sin distribuir. No envía un guardado, no repite enlaces de eliminación y conserva el respaldo de texto antiguo y preparaciones de otras semanas. Se deshabilita durante copia, guardado, recuperación sin resolver y guardados inciertos. Cancelar o fallar el almacenamiento conserva la preparación. Evidencia focal en `artifacts/schedule-clear-all/validation.md`.

«Buscar o escanear SKU» permanece visible junto a «Copiar semana». Desde 576 px comparten fila; por debajo se apilan con controles de al menos 44 px. El botón de copia despliega su panel debajo sin ocultar el buscador. Cerrar el panel conserva cantidades, opciones, notas y arrastres; Escape dentro del panel lo cierra y devuelve el foco al botón. Empieza cerrado, salvo enlaces de copia/arrastre o recuperación de una copia. «Preparar copia» agrega a la misma tabla de planeación, con una sola fila por SKU y un apartado de referencias de empaque en la última celda. El contador de cambios y «Limpiar todo» quedan sobre la tabla semanal.

El pegado masivo desde Excel se retiró; «Otras acciones → Importar Excel» continúa disponible. Se recuperan filas y previews antiguos, incluyendo sus cantidades, notas y selección. El texto antiguo sin procesar se respalda en la clave local `warehouse-epi:schedule:{usuario}:{semana}:legacy-paste` y aparece solo para lectura en «Texto pendiente del pegado anterior». No participa en validación, apertura ni POSTs y sobrevive al guardado y al descarte de otras preparaciones. Solo «Descartar texto anterior» elimina ese respaldo. `WorkspaceResolveProducts` conserva su ruta y su uso para escaneo y recuperación; el atributo del cliente ahora se llama `data-resolve-products-url`.

«Cómo interpretar el avance» pliega únicamente la explicación general. Las advertencias, errores y preparaciones pendientes permanecen visibles. Las tarjetas exteriores que solo envolvían otras tarjetas se retiraron; cada vista conserva su superficie de trabajo y los formularios operativos.

En una semana ya creada, «Revisar cambios del programa» permite guardar y seguir trabajando en borrador. «Revisar y abrir semana» lleva a la revisión de apertura si el programa está guardado. Cuando hay cambios, presenta la revisión y «Guardar y continuar a apertura». Solo continúa después de recibir o comprobar la confirmación del servidor. La apertura de ese borrador requiere una acción posterior explícita: NIP ADMIN y «Confirmar apertura». La preparación de una semana nueva usa la confirmación conjunta descrita arriba.

La tabla muestra **SKU · Corte · Costura · Ready to Pack · Lunes…Domingo · Total · Acciones**. Las tres áreas se agrupan bajo «Arrastre inicial», permanecen visibles y tienen fondo amarillo cuando su cantidad es positiva. El total solamente suma programación nueva. «Ver origen» contiene la procedencia sin identificadores técnicos y permanece cerrado inicialmente. La tabla principal conserva siempre lunes a domingo.

Los enlaces antiguos de Agregar, Editar, Eliminar y Ver día acceden al buscador, al renglón o al día de esta misma tabla. Eliminar prepara el cambio y permite deshacerlo antes de guardar. En cerradas, las áreas y «Ver origen» conservan la consulta sin permitir edición.

Las semanas cerradas permiten filtrar los SKU presentes con «Buscar SKU en esta semana». Acepta coincidencias parciales sin distinguir mayúsculas, conserva el filtro y permite Enter del lector sin enviar cambios. Al limpiar la búsqueda reaparecen todos los productos; el resultado se anuncia junto al campo. La tabla continúa en modo consulta.

Esta búsqueda se verificó con 30 pruebas .NET y 36 pruebas JavaScript aprobadas, además de Edge aislado con fixtures Razor: 54 combinaciones de idioma, ancho y tema para búsqueda y 36 capturas de regresión del programa. Incluye coincidencias parciales, mayúsculas, Enter, limpieza, resultados vacíos y cambio de vista; el servidor sigue rechazando guardar una semana cerrada. Evidencia en `artifacts/schedule-closed-search/`; sin despliegue ni reinicio. La prueba física de tablet y lector HID sigue pendiente.

La consulta de semanas cerradas mantiene una celda de tabla por área. El formato numérico flexible se aplica al contenido interior, evitando apilar el arrastre y desplazar los días. `production-schedule-alignment.browser.cjs` compara las posiciones y anchos de las celdas con sus encabezados en semanas históricas, actuales cerradas y abiertas, antes y después del desplazamiento horizontal. La corrección se verificó en 108 combinaciones de vista, ancho y tema; las capturas están en `artifacts/schedule-closed-alignment/`.

Cantidades inválidas, filas nuevas sin cantidad, notas, arrastres, selecciones excluidas, recuperación pendiente y respuestas inciertas participan en la protección contra navegación y apertura. La intención de continuar se vincula a usuario, semana y operación; un reintento conserva la misma operación. El NIP no forma parte de la preparación local. La preparación, revisión y guardado del programa admiten más de 100 cambios sin truncarlos.

La revisión muestra bloqueos antes del NIP, ofrece resolver la configuración incompleta y presenta cantidades por unidad y arrastres por área. «Volver a editar» conserva semana y día.

## Cerrar y reabrir

«Cerrar semana…» muestra fechas y consecuencias, con «Cancelar» y «Confirmar cierre». «Reabrir semana…» explica la habilitación de captura y auditoría, con «Confirmar reapertura». Escape y Cancelar devuelven el foco al disparador y conservan el contexto. Abrir el diálogo no cambia datos; confirmar impide un segundo envío.

Los enlaces anteriores con ActionPanel=close o ActionPanel=reopen abren el mismo diálogo. Al cancelar se retira solamente ese parámetro de la URL. Los POST existentes mantienen autorización ADMIN, antiforgery, versión esperada y auditoría; no se añadió NIP a cierre o reapertura.

## Verificación histórica anterior al flujo unificado

- Compilación y 51 pruebas .NET focales de acciones, permisos, configuración, recuperación del programa, tablas adaptadas, localización, CSP y carga de scripts.
- 63 pruebas JavaScript de preparación, copia, arrastre, límites, escaneo, guardado y reintentos, incluida continuación de una operación recuperada y localización del contador después de editar.
- Playwright con HTML Razor de pruebas HTTP, Edge headless y escrituras interceptadas: 648 casos de tanda 2, 270 de regresión de tanda 1 y zoom 200 % emulado. Nueve anchos (360, 768, 899, 900, 991, 992, 1199, 1200 y 1280 px), ES/EN y Claro/Oscuro/Sistema. Incluye guardar y continuar, preparaciones conservadas al cambiar de método, recuperación del método previo, cierre/reapertura, doble envío, Escape, foco y ausencia de desbordamiento de página. La revisión inicial a 1280×900 y 768×1024 muestra la tabla y el primer SKU; a 360×800 muestra contexto y buscador.
- Se actualizaron dos comprobaciones existentes: el balance ahora carga su tabla mediante un parcial y los constructores DOM text(tag, value) no representan claves de traducción.

Las evidencias se generan en artifacts/schedule-ux. La aplicación operativa y el servicio no se reinician; no hay cambios de esquema, migraciones ni despliegue. La validación física de tablet, lector HID y teclado en pantalla sigue pendiente.

## Convenciones de la tanda 2 para las siguientes tandas

| Estado | Tratamiento |
|---|---|
| Borrador, Cerrada, programa guardado | Neutro |
| Abierta | Éxito |
| Cambios, preparación, recuperación, guardado incierto | Advertencia |
| Guardando | Información |
| Error | Peligro |

Las superficies de herramientas son neutras; el énfasis principal identifica acciones y selección. Los estados conservan texto. Los indicadores mantienen sus niveles, umbrales, cantidades, porcentajes y dimensiones; fondo, borde y texto usan los tokens Bootstrap `danger`, `warning` y `success` (`bg-subtle`, `border-subtle`, `text-emphasis`), compatibles con Claro/Oscuro/Sistema.

El contador utiliza traducciones de singular y plural también después de editar. Cero cambios es neutro; los cambios pendientes utilizan advertencia. Navegación, herramientas, acciones generales y estados de preparación cumplen contraste de texto de al menos 4.5:1 en los tres temas.

Los nombres nuevos tienen traducción ES/EN. La etiqueta Productos utiliza la clave `Sección Productos`: el catálogo ya contiene `productos` en minúscula y MSBuild considera ambas variantes un nombre duplicado. Así se conserva la traducción existente de los consumidores de Balance y Reportes sin modificarlos.

La evidencia de esta tanda se guarda en `artifacts/tablet-tanda2`, con un diff contra la copia del trabajo local anterior, fixtures HTTP renovados, resultados y capturas antes/después a los mismos tamaños. La validación física en Android, especialmente con teclado virtual, permanece pendiente.

## Tanda 3: datos compactos y resumen por unidad

La cantidad programada tiene etiqueta visible y cifras tabulares; los inputs conservan 44 px, vacío, cero y precisión original. El estado del plan aparece una vez por celda. Cada área mantiene su nombre completo, porcentaje y color, seguido de «Hecho ese día» sobre superficie neutra. «Ver desglose» abre Cubierto, Meta y Pendiente sin recalcular cobertura. Editar mantiene el snapshot guardado y su advertencia hasta guardar.

Las celdas diarias se alinean arriba y el valor de consulta reserva los mismos 44 px que el campo editable, sin aplicarlos al arrastre. «Hecho ese día» separa etiqueta y cantidad; el valor se alinea a la derecha con el porcentaje. Cada área reserva una línea secundaria también para «No aplica», sin mostrar producción ficticia. Semana conserva completos nombres, estados y cifras mediante el desplazamiento horizontal; la tabla permite desplazarse hasta cualquier día. Se mantienen tamaños de texto, colores y precisión, y los errores o desgloses abiertos pueden aumentar la altura de una celda.

La vista semanal conserva la tabla, SKU adherido y ambas barras sincronizadas en todos los anchos. El foco ajusta el desplazamiento para evitar que SKU cubra una cantidad. `SelectedDay` ya no altera la presentación.

Cada SKU mantiene una fila con campos diarios editables en borrador y abierta, incluso cuando tiene únicamente arrastre. Esos campos comienzan vacíos y no crean programación ni cambios locales hasta editar una cantidad o los detalles. Una única línea incluida se edita directamente; un día sin líneas crea programación de inventario. Con varias líneas, editar el total establece una distribución pendiente: «Editar pedidos (n)» abre en la última celda los campos de cada renglón, sin filas ni tablas adicionales. El operador ajusta sus cantidades manualmente hasta coincidir con el total solicitado; mientras tanto aparecen el total y lo asignado junto al campo y se bloquea la revisión. Las cantidades excluidas no participan en la suma y la selección de arrastre es independiente.

Las líneas conservan IDs, cantidades, referencias, notas, selección y restricciones de eliminación. «Quitar día» y «Deshacer» están en sus detalles y usan los cambios existentes, con confirmación explícita; no se eliminan cantidades guardadas al escribir cero. El total pendiente se conserva en el campo local opcional `skuDayTargets`, sin enviarlo en POSTs, y se recuperan preparaciones anteriores sin ese campo. Las aperturas de pedidos, notas y desgloses se conservan durante la sesión; Escape cierra los pedidos y vuelve a su resumen. Cerrada conserva «Ver pedidos (n)» y la búsqueda de consulta. Enter, escaneo, revisión y reintentos mantienen sus contratos.

Productos muestra SKU, Día, Cantidad, Destino, Detalles y Acción. Detalles conserva pedidos, tipo original, notas y ambas anotaciones con etiquetas distintas y mensajes para vacíos. Sus tarjetas de dos columnas siguen activas por debajo de 992 px. No fusiona renglones ni modifica orden o paginación. Programa utiliza ahora únicamente la tabla principal para los días y sus acciones.

El parcial `_ScheduleSummary` se comparte entre Resumen y revisión. Cada unidad conserva totales de Programado nuevo, Cliente e Inventario y los siete días, incluidos ceros. «Ver conteos y arrastres» muestra conteos de renglones, apertura importada y arrastre programado por día y por unidad, separados del programado nuevo. Desde 992 px usa tabla; por debajo, tarjetas diarias. Sin cantidades ni renglones, muestra un estado vacío sin inventar una unidad «—». Bloqueos, NIP vacío y confirmación de apertura permanecen en su formulario.

Las traducciones nuevas están en ES/EN. Los estados conservan tokens Bootstrap y texto identificable en Claro/Oscuro/Sistema. No cambian servicios, modelos, cálculos, permisos, contratos POST, idempotencia ni formato de preparación. La evidencia exclusiva se guarda en `artifacts/tablet-tanda3`, comparada contra el trabajo local al comenzar esta tanda. La validación física Android, teclado virtual y lector HID sigue pendiente; no se despliega ni se reinicia el servicio.

Verificación de la tanda 3: compilación aislada sin errores ni advertencias; 69 pruebas .NET (51 de rutas/contratos y 18 de dominio) y 63 JavaScript aprobadas. Edge aislado comprueba 810 casos de la nueva presentación, 648 de regresión de cabecera, 270 de regresión tablet y 64 casos de zoom emulado al 200 %, además de las barras nativas y 36 capturas de avance. La fila representativa de SKU corto y tres áreas aplicables pasa de 390,6 a 250,3 px a 768 px (35,9 %) y de 370,2 a 250,3 px a 1280 px (32,4 %). Conserva porcentajes, producción del día, contraste de 4,5:1 y controles de 44 px. Eliminación/Deshacer se verifica con un fixture HTTP sembrado sin capturas, manteniendo las prohibiciones de las filas que sí tienen producción. El informe y las capturas antes/después están en `artifacts/tablet-tanda3/verification.html`.

## Convenciones compartidas — Tanda 4

Tabla y balance usa superficies neutras para contexto y herramientas, y conserva por solicitud los fondos distintivos de Corte (azul), Costura (ámbar) y Ready to Pack (verde). Mantiene siempre T1/T2, los tres saldos, nombres de área y porcentajes Diario/Acumulado. Estos porcentajes son texto coloreado sin recuadros ni fondo propio. Los indicadores usan tokens Bootstrap de peligro, advertencia y éxito con texto identificable; nunca presentan como completado un porcentaje inferior a 100 % por redondeo. Desglose y programación múltiple usan controles nativos de al menos 44 px y un solo DOM editable.

La fila de contexto abarca todo Balance diario y es el único elemento superior adherido; revisión usa una sola barra inferior dentro del editor. En tablet, el foco y la reducción de ventana por teclado devuelven los elementos adheridos al flujo normal. Los controles ocultos quedan fuera de Tab, y la recuperación abre el desplegable correspondiente antes de devolver el foco. Filtrar productos y buscar/agregar para capturar mantienen propósitos y etiquetas distintos.

La revisión conserva panel de cambios y NIP junto a la confirmación, con separación visible entre datos guardados y vista previa. No modifica las tandas 1–3, cierre semanal, Reportes, Importación o Historial. La documentación específica y la evidencia están en `docs/PRODUCTION_BALANCE_PRIORITY1.md` y `artifacts/tablet-tanda4/verification.html`. Las comprobaciones en Edge y zoom emulado no equivalen a una prueba física con Android/HID.

Después de confirmar en Balance, la recarga devuelve al campo de trabajo o al buscador/SKU disponible, evitando restaurar el desplazamiento de la revisión hacia el pie vacío. El ancla temporal de interfaz pertenece a la entrada de historial y se elimina al usarla; conserva el formato y las claves existentes de recuperación, sin cantidades ni NIP.

## Verificación de la barra compacta (1 de octubre de 2026)

Compilación y 53 pruebas .NET focales aprobadas, incluidas rutas, permisos, apertura/cierre, guardado, localización, CSP y carga de scripts. Las 36 pruebas JavaScript del espacio de trabajo pasaron, incluidas copia, escaneo, reintentos, filas excluidas y respaldo antiguo. Se usó la configuración aislada CaptureUi; los binlogs se generaron automáticamente mediante Directory.Build.rsp. Resultado .NET: `artifacts/schedule-toolbar/results/toolbar-final.trx`.

Edge aislado sobre fixtures Razor actuales: 66 combinaciones de ancho, idioma y tema (incluyendo 575/576, 899/900 y 1199/1200 px), 24 comprobaciones de zoom emulado, 36 capturas de programa abierta/cerrada y 18 combinaciones de copia con arrastre. Pasaron también revisión/guardado/apertura, Escape y retorno del foco, recuperación, colores, scroll y privacidad del NIP. Las escrituras estuvieron interceptadas y no se usó el servicio ni la base operativa. Se inspeccionaron las capturas de escritorio claro y móvil claro.

La comparación con los fixtures y assets previos, después de estabilizar fuentes y estilos, recupera 72 px verticales a 1440 × 1000. Evidencias en `artifacts/schedule-toolbar/`: `geometry.json`, `toolbar-desktop.png`, `header-browser/`, `browser/` y `copy-browser/`. Pasaron sintaxis JavaScript y comprobaciones de whitespace del cambio. Sin migración, despliegue ni reinicio; tablet y lector HID físicos pendientes.

## Verificación de celdas comparables (1 de octubre de 2026)

Pasaron 51 pruebas .NET focales y 36 JavaScript. Edge aislado comprobó 324 combinaciones de borrador/abierta/cerrada, Semana/Por día, ES/EN, Claro/Oscuro/Sistema y nueve anchos de 360 a 1440 px, además de 36 casos de zoom al 200 % emulado. Se validaron alineación superior con SKU y notas largos, estados mezclados, cantidades largas, decimales, errores, Enter/foco, recuperación de desgloses y búsqueda en cerradas. La regresión del programa confirmó guardado, conflicto, NIP, recuperación, colores y scroll sincronizado.

La comparación anterior/posterior comprobó 9072 tamaños de texto sin cambios. Los bloques diarios de consulta pasan a 44 px; la diferencia máxima de altura entre indicadores de la misma área pasa de 16,4 a 0 px con los desgloses cerrados. Evidencias y capturas comparables en `artifacts/schedule-cells/`, incluido `comparison.json` y `results/schedule-cells.trx`. Se usó CaptureUi y el binlog automático del repositorio; no se inició, reinició ni desplegó el servicio. La validación física de tablet y lector HID continúa pendiente.

## Verificación de una fila editable por SKU (1 de octubre de 2026)

Pasaron 52 pruebas .NET focales y 52 JavaScript (38 del espacio de trabajo y 14 de arrastre). Se regeneraron fixtures Razor con salida CaptureUi y binlogs automáticos, incluyendo el idioma inglés para copia. Edge aislado comprobó arrastre guardado sin programación nueva, edición de los siete días, copia con/sin cantidades, distribución manual, recuperación de totales y filas antiguas, repetición de copia, exclusiones, descarte, suma exacta de cuatro decimales, IDs guardados, eliminación por día, Deshacer, restricciones por producción, Enter, Escape y foco. Las solicitudes de escritura estuvieron interceptadas.

La presentación pasó 324 combinaciones de estado, idioma, ancho, vista y tema, además de 54 del nuevo flujo y la regresión de copia, guardado, NIP y búsqueda en cerradas. Las verificaciones de cabecera y pedidos incluyen zoom al 200 % emulado. La fila compacta representativa mantiene exactamente 265,515625 px a 768 y 1280 px respecto de los archivos previos de esta tarea, con desgloses cerrados. Evidencias en `artifacts/schedule-sku-row/`, incluidas capturas, resultados, fuentes anteriores y la comparación de geometría. No se inició, reinició ni desplegó el servicio; tablet y lector HID físicos siguen pendientes.

Al volver a la semana, la preparación local se precarga automáticamente y habilita «Limpiar todo». Los conflictos de versión y guardados inciertos conservan sus protecciones. Las cabeceras permanecen visibles dentro del desplazamiento vertical de la tabla. «Incluir producto» aparece bajo el SKU; el arrastre se modifica directamente, incluido cero, sin selector individual «Incluir arrastre».

La preparación local pendiente y el bloqueo de apertura se evalúan por separado. «Revisar y abrir semana» espera la recuperación y revisa las cantidades capturadas, incluidos los arrastres cero. Los días nuevos vacíos y las selecciones excluidas se conservan sin impedir la apertura. Después de confirmar una semana nueva, las sugerencias restantes se trasladan a la clave local de la semana creada; el NIP nunca se conserva. Los cambios sin confirmar, cantidades inválidas, conflictos y guardados inciertos siguen requiriendo atención antes de abrir.

«Incluir producto» excluye el SKU completo de la preparación: programación y los tres arrastres, incluso valores manuales. La selección se conserva al recuperar o repetir una copia. Al confirmar, los SKU excluidos se retiran de la preparación restante para que no reaparezcan en el plan; las sugerencias vacías de productos incluidos se mantienen. Antes de confirmar se pueden volver a incluir conservando las cantidades.

El programa conserva «Total» como suma de programación nueva y añade «Total con arrastre» antes de Acciones. La nueva celda muestra Corte, Costura y Ready to Pack por separado: programación semanal más arrastre inicial del área, sin restar producción. El arrastre se agrega una sola vez por SKU, aunque tenga varios pedidos. Se actualiza con la preparación y muestra «Por revisar» en totales afectados por cantidades inválidas o pendientes de distribuir. Disponible también en consulta de semanas cerradas.


## Totales por SKU — 2 de octubre de 2026

El editor reúne todas las cantidades de un SKU en una sola fila de lunes a domingo. Pedido 1, 2 y 3 son referencias de empaque, sin cantidades separadas. Se retiran Editar pedidos, los subrenglones de inventario y la creación de otra fila para otro pedido. Esta definición sustituye la distribución manual documentada en las verificaciones históricas anteriores.

Copiar y recuperar suman las cantidades por SKU y día, conservan ceros y ediciones manuales y reconocen los orígenes ya incorporados. Un total antiguo pendiente de distribuir se convierte en el total editable. Las referencias se deduplican; más de tres requieren selección explícita antes de guardar. Las notas distintas se unen sin truncar y el límite de 500 caracteres exige corregir el texto si se supera.

La operación opcional `SkuTotals` de revisión/guardado lleva un total por producto y fecha con sus referencias. No se mezcla con cambios por renglón. Los contratos anteriores siguen admitidos para preparaciones con guardado incierto. El servidor expande los totales dentro de la transacción: aumenta el primer renglón y reduce desde el último en orden por secuencia e ID, respetando producción y vínculos físicos. Los renglones nuevos son únicos por SKU/día; el histórico no se consolida masivamente. Revisión y confirmación comparten la expansión, con versión, huella de revisión, NIP ADMIN en semanas abiertas e idempotencia. No requiere migración.

La pantalla conserva Total y Total con arrastre, etiquetas de áreas, cabeceras adheridas y temas. Las semanas cerradas muestran los totales consolidados y sus referencias en consulta.
