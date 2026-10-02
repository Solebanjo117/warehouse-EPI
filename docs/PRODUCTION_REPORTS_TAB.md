# Reportes de producción

Implementado el 28 de septiembre de 2026 en `/Operations/Production?Tab=reports`.
La navegación conserva `Tabla y balance`, `Historial` y añade `Reportes`.

## Consulta

- `ReportKind=daily`: siete días, una fila por día sin columna de unidad, con T1/T2/total dentro
  de cada proceso y totales semanales. Cada día enlaza al detalle por producto.
- `ReportKind=products`, `ReportPeriod=day|week`: detalle diario o semanal por
  producto; ambos turnos juntos, arrastre programado, apertura, producción,
  pendiente final. Los excedentes aparecen como pendiente negativo, sin columna
  Por conciliar. El intermedio después de T1 solo es diario.
- `WeekId`, `Through`, `Sku`, `Reference`, `Area` son filtros GET reproducibles.
  SKU busca también descripción. `ReportPage` pagina productos de 25 en 25;
  los totales siempre incluyen todos los resultados filtrados.
- Los datos proceden de los servicios de balance, no del historial limitado a
  500 capturas. Las capturas revertidas se excluyen; un turno histórico fuera
  de los dos configurados se presenta como `Otros turnos` en el mismo grupo.
- Solo lectura y cantidades guardadas. Cero no acredita revisión de captura.
  No se suman procesos entre sí. El resumen diario acumula todos los productos
  por día; el detalle por producto conserva sus unidades. El arrastre programado es
  una referencia separada, no una segunda suma a la apertura.

## Salidas y compatibilidad

`handler=ReportExport` descarga el reporte filtrado con ClosedXML.
`handler=ReportPrint` abre HTML independiente con todos los resultados, sin
editor ni navegación. Ambas rutas usan el mismo contrato de tabla que pantalla
y rechazan más de 10.000 filas sin truncar. El exportador histórico permanece
intacto. Fechas y cantidades se exportan tipadas, con hasta cuatro decimales;
los textos se protegen contra fórmulas.

Excel distingue Corte en azul, Costura en naranja y Ready to Pack en verde,
con encabezados oscuros, fondos suaves por proceso y totales resaltados.
Los colores dependen del proceso, incluso cuando se filtra uno solo; el SKU
permanece inmovilizado junto con los encabezados.

Impresión A4 horizontal para resumen o un proceso; A3 horizontal para detalle
de varios procesos. T1/T2 nunca se dividen en tablas independientes. Los filtros
de detalle solo se muestran al elegir el reporte por producto.

Se conservan los permisos existentes de consulta de Producción y la recuperación
de borradores al navegar desde Balance. Reportes no carga editor ni historial.
No hay migraciones, paquetes nuevos ni cambios de inventario. No se desplegó ni
se reinició la aplicación operativa.

## Validación

Corrección de desplazamiento horizontal: los eventos generados al sincronizar
una barra ya no devuelven posiciones atrasadas a la barra que se está moviendo.
La regresión de navegador reprodujo el retroceso antes del cambio y pasó después
en ambos sentidos, ambos reportes y seis anchos; también pasó la revisión de
temas, teclado e impresión. Validación con fixtures, sin reiniciar la aplicación.

Comandos ejecutados desde la raíz, con salida aislada porque la aplicación
mantiene bloqueadas DLL en `bin/Debug`. Se usó `DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`.
Los binlogs se generan automáticamente en `.binlogs/`.

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-restore --filter 'FullyQualifiedName~ProductionDailyReport&FullyQualifiedName!~isolated_postgresql' -m:1 -nr:false -p:UseSharedCompilation=false -p:UseAppHost=false -p:OutputPath=bin/Reports/net10.0/ --verbosity minimal
```

Resultado: **Passed: 4, Failed: 0, Skipped: 0**. Incluye paridad con Balance,
más de 500 capturas, reversos, turnos históricos, unidades distintas, rutas que
no aplican, arrastre, pendientes negativos, conciliación, filtros, paginación,
exportación completa, impresión y límites de 10.000/10.001 filas.

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-build --no-restore --filter '(FullyQualifiedName~ProductionBalanceEntryTests|FullyQualifiedName~ProductionBalanceWorkspaceTests|FullyQualifiedName~ProductionCaptureRecoveryTests|FullyQualifiedName~ProductionDailyBalanceTests|FullyQualifiedName~ProductionWeeklySummaryTests|FullyQualifiedName~ContentSecurityPolicyContractTests|FullyQualifiedName~ScriptLoadingContractTests)&FullyQualifiedName!~postgresql' -m:1 -nr:false -p:UseAppHost=false -p:OutputPath=bin/Reports/net10.0/ --verbosity minimal
```

Resultado: **Passed: 37, Failed: 0, Skipped: 0**.

```powershell
dotnet test tests/WarehouseEPI.Tests/WarehouseEPI.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ProductionDailyReportTests.Reports_translate_on_isolated_postgresql' -m:1 -nr:false -p:UseAppHost=false -p:OutputPath=bin/Reports/net10.0/ --verbosity minimal
```

