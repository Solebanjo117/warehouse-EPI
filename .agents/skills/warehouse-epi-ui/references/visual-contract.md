# Contrato visual de Warehouse EPI

Usa este documento para tomar decisiones de interfaz. Confirma siempre el código y `docs/CONTEXT.md` actuales: este contrato describe invariantes del producto, no sustituye la inspección del repositorio.

## Arquitectura visual

- La interfaz es ASP.NET Core Razor Pages con Bootstrap, CSS propio y JavaScript sin un framework SPA.
- El producto se llama **Warehouse EPI**. El nombre y logo configurables identifican el negocio y el almacén, no reemplazan el nombre del producto.
- La base visual usa fondo gris claro, superficies blancas, azul petróleo y estados activos con color, contraste y peso tipográfico. Los temas Claro, Oscuro y Sistema deben continuar funcionando mediante los tokens Bootstrap y las variables existentes.
- Usa el sprite SVG y los iconos existentes cuando cubran la acción. Un icono operativo debe acompañarse de texto o nombre accesible.
- Prefiere jerarquía, espacio y tipografía sobre adornos. Evita gradientes, sombras o animaciones nuevas que no ayuden a reconocer estado, prioridad o agrupación.

## Navegación responsive

Conserva una sola arquitectura de navegación:

- `>= 1200 px`: sidebar de 250 px, colapsable a rail de 72 px; el estado colapsado puede persistir localmente.
- `900–1199.98 px`: rail de 72 px con expansión superpuesta.
- `< 900 px`: barra superior y drawer.

El drawer debe controlar `aria-expanded`, contener el foco de forma razonable, cerrar con Escape o backdrop y devolver el foco al disparador. La página activa usa `aria-current="page"`. Las rutas ADMIN se ocultan para otros usuarios, sin reemplazar la autorización del servidor.

Cuando la cámara está activa, oculta navegación y distracciones; el lector ocupa la ventana disponible. Respeta `100dvh`, safe areas y orientación. No cierres el lector porque un cuadro no produjo detección.

## Controles y accesibilidad

- Mantén objetivos táctiles cercanos a 44 px o mayores para acciones frecuentes.
- Cada control requiere etiqueta o nombre accesible; los placeholders no sustituyen etiquetas.
- Conserva foco visible, orden DOM lógico, navegación por teclado y `prefers-reduced-motion`.
- Anuncia resultados, correcciones automáticas y errores cerca del campo o acción que los causó.
- No dependas solo de color. Combina texto, iconografía, borde, forma o peso según corresponda.
- Evita modales para información que puede vivir en la página. Úsalos cuando sea necesario bloquear una confirmación, NIP o sesión de cámara.

## Patrones por tipo de pantalla

### Operación

- Optimiza el recorrido producto → ubicación → cantidad → revisión → NIP/confirmación.
- Los pasos muestran estados textuales como Pendiente, En captura y Listo; permiten corregir una selección sin perder valores válidos no relacionados.
- Mantén Enter y lectores HID como caminos principales. Cámara y captura escrita deben resolver mediante los contratos existentes.
- Mantén advertencias operativas visibles, pero no bloquees comportamientos permitidos, como saldo negativo, si el dominio actual los admite.
- El resumen permanece lateral en laptop ancha y se integra al flujo en anchos menores. Solo habilita la revisión cuando los datos requeridos son válidos.
- El NIP nunca se repuebla después de un POST fallido.

### Consulta

- Prioriza lectura rápida: encabezado, indicadores confiables, filtros, resultado, contexto y enlaces de trazabilidad.
- Las consultas públicas permanecen sin escritura. Las acciones ADMIN se distinguen visualmente y continúan protegidas.
- Los saldos provienen del modelo real; no confundas asignación con ocupación ni combines cantidades de unidades distintas.
- En trazabilidad, presenta fecha local del almacén, folio, responsable, propósito, líneas, cambios de saldo y cadena de corrección sin modificar el historial.

### Listados y detalle

- Conserva filtros GET, búsqueda parcial cuando el módulo ya la admite y paginación en servidor, normalmente de 25 registros según el contrato de la pantalla.
- En laptop, una tabla puede ser la vista principal. En tablet vertical, proporciona tarjetas compactas o una composición equivalente cuando la tabla requiera desplazamiento horizontal excesivo.
- Separa listado, detalle de consulta y edición explícita. Después de crear o editar, prefiere volver a la ficha de detalle con confirmación cuando ese sea el contrato existente.
- Estados vacíos explican qué filtro o acción puede producir resultados. No inventes estadísticas para llenar espacio.

### Tableros y gráficas

- Usa métricas derivadas de consultas existentes y muestra la hora o vigencia del dato.
- Conserva leyendas, etiquetas y fallback tabular accesible para Chart.js.
- No sacrifiques trazabilidad por densidad visual. Un indicador debe enlazar a la consulta que explica sus datos cuando exista.
- Diferencia cero real, sin datos, carga y error de actualización.

## Rendimiento y resiliencia local

- No agregues llamadas a CDNs, fuentes remotas ni servicios externos: la operación principal debe sobrevivir sin Internet.
- Evita imágenes grandes, animaciones costosas, observadores globales innecesarios y bibliotecas completas para una interacción pequeña.
- Mantén las consultas, filtros y paginación en servidor cuando el volumen pueda crecer.
- No escondas errores de red. Conserva la captura válida en el cliente cuando sea seguro y ofrece una acción clara para reintentar, sin duplicar el POST.
