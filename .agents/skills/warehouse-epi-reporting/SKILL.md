---
name: warehouse-epi-reporting
description: Diseñar, implementar, auditar o corregir reportes de Warehouse EPI en ASP.NET Core, EF Core, PostgreSQL, Razor Pages, ClosedXML, CSV y Chart.js, preservando movimientos efectivos, filtros, zona horaria, límites, permisos y trazabilidad. Usar para consultas, tableros, métricas y exportaciones dentro de este repositorio; no usar para analizar una hoja de cálculo aislada ni para reportes de otros proyectos.
---

# Warehouse EPI Reporting

Produce reportes explicables, reproducibles y seguros sobre los datos operativos de Warehouse EPI. Conserva la trazabilidad y el significado del dominio antes que la apariencia o la conveniencia de una consulta.

## Antes de actuar

1. Confirma que el repositorio es Warehouse EPI y lee la sección pertinente de `docs/CONTEXT.md`, especialmente Fase 13 cuando corresponda.
2. Revisa `git status`, el diff y los archivos actuales. Conserva todo trabajo local no relacionado.
3. Inspecciona los contratos en `Infrastructure/Reporting`, el servicio de consulta, el `PageModel`, la vista, el exportador y las pruebas existentes antes de proponer otro patrón.
4. Define qué pregunta operativa responde el reporte, quién puede verlo, qué representa cada fila, qué fecha gobierna el intervalo y qué decisión soporta.
5. Si la petición es auditar, explicar o proponer, no modifiques archivos. Si pide corregir, aplicar o realizar el plan, implementa el alcance acordado y añade verificación focal.

No inicies ni detengas la aplicación o el servicio Windows salvo petición explícita. No consultes ni migres la base operativa solo para diseñar un reporte; usa una base de prueba aislada cuando la traducción PostgreSQL deba comprobarse.

## Contrato analítico

Lee [references/reporting-contract.md](references/reporting-contract.md) antes de crear o modificar una consulta, filtro, métrica, tablero o tabla.

Mantén estas fronteras:

- Los reportes son de solo lectura. No hagas que consultar, descargar, imprimir o actualizar un tablero cambie inventario, movimientos, conteos o auditoría.
- Reutiliza DTOs inmutables, servicios de consulta, `WarehouseClock` y contratos existentes. No calcules saldos ni fechas de negocio de nuevo en Razor o JavaScript.
- Reporta movimientos efectivos cuando el análisis pretenda describir el efecto vigente. No mezcles originales corregidos, reversos y reemplazos como si todos fueran operación neta.
- Conserva permisos y visibilidad. Una descarga debe aplicar la misma autorización y filtros que su pantalla, salvo una diferencia explícita y documentada.
- No agregues migraciones, paquetes o dependencias para un cambio que pueda resolverse con la arquitectura actual.
- No refactorices `ReportExportService` u otros servicios por tamaño o duplicación aparente sin demostrar menor riesgo, una interfaz más clara o cobertura mejor.

## Implementación

Haz el cambio más pequeño que mantenga el contrato completo:

1. Define o reutiliza un filtro tipado y un DTO de salida; no expongas entidades EF directamente a la vista o al exportador.
2. Mantén una sola fuente para aplicar filtros a pantalla y exportación. Preserva parámetros URL existentes cuando formen parte del contrato visible.
3. Usa consultas `AsNoTracking`, orden determinista y paginación del servidor. Evita materializar conjuntos sin límite antes de filtrar.
4. Proyecta solo los datos necesarios. Si EF/PostgreSQL no traduce una agrupación compleja, reduce primero a filas simples y acotadas, materializa y agrupa en memoria; comprueba la traducción real antes de afirmar que funciona.
5. Mantén estados distintos para carga, cero real, sin resultados por filtros, dato desactualizado y error.
6. Para una gráfica, conserva una lectura textual o tabular accesible y enlaza a la consulta que explica el indicador cuando exista.

## Exportaciones

Para crear o cambiar Excel o CSV, lee [references/exports.md](references/exports.md). No trates Excel y CSV como una serialización indiferenciada: conserva tipos, codificación, metadatos y defensas específicas de cada formato.

Si la petición requiere un PDF o documento con fidelidad visual, usa la skill especializada correspondiente además de esta; el contrato analítico continúa siendo responsabilidad de `warehouse-epi-reporting`.

## Verificación y entrega

Para una implementación o corrección, lee [references/verification.md](references/verification.md) y ejecuta comprobaciones proporcionales al riesgo.

Entrega por separado:

- definición de la métrica o población reportada;
- código y pruebas automatizadas verificadas;
- validación visual en navegador, si existió;
- validación LAN, tablet, impresión o datos operativos aún pendiente.

No presentes una consulta InMemory como prueba suficiente de traducción PostgreSQL ni una inspección estructural del XLSX como validación de impresión.