Resultado: **Passed: 1, Failed: 0, Skipped: 0**. La cuenta habitual no permitía
crear bases. Se validó con PostgreSQL 18 temporal en `127.0.0.1:55437`, dentro de
`artifacts/production-reports/pgdata`; la prueba creó y eliminó su base única.
La instancia temporal quedó detenida. No se usó la base operativa.

```powershell
node tests/javascript/production-balance-editor.test.cjs
node tests/javascript/production-capture-draft.test.cjs
node tests/javascript/production-reports.browser.cjs
```

Las dos suites JavaScript existentes: **21 aprobadas**. La suite opcional de
navegador necesita Playwright en `NODE_PATH` y fixtures HTML generados con
`WAREHOUSE_REPORT_FIXTURES=artifacts/production-reports/fixtures` al ejecutar la
prueba HTTP. Pasaron ambos reportes en seis anchos (768, 899, 900, 1199, 1200,
1440), estilos claros/oscuros, foco, selector día/semana, impresión de todas
las filas y ausencia de errores JavaScript. Las capturas usan viewport fijo:
la captura de página completa alteraba la posición aparente del menú fijo.

Evidencias locales en `artifacts/production-reports/`. Es validación de HTML
real generado por el servidor de pruebas con fixtures, no del servicio LAN.
Quedan pendientes tablet e impresora físicas y aceptación con datos operativos.

## Ajustes de captura y lectura

- Cierre semanal elimina la unidad en Comparación de turnos, Pendiente para
  próxima semana, Cumplimiento y Resumen por SKU, también en sus cuatro hojas
  Excel. Los totales incluyen toda la población filtrada en una sola fila;
  la comparación presenta una fila por proceso y conserva T1/T2 juntos.
  Se retiró la consulta de unidades del servicio de cierre. Validación: 32
  pruebas aprobadas (incluyen productos con unidades distintas), revisión
  en navegador y paridad de columnas y totales con Excel.

- Balance diario incorpora una barra horizontal superior accesible por teclado,
  sincronizada con la inferior. Ignora los eventos reflejados atrasados, iguala
  el ancho útil descontando la barra vertical y conserva el extremo derecho al
  cambiar las dimensiones. ResizeObserver actualiza el recorrido al agregar
  productos. Se oculta sin desbordamiento y al imprimir. Validado con arrastre
  nativo de ambas barras, eventos atrasados, extremos y ocho combinaciones de
  tamaño/tema en navegador; 20 pruebas web/CSP/carga de scripts aprobadas.

- Auditoría de doble descuento: no hay resta de ToReconcile al pendiente firmado.
  En el cálculo físico, la parte asignada y el residual se cuentan una vez cada
  una; el balance de programa usa la cantidad efectiva completa. Se agregaron
  dos regresiones (modalidad histórica y arrastre explícito): 100 programadas y
  130 producidas mantienen −30 al cambiar la conciliación de 130 a 30, al día
  siguiente, al cierre y en ambos reportes. Reversar restaura 100; reemplazar por
  110 deja −10. Pasaron 32 pruebas focales, incluidos arrastres entre semanas.

- Tabla y balance, cierre semanal, captura individual/matriz y todas las hojas
  de exportación de producción usan el pendiente con signo, sin columnas ni
  indicadores separados de Por conciliar. El saldo es apertura + programa
  aplicable − producción efectiva; el excedente queda negativo. La producción
  sin asignar no se descuenta una segunda vez. La asignación interna a órdenes
  conserva sus controles y trazabilidad.
- La captura muestra el pendiente del programa, incluso cuando la disponibilidad
  física es diferente. Su vista previa resta la cantidad solicitada al pendiente
  registrado. Se conserva la población operativa de los selectores de captura.
- Validación de este ajuste: 106 de 109 pruebas .NET y 21 JavaScript aprobadas;
  navegador de reportes y balance aprobado. Tres pruebas de ProductionDailyGroupRouteTests
  esperan la antigua lista por área: posición del segundo SKU y estado vacío
  de Ready to Pack, mientras la ruta GET actual abre la matriz de tres áreas.
  No se modificó ese contrato de navegación en este ajuste. Tablet física pendiente.

- `ReportKind` y `ReportPeriod` son opcionales al enlazar la petición: las
  pestañas ajenas a Reportes no tienen que enviarlos. En Reportes se normalizan
  a `daily` y `day`. La regresión HTTP cubre filtros ausentes y vacíos en las
  cuatro rutas (incluida la captura histórica).
- El detalle por producto presenta SKU y programa nuevo, sin columna de unidad ni
  Descripción, también en Excel e impresión. La búsqueda por descripción sigue
  disponible. SKU permanece fijo durante el desplazamiento. Los totales indican
  su unidad en la etiqueta de la fila.
- Las tablas con desbordamiento tienen una barra horizontal superior,
  sincronizada en ambos sentidos con la inferior. Se recalcula al cambiar el
  tamaño y se omite en impresión. La prueba de navegador verifica ambos sentidos.
