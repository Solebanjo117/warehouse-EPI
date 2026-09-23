# Verificación de Reporting

Aplica el conjunto más pequeño que demuestre el contrato modificado y amplíalo según el riesgo. Ejecuta build y pruebas secuencialmente para evitar contención de binarios.

## Consulta y dominio

- Prueba inclusiones, exclusiones, bordes del periodo y orden determinista.
- Prueba cada filtro por separado y combinaciones AND relevantes.
- Para producto y ubicación, cubre coincidencias parciales, mayúsculas/minúsculas, referencia, código de barras y ubicación histórica cuando pertenezcan al contrato.
- Cubre movimientos normales, originales corregidos, reversos, reemplazos y ajustes según la población reportada.
- Verifica granularidad: operaciones, líneas, cambios o productos no deben intercambiarse en conteos y límites.
- Para ocupación y saldos, incluye asignación sin saldo, saldo sin asignación, cero, negativo y unidades distintas cuando sean casos válidos.

## EF Core y PostgreSQL

- Las pruebas InMemory son útiles para reglas puras, pero no prueban traducción SQL ni semántica PostgreSQL.
- Cuando se cambie una consulta no trivial, ejecuta la prueba focal contra `warehouse_epi_test` o la base aislada equivalente; nunca uses la base operativa para sembrar una prueba.
- Reutiliza datos sembrados salvo que la prueba necesite mutación. Evita actualizar entidades compartidas de fixture de forma que provoque concurrencia no relacionada.
- Comprueba que filtros, agrupaciones, orden y paginación se ejecuten en el servidor o que cualquier materialización intermedia sea explícita y acotada.

## Exportaciones

- Reabre el XLSX con ClosedXML y verifica nombres de hoja, encabezados, tipos nativos, formatos, filtros, totales y contenido.
- Prueba texto que empiece con `=`, `+`, `-`, `@` y tabulación, además de números negativos reales.
- Verifica CSV con BOM, comillas, comas, saltos de línea, cultura invariante y fórmula neutralizada.
- Prueba exactamente el límite y un elemento por encima. El exceso debe rechazarse, no truncarse.
- Confirma que pantalla y archivo aplican la misma población y filtros.

## UI y rutas

- Prueba autorización del handler de exportación y no solo visibilidad del enlace.
- Verifica persistencia de parámetros GET, paginación y mensajes de sin resultados o exceso de filas.
- Para Chart.js, comprueba que el dataset derive del snapshot correcto, que exista fallback tabular y que carga, error y dato desactualizado sean distinguibles.
- Usa navegación real solo si existe una instancia abierta por el usuario o autorización para abrirla. Un viewport simulado no sustituye tablet, LAN ni impresión física.

## Cierre

- Ejecuta `git diff --check` y revisa el diff completo.
- Ejecuta build y pruebas focales de Reporting; amplía a Quality o suite completa cuando el alcance lo justifique.
- Si una aplicación en ejecución bloquea binarios, no la cierres automáticamente. Correlaciona proceso, servicio y puertos o usa salida aislada con `UseAppHost=false`.
- Los HTTP 400 de antiforgery del host de pruebas documentado no prueban por sí solos una regresión del reporte. Compara con la línea base y reporta por separado el veredicto focal.
- Distingue código verificado, consulta PostgreSQL verificada, navegador revisado y validación física pendiente.
