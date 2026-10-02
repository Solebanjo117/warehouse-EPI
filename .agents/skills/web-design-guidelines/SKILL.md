---
name: web-design-guidelines
description: Revisar accesibilidad, formularios, foco, contenido, temas y rendimiento de las interfaces Razor Pages de Warehouse EPI. Adaptación de las guías de Vercel al flujo de captura, tablets y contratos del proyecto; no usar para otros frontends.
metadata:
  author: vercel
  version: "1.0.0-warehouse-epi.1"
  upstream-commit: "063bee94c3f4df8453406c830b0a7df0f2860278"
  guidelines-commit: "e3d624baaf29dc1fc645aff3e38f03e564d2d6b1"
---

# Web Design Guidelines para Warehouse EPI

Complementa [warehouse-epi-ui](../warehouse-epi-ui/SKILL.md) y su [contrato visual](../warehouse-epi-ui/references/visual-contract.md). Conserva Razor Pages, Bootstrap, los tokens, los selectores y los contratos operativos actuales.

## Revisión

Identifica las pantallas solicitadas o las afectadas por el diff. Lee su Razor, PageModel y JavaScript relacionado; distingue hallazgos de código de comportamientos comprobados en navegador. Una revisión no autoriza implementar cambios.

Aplica solo las comprobaciones pertinentes:

- **Semántica y foco:** etiquetas asociadas, nombre accesible en botones de icono, enlaces para navegación y botones para acciones, encabezados en orden y acceso al contenido principal. Usa la interacción de teclado nativa antes de añadir handlers. Comprueba que cabeceras fijas, drawer, modal y teclado en pantalla no tapen el control enfocado, y que el foco regrese al disparador al cerrar.
- **Captura:** conserva Enter/HID y el foco del siguiente campo sin saltos inesperados. No bloquees pegar ni la corrección manual. Verifica `name`, `type`, `inputmode`, `autocomplete` y `spellcheck` según el campo y el lector. Los errores deben quedar junto al dato y explicar cómo corregirlo; el progreso debe anunciarse sin robar el foco. Mantén revisión, NIP y protección contra envíos duplicados.
- **Contenido y datos:** prueba códigos y descripciones largos, valores grandes, cero, vacío, carga, error y conflicto. No ocultes información necesaria para identificar producto o ubicación mediante truncado sin alternativa. Considera `font-variant-numeric: tabular-nums` para cantidades comparables. Conserva filtros GET, enlaces, paginación del servidor y formatos de los servicios existentes.
- **Tablet y zoom:** conserva objetivos táctiles de al menos 44 px en acciones frecuentes, alternativas a hover/arrastre y zoom del navegador. Revisa orientación, safe areas y las transiciones de navegación en 900 y 1200 px. Resuelve el desbordamiento sin ocultar controles o datos con `overflow-x: hidden`.
- **Temas y movimiento:** revisa Claro, Oscuro y Sistema, también controles nativos, advertencias y foco. No transmitas estado solo con color. Respeta `prefers-reduced-motion`; evita `transition: all` y animación decorativa en captura.
- **Rendimiento:** imágenes con dimensiones y carga diferida cuando corresponda; lecturas/escrituras DOM agrupadas en interacciones frecuentes. Mantén scripts pesados por página y evita nuevas dependencias, CDNs, fuentes remotas y trabajo por cada pulsación que perjudique a tablets lentas.

## Adaptaciones deliberadas

- El foco inicial en tablet está justificado por el escáner. No elimines `autofocus` automáticamente por la regla genérica para móvil; verifica su comportamiento con el flujo existente.
- Un botón puede permanecer deshabilitado hasta completar una captura válida. Conserva la validación por pasos, la explicación de lo pendiente y la idempotencia del servidor.
- Usa el español y las convenciones de capitalización del proyecto; no impongas Title Case ni abreviaturas inglesas.
- La CSP exige JavaScript externo y enlaces mediante `data-*`; los ejemplos JSX de eventos, hidratación o React no se trasladan literalmente a Razor.
- Prefiere paginación en servidor antes que virtualización del cliente. No añadas React, Tailwind, bibliotecas de iconos ni preconexiones externas para satisfacer una guía genérica.
- No serialices NIP, cookies o secretos en URLs, capturas, trazas o almacenamiento. Las fechas, unidades y saldos siguen los modelos y servicios del almacén; `Intl` solo corresponde cuando el cliente necesita formatearlos.

## Entrega

Presenta cada hallazgo con archivo, línea comprobada, consecuencia operativa y corrección concreta. No marques como defecto una excepción justificada arriba. Identifica la revisión en navegador y la validación física pendiente por separado. Para revisar en navegador, usa [playwright-cli](../playwright-cli/SKILL.md) cuando resulte apropiado; para implementar una corrección, sigue la verificación de `warehouse-epi-ui`.

## Procedencia

Adaptación local de [la skill de Vercel](https://github.com/vercel-labs/agent-skills/blob/063bee94c3f4df8453406c830b0a7df0f2860278/skills/web-design-guidelines/SKILL.md) y de [Web Interface Guidelines](https://github.com/vercel-labs/web-interface-guidelines/blob/e3d624baaf29dc1fc645aff3e38f03e564d2d6b1/command.md), revisadas el 28 de septiembre de 2026. La [skill original](upstream-skill.md) se conserva para trazabilidad. Esta lista funciona sin descargar reglas en cada revisión; consulta las fuentes al actualizarla deliberadamente.
