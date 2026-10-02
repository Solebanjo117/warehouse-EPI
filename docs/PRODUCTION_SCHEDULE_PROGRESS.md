# Planeación semanal y avance por área

La tabla del programa está disponible en borrador y abierta para edición ADMIN, y en cerrada para consulta. Semana y Por día comparten el día seleccionado; pantallas menores de 900 px empiezan en Por día. El selector «Día» solo aparece en Por día y filtra las columnas visibles, sin cambiar el cálculo del estado. Los pedidos del mismo SKU conservan sus cantidades y notas en el detalle, con un único estado por SKU y área.

## Metas

`GetScheduleProgressAsync` consulta una sola vez el balance semanal de reportes. La producción del día se obtiene de la diferencia de producción efectiva acumulada; la meta firmada es el saldo final firmado más esa producción diaria. Por lo tanto incluye programación aplicable, aperturas en su fecha, faltantes y adelantos anteriores, sin modificar el plan base.

La meta visible es la parte positiva de la meta firmada. El porcentaje **diario** es (adelanto anterior aplicado + realizado del día) / (faltante anterior positivo + apertura del día + programa aplicable del día) × 100. Muestra por separado el objetivo antes del adelanto, la meta restante, la producción de hoy y el saldo firmado. Una meta cero muestra «Cubierto por adelanto» o «Sin meta»; una etapa omitida muestra «No aplica». Los valores negativos conservan el adelanto durante días sin programa. Cada área se calcula independientemente. Ready to Pack es el tercer proceso existente, no una captura nueva de packing.

Ejemplo: 200 el lunes y 400 el martes. Con corte de 250 el lunes, la meta del martes es 350; hacer 350 equivale a 100%. Con costura de 150 el lunes, la meta del martes es 450.

La tarjeta principal de la tabla muestra **Estado del plan**: la cobertura de la meta exigible de ese día con toda la producción efectiva guardada de la semana. Asigna primero a la meta más antigua y limita cada estado al 100%. Incluye el crédito inicial y las aperturas fechadas, pero no mueve capturas de su fecha real. La línea «Hecho ese día» muestra esa producción histórica por separado. Un día sin meta nueva queda neutro y conserva «Hecho ese día»; un proceso omitido indica «No aplica». Por ejemplo, si lunes y martes ya están cubiertos, 55 cortes de jueves y 40 de viernes cubren 95 de 400 pendientes de miércoles: 23,8%. Al guardar otros 305, miércoles pasa a 100% aunque el trabajo siga fechado después. El estado usa todos los días de la semana en ambas vistas; el selector «Día» solo filtra columnas.

El avance diario histórico y el acumulado permanecen en «Tabla y balance» y en la exportación «Balance diario», con sus significados anteriores. No se presentan como tarjetas adicionales en el programa semanal.

Los indicadores permanecen ligados al programa guardado mientras se edita, con aviso de cambios pendientes. Guardar recarga el programa y los indicadores. En «Tabla y balance» se muestran ambos por área; «Status %» usa el avance diario de Ready to Pack. La hoja «Balance diario» mantiene sus columnas y agrega los tres porcentajes acumulados. Las aperturas entre semanas y la disponibilidad física conservan sus contratos.

Desde la tanda 3, «Estado del plan» se presenta una vez por celda y cada área conserva nombre completo y porcentaje coloreado. «Hecho ese día» queda siempre visible en una línea neutra independiente; las áreas no aplicables muestran «No aplica» sin cantidades ficticias. Un único «Ver desglose» por día/SKU expone Cubierto, Meta y Pendiente con los valores existentes. No cambia su cálculo, asignación temporal ni umbrales: menos de 75 % peligro, desde 75 % hasta menos de 100 % advertencia, meta completa éxito, sin meta neutro. Un valor inferior a 100 % se muestra como máximo 99,9 % para evitar que el redondeo sugiera una meta completa.

La presentación reduce altura mediante espaciado y detalle plegable, conservando controles de 44 px. Los pedidos independientes comparten un único conjunto de indicadores por SKU/día. Por día usa tarjetas en anchos menores de 576 px y tabla desde ese ancho; Semana mantiene su tabla con SKU adherido solo horizontalmente. Abrir un desglose, editar o cambiar de vista no modifica la cobertura guardada.

