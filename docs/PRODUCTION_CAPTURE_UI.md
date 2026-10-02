# Registro de producción: captura y recuperación temporal

Implementación y verificación: 25 de septiembre de 2026.

La evolución posterior a captura conjunta de tres áreas y navegación automática está documentada en [PRODUCTION_CAPTURE_MATRIX.md](PRODUCTION_CAPTURE_MATRIX.md). Ese documento describe el comportamiento vigente; esta página conserva la evidencia de la primera versión.

## Flujo entregado

- «Registrar producción» abre la lista manual. El contexto se presenta una vez; «Cambiar» despliega fecha, área y turno, y «Aplicar» cambia los tres juntos. Se conservan los valores predeterminados del servidor y la fecha del almacén.
- Estados tipados: abierta, cerrada, borrador, sin semana y configuración incompleta. Los estados bloqueados presentan las acciones correspondientes y ocultan captura y revisión. Los accesos administrativos conservan sus permisos.
- Filas con SKU, cantidad a sumar, unidad, registrado y total previsto. Referencias y notas secundarias; descripción únicamente en búsqueda. Máximo de 100 filas; los filtros y cambios de modo conservan cantidades y notas.
- Búsqueda agrupada y escaneo existentes; seleccionar un SKU repetido enfoca la fila sin duplicarla ni incrementar cantidades. Enter avanza por cantidades en lista y vuelve al buscador en captura rápida.
- Validación local de punto decimal, cuatro decimales, rango, productos inactivos y formatos ambiguos. Vacío/cero no integra la tanda. Los errores aparecen junto al campo y el servidor conserva su validación.
- «Solo con cantidad», «Limpiar fila» y deshacer durante la edición. Totales separados por unidad, calculados con precisión decimal.
- Revisión con advertencias, reparto por órdenes plegable y retorno a edición. El NIP se solicita al confirmar; editar invalida la revisión y limpia el NIP. Se bloquea el doble envío y el comprobante incluye responsable, folio e identificador de operación.

## Borrador e idempotencia

`production-capture-draft.js` utiliza `sessionStorage` con claves independientes por fecha, área y turno. Guarda exclusivamente contexto, modo, filtro, productos, cantidades originales, notas e identificador de operación. No guarda NIP, antiforgery ni huella de revisión. Recuperar es explícito; un borrador corrupto permite descarte y un almacenamiento inaccesible muestra una advertencia sin impedir capturar.

El handler POST `GroupRestore` exige antiforgery, valida contexto, duplicados, máximo de filas y longitudes. Recarga SKU, unidad, disponibilidad y cifras desde servidor. Conserva entradas numéricas inválidas para corregirlas; un producto desactivado puede limpiarse antes de continuar.

Antes de reconstruir, `FindRecordedGroupAsync` consulta la operación existente con la misma huella canónica que usa la confirmación. Si coincide, devuelve el comprobante incluso después del cierre de semana. Un conflicto mantiene el borrador y bloquea el reenvío. La recuperación sin registro previo conserva la operación y exige revisión nueva. Un éxito elimina exclusivamente el borrador de la operación confirmada.

Los servicios de confirmación, permisos y transacciones existentes permanecen como autoridad. Esta mejora no añade migraciones ni registro automático sin conexión.

## Evidencia de verificación

| Comprobación | Resultado |
| --- | --- |
| Compilación .NET con salida aislada `bin/CaptureUi/` | Correcta; evita los binarios bloqueados por la instancia operativa |
| JavaScript: captura, buscador, teclado, borrador y edición | 32/32 aprobadas |
| Regresión .NET ampliada de captura, rutas, configuración, CSP, scripts y localización | 89/90 aprobadas; detalle del fallo abajo |
| Nuevas pruebas HTTP de recuperación | 8/8 aprobadas, incluidas recuperación, validación, antiforgery, productos inactivos y respuesta perdida tras confirmar |
| Formato de los tres archivos C# añadidos/modificados para recuperación | Correcto |
| `git diff --check` | Correcto |

La regresión ampliada se encuentra en `artifacts/capture-ui-tests/capture-ui-final.trx`; los binlogs están en `artifacts/capture-ui-*.binlog`. El registro JavaScript final está en `artifacts/capture-ui-tests/javascript-final.log`. Los artefactos locales no forman parte del código publicado.

### Fallos ajenos al flujo de captura pendientes

1. `ProductionDailyGroupRouteTests.Review_requires_no_pin_errors_preserve_rows_and_confirmation_clears_secrets`: supera el flujo de captura y falla posteriormente en la aserción administrativa de `Copy.Rows[0].Date` (línea 378). La página de semana en borrador utiliza el espacio de trabajo semanal y no presenta ese campo del formulario antiguo. La condición que excluye `_ScheduleWorkflow` para borradores también existe en HEAD; no se cambió ese flujo en esta implementación.
2. `LocalizationTests.Client_script_translation_keys_exist_in_the_shared_dictionary`: detecta llamadas al helper DOM `text('button', ...)` de `production-week-workspace.js` como si fueran claves de traducción. Ese helper ya existe en HEAD. Se verificó este fallo por separado y se excluyó de la ejecución ampliada de 90 pruebas. La suite global no se declara completamente aprobada.

### Revisión visual y de uso

Se revisaron en navegador las vistas Razor renderizadas por las pruebas HTTP, servidas por una previsualización local de solo lectura con los CSS y JavaScript reales. Se comprobaron 360, 768, 900 y 1280 px, temas claro/oscuro, captura, revisión, semana cerrada con borrador pendiente, errores, navegación Enter, limpiar/deshacer y aviso de recuperación tras recarga. Sin desbordamiento horizontal observado; cantidad mínima medida de 48 px en 360 px. Se corrigió el contraste de acciones secundarias en oscuro y la prioridad CSS de la altura táctil.

La confirmación y la recuperación idempotente se verificaron en las pruebas HTTP; la previsualización visual no escribe producción ni equivale a una prueba contra la aplicación desplegada. El caso automatizado captura tres SKU, conserva y corrige una cantidad inválida, revisa y confirma con NIP, y recupera el comprobante sin duplicar movimientos.

Pendiente antes de validar el uso en planta: tablet Android real con teclado virtual y lector HID, SKU especialmente largos en el dispositivo y recorrido completo de tres SKU en el entorno de ensayo. La lógica de adaptación al teclado se implementó; la prueba física no puede sustituirse con el cambio de tamaño del navegador.

## Entrega

Cambios concentrados en `Index.Group.cs`, las parciales de captura, sus CSS/JavaScript, recursos de localización y pruebas; consulta de comprobante añadida al servicio de tandas. Se preservaron los cambios locales previos. No se publicó la aplicación ni se reinició el servicio operativo.
