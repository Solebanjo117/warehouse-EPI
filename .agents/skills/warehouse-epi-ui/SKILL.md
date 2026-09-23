---
name: warehouse-epi-ui
description: Diseñar, implementar o revisar interfaces de Warehouse EPI en ASP.NET Core Razor Pages, Bootstrap, CSS y JavaScript, preservando sus contratos operativos, responsivos y de acceso. Usar para pantallas, formularios, navegación, tablas, tarjetas, tableros, cámara/HID, temas y accesibilidad de este repositorio; no aplicar a HermeX ni a otros frontends.
---

# Warehouse EPI UI

Trabaja dentro del lenguaje visual y la arquitectura existentes de Warehouse EPI. Mejora la claridad, velocidad y seguridad de la operación en laptop y tablets Android lentas sin convertir la tarea en una reescritura del frontend.

## Antes de actuar

1. Confirma que el repositorio es Warehouse EPI y lee la sección pertinente de `docs/CONTEXT.md`.
2. Revisa `git status`, el diff y los archivos actuales antes de editar. Conserva todo trabajo local no relacionado.
3. Inspecciona la página Razor, su `PageModel`, los componentes compartidos, `wwwroot/css/site.css`, el JavaScript relacionado y las pruebas de contrato existentes.
4. Clasifica la pantalla como operación, consulta pública, consulta ADMIN o edición ADMIN. Conserva esa separación y la autorización del servidor.
5. Si la petición es solo diseñar, explicar o revisar, no implementes cambios. Si la petición pide corregir, aplicar o realizar el plan, implementa el alcance acordado y verifícalo.

No inicies ni detengas la aplicación, el servicio Windows ni una Release publicada salvo petición explícita.

## Contrato de diseño

Lee [references/visual-contract.md](references/visual-contract.md) antes de diseñar, implementar o revisar una interfaz. Aplica únicamente las secciones relevantes a la pantalla.

Mantén estas fronteras:

- Reutiliza Razor Pages, Bootstrap, los tokens CSS y el JavaScript empaquetado. No introduzcas otro framework, CDN o dependencia de Internet para resolver una mejora visual.
- No cambies entidades, migraciones, saldos, movimientos, NIP, idempotencia, permisos ni contratos POST como efecto colateral de una tarea visual. Si un cambio de datos es imprescindible, señálalo y limita el trabajo al alcance autorizado.
- No dupliques cálculos de inventario, zona horaria ni permisos en la vista. Consume los modelos y servicios existentes.
- Mantén el texto operativo en español, breve y accionable. Distingue información, advertencia, error y confirmación también con texto o iconos; el color por sí solo no basta.
- Prioriza el flujo principal. Las acciones destructivas o administrativas deben ser explícitas; la consulta debe permanecer separada de la edición.
- Diseña con datos reales del modelo y estados vacíos plausibles. No añadas datos simulados al código de producción.

## Implementación

Haz el cambio más pequeño que produzca una pantalla coherente:

1. Reutiliza primero clases y componentes existentes; agrega una variante compartida cuando el patrón se repita.
2. Mantén el HTML semántico, etiquetas asociadas, orden de tabulación, `aria-current`, `aria-expanded`, foco visible y cierre con Escape cuando corresponda.
3. Conserva nombres y selectores usados por JavaScript, pruebas y automatización. Si deben cambiar, actualiza todos sus consumidores y añade cobertura focal.
4. Evita trabajo pesado en el cliente. La aplicación debe seguir siendo usable con hardware limitado, Wi-Fi local irregular y sin Internet.
5. En pantallas de captura, no rompas Enter/HID, cámara, corrección de escaneo cruzado, foco siguiente, NIP ni el resumen previo a confirmar.
6. En listados, conserva filtros GET, paginación del servidor y una alternativa legible a tablas anchas en tablet vertical.
7. En tableros y gráficas, conserva una representación textual o tabular accesible y no presentes métricas aproximadas como saldos reales.

## Verificación y entrega

Para una implementación o corrección, lee [references/verification.md](references/verification.md) y ejecuta comprobaciones proporcionales al riesgo.

Entrega el resultado separando claramente:

- lo implementado y verificado en código;
- la validación visual realizada en navegador, si existió;
- la validación física pendiente en laptop, tablet, HID, cámara o impresora.

Nunca declares validación física basándote solo en build, pruebas, viewport simulado o inspección del código.