## Guardado

`WorkspaceSave` acepta el payload existente y un campo de formulario `adminPin` independiente. `SaveWorkspaceChangesAsync` permite borrador y abierta; `SaveDraftChangesAsync` conserva su restricción a borrador. El NIP se pide en abierta para altas o cancelaciones que requieran revertir material, conforme a los servicios existentes. No se incluye en huellas, auditoría ni preparación local y se limpia al enviar o volver a editar.

El grupo mantiene el límite de 100 cambios, antiforgery, autorización ADMIN, versiones por semana/renglón, idempotencia y transacción serializable. Las altas crean orden/lote; las ediciones y cancelaciones reutilizan sus restricciones de producción y compromisos de arrastre. Una falla revierte todo el grupo. La auditoría utiliza `open-batch-changed` en abierta y conserva `draft-batch-changed` en borrador; el endpoint de comprobación reconoce ambos. Las correcciones de apertura en abierta continúan en su flujo separado.

La tabla cerrada no genera formularios de edición, y un nuevo POST de modificación se rechaza en el servidor. Un reintento de una operación ya confirmada puede reconocer el éxito anterior sin escribir otra vez.

## Verificación del 29 de septiembre de 2026

Estado recuperado: 20 pruebas .NET focales de proyección y rutas pasaron, además de 2 pruebas en PostgreSQL 18 temporal y 36 contratos de UI/localización. El caso real del miércoles pasa de 95/400 (23,8%) a 400/400 (100%) tras una captura posterior de 305, manteniendo «Hecho ese día: 0» y sin cambiar el balance histórico. La suite de navegador con fixtures Razor antes/después comprobó ambas vistas, semana cerrada, 36 combinaciones de ancho/tema, colores y estado estable ante edición pendiente. Pasaron también 44 pruebas JavaScript del workspace y la regresión de apertura/cierre en Edge. Evidencia: `artifacts/schedule-progress/schedule-recovered*.trx` y `artifacts/schedule-progress/browser/recovery-*.png`. La instancia PostgreSQL temporal se detuvo.

- Ampliación diario + acumulado: 33 pruebas .NET focales pasaron, incluidas las consultas y concurrencia en PostgreSQL 18 aislado (`artifacts/schedule-progress/schedule-cumulative-final.trx`); 11 pruebas de proyección pasaron tras las últimas aserciones (`schedule-cumulative-projection.trx`). El ejemplo 200% → 300% / 100% se comprueba con plan 100/200/300, ambos turnos, reverso excluido y exportación XLSX. La ruta HTTP verifica los dos indicadores en el balance y su respuesta de previsualización.
- JavaScript: 42 pruebas del programa y 20 del editor de balance, incluida actualización y restauración de los dos indicadores. Las suites de Edge headless usan fixtures HTTP, revisan borrador/abierta/cerrada, corte, barras nativas, temas y 36 capturas del programa más 8 combinaciones de balance. No equivalen a una prueba física de tablet.

La tabla principal incluye una barra horizontal superior en borrador, abierta y cerrada, sincronizada con la inferior. Ambas comparten el mismo ancho útil y recorrido; se ignoran los eventos generados por la propia sincronización para evitar rebotes. La barra superior se oculta cuando la tabla cabe y actualiza su recorrido al cambiar de vista, contenido o tamaño. Conserva el extremo derecho al redimensionar y permite navegación por teclado con foco visible.

Cada indicador de área con porcentaje usa fondo rojo claro por debajo de 75%, amarillo claro desde 75% hasta menos de 100%, y verde claro al completar 100%. Se utiliza el porcentaje sin redondear y los colores corresponden al programa guardado. Los estados sin meta y no aplicables conservan fondo neutro. El texto principal y secundario conserva colores oscuros legibles sobre los fondos claros en todos los temas; la suite de navegador comprueba los límites 74.99/75/99.99/100. La cobertura real nunca supera 100%.

