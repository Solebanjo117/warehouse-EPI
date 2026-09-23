# Verificación de UI de Warehouse EPI

Aplica comprobaciones proporcionales al cambio. No inicies, detengas, publiques ni reemplaces la aplicación o el servicio salvo autorización explícita.

## Inspección estática

- Revisa el diff completo y confirma que no se sobrescribió trabajo local.
- Ejecuta `git diff --check` sobre el cambio.
- Comprueba HTML/Razor semántico, etiquetas, nombres accesibles, foco, teclado, estados vacíos y mensajes de error.
- Busca todos los consumidores de clases, IDs, atributos `data-*`, handlers Razor y selectores JavaScript modificados.
- Si se tocó JavaScript, ejecuta `node --check` para cada archivo propio afectado.
- Si se tocaron rutas o contratos visibles, actualiza o agrega la prueba focal existente; evita pruebas que solo congelen texto decorativo.

## Compilación y pruebas

- Ejecuta build y pruebas secuencialmente; las ejecuciones paralelas pueden competir por binarios.
- Usa la comprobación más pequeña que cubra el cambio y amplíala según el riesgo.
- Si la aplicación en ejecución bloquea binarios, no la cierres automáticamente. Correlaciona primero proceso, servicio y puertos; para validar sin interferir puede usarse una salida aislada con `UseAppHost=false`.
- Los HTTP 400 de antiforgery del host de pruebas documentado no demuestran por sí solos una regresión visual. Compara contra la línea base y conserva evidencia del chequeo focal.

## Navegador

Cuando exista una instancia que el usuario haya abierto o haya autorizado abrir, verifica al menos los anchos cercanos a estas transiciones:

- por debajo de 900 px: topbar y drawer;
- 900–1199.98 px: rail y expansión superpuesta;
- 1200 px o más: sidebar expandido/colapsado.

Comprueba Claro, Oscuro y Sistema; teclado y foco; textos largos; estados sin datos y con error; scroll; solapamientos; acción primaria; visibilidad ADMIN; y retorno de foco después de drawer o modal. Para cámara, comprueba que la navegación se oculte, que la sesión permanezca abierta sin detección y que el cierre libere la cámara.

Una emulación de viewport es validación visual en navegador, no validación física.

## Validación física

Marca como pendiente hasta probar en el dispositivo correspondiente:

- orientación horizontal y vertical de tablets reales;
- lector HID y comportamiento de Enter/foco;
- cámara trasera, selección y persistencia de dispositivo;
- lectura de un Code 128 largo real;
- sonido, vibración, rendimiento y teclado en pantalla;
- dimensiones, DPI y márgenes de impresora cuando la UI incluya impresión.

Informa por separado compilación, pruebas automatizadas, revisión visual en navegador y prueba física.
