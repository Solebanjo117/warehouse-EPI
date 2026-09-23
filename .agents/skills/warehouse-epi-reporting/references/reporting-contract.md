# Contrato analítico de Warehouse EPI

Confirma siempre el código y `docs/CONTEXT.md` actuales. Este documento conserva invariantes del dominio; no reemplaza la inspección del repositorio.

## Definir el reporte

Antes de escribir una consulta, establece:

- la pregunta operativa y la decisión que soporta;
- la población incluida y excluida;
- la granularidad de una fila: operación, línea, cambio de saldo, producto, ubicación, día o campaña;
- el instante o periodo de negocio y la zona horaria;
- las unidades y si pueden agregarse;
- el orden determinista y el desempate;
- la audiencia pública, OPERATOR o ADMIN;
- la relación entre pantalla, detalle, gráfica y exportación.

No mezcles cantidades de unidades distintas. Ocupación se deriva del saldo real, no de una asignación; una asignación puede explicar relación o capacidad, pero no inventario presente.

## Movimientos efectivos y trazabilidad

Cuando el reporte describa operación vigente, parte de `EffectiveMovementQuery.ApplyFilter` o del seam equivalente actual. Conserva el modelo inmutable:

- un movimiento confirmado no se edita;
- una corrección se representa mediante reverso y, cuando aplica, reemplazo;
- el análisis efectivo evita contar como vigentes tanto el original corregido como su compensación;
- el detalle de auditoría puede mostrar toda la cadena, claramente identificada, sin confundirla con totales netos.

Los lotes, ubicaciones y saldos históricos deben provenir de snapshots o cambios de saldo conservados por el movimiento cuando ese sea el contrato actual; no reconstruyas historia usando únicamente el estado presente.

## Fechas e intervalos

- Usa `WarehouseClock` y la zona horaria configurada del almacén.
- Convierte fechas locales a un intervalo UTC semiabierto: inicio inclusivo y fin exclusivo.
- No uses `DateTime.Now`, `DateTime.UtcNow` disperso ni `ToLocalTime()` en vistas para producir fechas de negocio.
- Presenta al usuario fechas locales y conserva el offset o zona horaria en exportaciones cuando sea útil para reproducir el resultado.
- Define explícitamente si “sin movimiento” significa nunca, antes del periodo o fuera de una ventana determinada.

## Filtros y búsqueda

- Conserva filtros GET y parámetros URL existentes para enlaces reproducibles.
- Producto y ubicación aceptan fragmentos sin distinguir mayúsculas cuando el módulo equivalente ya lo hace.
- El filtro de producto puede cubrir SKU, descripción, referencia externa y códigos de barras según el contrato actual.
- El filtro de ubicación puede cubrir área operativa, origen, destino y ubicaciones históricas de cambios de saldo.
- Los filtros dedicados combinan con semántica AND salvo que la pantalla indique explícitamente otra cosa.
- Un buscador general no debe alterar silenciosamente la semántica de filtros dedicados.
- Normaliza entrada vacía y espacios sin convertir la consulta en coincidencia total accidental.

## Consultas y agregación

- Usa `AsNoTracking` para lectura.
- Aplica autorización, filtros y límites antes de materializar.
- Proyecta DTOs simples y selecciona solo columnas necesarias.
- Usa orden total y estable antes de paginar; agrega un desempate estable cuando una métrica pueda empatar.
- Calcula conteos y totales sobre la granularidad declarada. Una exportación de movimientos puede limitar líneas de detalle, no solo operaciones.
- Evita evaluación implícita en cliente. Para una operación que EF no traduzca, divide la consulta en etapas explícitas y acotadas y valida contra PostgreSQL.
- Mantén el saldo agregado como fuente de verdad para totales actuales; usa historial para explicar cambios, no para inferir el presente si ya existe el saldo persistido.

## Pantallas y tableros

- Distingue cero real, sin resultados, carga, error y dato desactualizado.
- Muestra filtros aplicados, periodo, hora de actualización y unidad cuando afecten la interpretación.
- Una tarjeta o gráfica resume; la tabla o el detalle explica. Mantén enlaces de trazabilidad cuando existan.
- Usa Chart.js empaquetado localmente y conserva fallback tabular accesible.
- No añadas actualización automática agresiva en tablets lentas; respeta el mecanismo de caché o refresco actual.
- Para decisiones de interfaz responsive y accesibilidad, usa `warehouse-epi-ui` además de esta skill.
