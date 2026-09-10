---
name: ui-operaciones
description: Guía de diseño e interfaz para las pantallas de Warehouse EPI (Razor Pages + Bootstrap, tablets Android en la nave). Úsala al crear o rediseñar cualquier `.cshtml`, al tocar `wwwroot/css/site.css` o `wwwroot/js/*.js`, y al revisar la UI de una fase antes de integrarla.
---

# UI de operaciones — Warehouse EPI

El dispositivo objetivo es una tablet Android con Chrome, sujeta con una mano,
en una nave con luz irregular, operada por alguien con guantes y un escáner. La
pantalla no compite por atención: **compite contra el reloj y contra el error de
captura**. Cada decisión visual se juzga por si acelera la operación o la
protege, no por si se ve moderna.

Antes de diseñar, ubica la pantalla en uno de estos tres registros. Mezclarlos
es el error más común:

| Registro | Ejemplos | Prioridad |
|---|---|---|
| **Captura** | `Pages/Operations/*` | Velocidad, un paso a la vez, cero ambigüedad |
| **Consulta** | `Pages/Inventory`, `Pages/Reports` | Densidad legible, filtros, escaneo visual |
| **Administración** | `Pages/Admin/*` | Control y detalle; es escritorio, no tablet |

## Restricciones no negociables

Cada una está verificada por una prueba en `tests/WarehouseEPI.Tests/Web`. No
son preferencias: romperlas rompe la build.

- **Sin JS inline.** La CSP es `script-src 'self'` sin `'unsafe-inline'`. Nada
  de `<script>` en `.cshtml`, ni `onclick=`, `onchange=`, `onsubmit=`, ni
  `href="javascript:"`. El navegador los bloquea en silencio y el control
  simplemente deja de responder. Todo el JS vive en `wwwroot/js/*.js` y se
  engancha por atributos `data-*`.
  → `ContentSecurityPolicyContractTests`
- **Presupuesto de scripts.** `_Layout.cshtml` solo carga `site.js`,
  `operational-notifications.js` y el bundle de Bootstrap. ZXing (395 KB),
  jQuery (88 KB) y `operations.js` (56 KB) los carga **solo** la página que los
  usa, vía su sección de scripts. Si añades una librería, pregúntate primero si
  la tablet más lenta la puede pagar.
  → `ScriptLoadingContractTests`
- **Sin regresiones de ruta ni de contrato HTML.** Los tests de `Web/` afirman
  marcado concreto (atributos `data-*`, ids, textos). Si cambias la estructura
  de una pantalla, actualiza su test en el mismo commit.

## Sistema visual

**Color: siempre tokens de Bootstrap, nunca hex crudo.** `var(--bs-body-bg)`,
`--bs-body-color`, `--bs-border-color`, `--bs-tertiary-bg`, `--bs-primary`, y
las variantes `--bs-*-bg-subtle` / `--bs-*-border-subtle` para los estados. El
tema claro/oscuro se aplica con `data-bs-theme` desde `theme-init.js`: un hex
literal se ve bien en un tema y se rompe en el otro. El croquis del almacén
(`.map-element`, `.map-position`) tiene hex heredados; es deuda conocida, no un
patrón a imitar.

Los estados de ubicación ya tienen semántica de color establecida — respétala:
`occupied` azul, `blocked` ámbar, `inactive` gris, `negative` rojo, `missing`
punteado y atenuado. Y nunca codifiques un estado **solo** con color: acompáñalo
de borde, texto o icono.

**Tipografía.** La base es `html { font-size: 14px }`. Los datos que el operario
lee de un vistazo van grandes y en negrita (código de ubicación, SKU, cantidad:
`1.2rem`–`1.5rem`); las etiquetas y metadatos van pequeños y en
`var(--bs-secondary-color)`. La jerarquía la carga el peso y el tamaño, no el
color.

**Iconos.** Sprite SVG en `_Layout.cshtml`, se usan con
`<svg class="app-icon" aria-hidden="true"><use href="#icon-…" /></svg>`. Para
añadir uno, agrega un `<symbol>` al sprite; no traigas una librería de iconos.

## Ergonomía táctil

- **44 px de alto mínimo** en todo lo que se toque: botones, filas de lista,
  celdas del keypad. Ya es el estándar del repo (`min-height: 44px` aparece 20
  veces en `site.css`).