La comprobación adicional de desplazamiento usa Edge headless con barras nativas visibles sobre fixtures Razor: arrastre de ambas barras con cambios de dirección, eventos alternados, teclado, redimensionado y vista por día. Resultados HTTP y localización: `artifacts/schedule-progress/schedule-scroll.trx` (19 pruebas) y `schedule-scroll-localization.trx` (6 pruebas, parcialmente compartidas). Capturas de las barras: `artifacts/schedule-progress/browser/scroll-*.png`.

- .NET: **Passed: 47, Failed: 0, Skipped: 0**, incluyendo metas, turnos, reversos, consolidación de pedidos, aperturas históricas y explícitas, procesos omitidos, estados, rutas HTTP, permisos, NIP, idempotencia, CSP, scripts y localización. Resultado: `artifacts/schedule-progress/schedule-progress-final.trx`.
- PostgreSQL 18 temporal: pruebas de consulta, rollback tras una alta seguida de una edición inválida y dos guardados simultáneos con la misma versión. La conexión configurada inicialmente rechazó CREATE DATABASE; la ejecución final usó un cluster independiente en `artifacts/schedule-progress/postgres`, puerto 55439, sin consultar ni modificar datos operativos. La instancia temporal se detuvo al terminar.
- JavaScript: **Passed: 42, Failed: 0**, ejecutadas con `node tests/javascript/production-week-workspace.test.cjs`.
- Edge headless sobre HTML Razor de pruebas HTTP, con escrituras interceptadas: agrupación, indicadores del programa guardado, conflicto y conservación de cambios, privacidad del NIP, semana cerrada, teclado y día seleccionado. 36 capturas cubren abierta/cerrada, anchos 768/899/900/1199/1200/1440 y temas Claro/Oscuro/Sistema, sin errores JavaScript. Se inspeccionaron visualmente escritorio claro y tablet emulada oscura. Evidencia: `artifacts/schedule-progress/browser/`.
- Pasó también la suite anterior de preparación, apertura, cierre/reapertura, teclado y 18 combinaciones de ancho/tema (`production-schedule.browser.cjs`).
- `git diff --check` y comprobación de sintaxis de los scripts propios pasaron.

No requiere migración ni reimportación. No se desplegó ni se reinició la aplicación o su servicio. La validación física de tablet, HID y teclado en pantalla queda pendiente.

Para repetir la suite .NET, configurar `WAREHOUSE_EPI_TEST_CONNECTION` con una conexión exclusivamente de pruebas con permiso para crear bases temporales. La configuración `CaptureUi` y `UseAppHost=false` evitan reemplazar binarios operativos. `Directory.Build.rsp` genera los binlogs automáticamente.

```powershell
$env:WAREHOUSE_SCHEDULE_PROGRESS_FIXTURES = Join-Path (Get-Location) 'artifacts/schedule-progress/fixtures'
$env:WAREHOUSE_SCHEDULE_FIXTURES = Join-Path (Get-Location) 'artifacts/schedule-progress/actions'
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj -c CaptureUi --no-restore -m:1 -nr:false -p:UseAppHost=false -p:BuildInParallel=false --filter '(FullyQualifiedName~ProductionScheduleProgress|FullyQualifiedName~ProductionScheduleWorkspaceRouteTests|FullyQualifiedName~ProductionScheduleActionsTests|FullyQualifiedName~ProductionProgrammingPendingTests|FullyQualifiedName~ProductionScheduleDraftBatchTests|FullyQualifiedName~ProductionExplicitCarryoverTests|FullyQualifiedName~ProductionDailyUxContractTests|FullyQualifiedName~Resource_source_keys_do_not_collide|FullyQualifiedName~English_catalogs_have_all_neutral_keys|FullyQualifiedName~Client_script_translation_keys|FullyQualifiedName~ContentSecurityPolicyContractTests|FullyQualifiedName~ScriptLoadingContractTests)' --logger 'trx;LogFileName=schedule-progress-final.trx' --results-directory artifacts/schedule-progress --verbosity minimal
node tests/javascript/production-week-workspace.test.cjs
node tests/javascript/production-schedule-progress.browser.cjs
node tests/javascript/production-schedule.browser.cjs
```