- Los campos de captura van en `input-group-lg`: el escáner escribe, pero el
  dedo corrige.
- `autocomplete="off"` y `autocapitalize="characters"` en códigos de producto y
  ubicación. `type="search"` para que la tablet ofrezca borrar rápido.
- `autofocus` en el primer campo de la operación: el operario escanea sin tocar
  la pantalla.
- Nada crítico detrás de hover: en tablet no existe.
- El objetivo táctil no se encoge en móvil. Si algo debe ceder en pantalla
  chica, que sea el padding o el texto secundario, nunca el área tocable.

## Patrón de captura guiada

`Pages/Operations/_GuidedMovementForm.cshtml` es la referencia canónica. Un paso
visible a la vez, con tres partes por `<section class="entry-step">`:

1. **Cabecera** — número, título, `data-entry-step-status` ("Pendiente" / "En
   captura" / listo) y un botón "Cambiar" para volver.
2. **Cuerpo** — un solo campo de captura con su `lookup-field`, resultados y el
   registro seleccionado.
3. **Resultado** — resumen compacto de lo elegido, para que el paso cerrado siga
   siendo legible.

El progreso se anuncia en un `role="status" aria-live="polite"` ("2 de 4
listos"). Un formulario nuevo de operación debe reutilizar este parcial o
replicar su estructura y sus atributos `data-*`; no inventes un flujo paralelo.

Recuerda las reglas de dominio que la UI debe hacer evidentes: **una operación,
un producto**; el NIP se pide en cada movimiento aunque haya cookie de ADMIN; el
inventario negativo **advierte pero no bloquea** — muéstralo como advertencia
clara, nunca como error que impida continuar.

## Texto de interfaz

Español, voz activa, mayúscula solo inicial. El texto es material de diseño, no
decoración.

- **Nombra por lo que el operario controla**, no por cómo está construido:
  "Ubicación destino", no "DestinationLocationId".
- **El mismo verbo en todo el flujo.** Si el botón dice "Registrar entrada", la
  confirmación dice "Entrada registrada". Nunca "Enviar" ni "Aceptar".
- **Los errores dicen qué pasó y qué hacer**, en la voz de la interfaz, sin
  disculparse ni ser vagos: "El saldo cambió mientras capturabas. Vuelve a
  consultar la ubicación antes de ajustar." Nada de "Ocurrió un error".
- **Las pantallas vacías invitan a actuar**, no informan de vacío: "Sin
  movimientos hoy. Registra una entrada para empezar."
- **Cada elemento hace un trabajo.** La etiqueta etiqueta, el `form-text`
  explica; no repitas la etiqueta en el placeholder.
- Nunca muestres, registres ni pases por query string un NIP.

## Piso de accesibilidad

No es opcional y es barato:

- Botón con solo icono → `aria-label` (o `<span class="visually-hidden">`).
- Feedback dinámico → `role="status"` con `aria-live="polite"`; errores de
  validación → `role="alert"` con `tabindex="-1"` para poder enfocarlos.
- Foco de teclado visible siempre; no elimines el outline sin sustituirlo
  (`:focus-visible` con `outline: 3px solid`).
- Contraste suficiente para luz de nave: no uses gris claro sobre blanco para
  información que se necesita leer.
- Números decorativos y sprites → `aria-hidden="true"`.
- Respeta `prefers-reduced-motion` si añades animación. Y en captura, casi
  nunca la necesitas.

## Antes de dar por terminada la pantalla

1. ¿Se ve correcta en tema claro **y** oscuro? (todo color vía token)
2. ¿Todo lo tocable llega a 44 px, también en móvil?
3. ¿Cero `<script>` inline y cero `on*=` en el `.cshtml`?
4. ¿Los scripts pesados los carga la página y no `_Layout`?
5. ¿El texto usa el mismo verbo de principio a fin?
6. ¿Los errores dicen cómo salir del problema?
7. ¿Actualizaste el test de contrato de la pantalla?

Verificación:

```powershell
dotnet test tests\WarehouseEPI.Tests\WarehouseEPI.Tests.csproj --filter "FullyQualifiedName~Web"
pwsh ./scripts/quality.ps1
```

Para verlo de verdad, `dotnet run --project src\WarehouseEPI.Web` y abre
`http://localhost:5142` reduciendo la ventana al ancho de una tablet. La
interfaz se prueba en el dispositivo más lento, no en el monitor del
desarrollador.
