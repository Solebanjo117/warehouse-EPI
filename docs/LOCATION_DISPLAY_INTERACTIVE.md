# Consulta interactiva en la exhibición de filas

Implementada en código el 1 de octubre de 2026. No requiere migraciones.

En `/Locations/Display?play=true`, tocar un rack del croquis lo abre con sus nueve
posiciones y la lista completa de productos por posición. También se pueden
consultar las áreas especiales. La pantalla se pausa durante la consulta;
**Reanudar** regresa a la pantalla del recorrido donde se inició y conserva las
filas, el orden, la orientación y la cantidad de racks configurados.

El rack consultado conserva la cabecera de fila, la cuadrícula, tipografía y
proporciones del rack individual del recorrido, con el croquis al lado en
horizontal. Muestra además las descripciones y listas completas de productos.

**Buscar producto** abre un panel dentro de la pantalla y conserva el rack
consultado mientras se escribe o se elige un producto. Reutiliza el desplegable
`lookup-field` / `lookup-results` de la aplicación: sugiere productos al escribir,
permite elegir con toque, clic o flechas y Enter; Escape cierra primero las
sugerencias. Admite SKU, descripción,
referencia y códigos de barras activos, con Enter como respaldo para lectores
HID. Incluye productos inactivos para consultar inventario existente. Una
coincidencia exacta tiene prioridad; las búsquedas parciales devuelven hasta cinco
productos. La consulta abarca todo el almacén, aunque la exhibición recorra una
sola fila.

Las ubicaciones se agrupan en **Con saldo** y **Asignado sin saldo**. El saldo es
neto por producto y ubicación, sumando sus lotes. Los saldos negativos, las
ubicaciones bloqueadas/inactivas y los productos inactivos se identifican con
texto. Las ubicaciones retiradas físicamente se excluyen. Una asignación a cero
no confirma existencia física. Los resultados sin representación en el croquis
mantienen una indicación textual y permiten abrir su contenido.

Los resultados resaltan racks y áreas: borde verde para coincidencias, punteado
para asignaciones sin saldo y borde azul para la selección. Elegir una ubicación
marca su pallet y el producto buscado. Cerrar la búsqueda con su botón o Escape
conserva la pausa; solamente **Reanudar** vuelve al recorrido.

## Lectura y actualización

Handlers públicos GET en `/Locations/Display`:

- `handler=Products&q=…`: lista de productos, exacta o parcial.
- `handler=ProductLocations&productId=…`: producto, ubicaciones con identidad
  estructurada y estados, cantidades, unidades y hora local de consulta.
- `handler=Inspect&rowCode=…&rackNumber=…` o `handler=Inspect&locationId=…`:
  parcial HTML de consulta. La ubicación de un pallet abre su rack completo.

Son consultas de solo lectura, con `no-store`. Los datos visibles se actualizan
cada dos segundos. Una selección nueva cancela la solicitud anterior y las
respuestas tardías se descartan. Ante errores de red se conservan los últimos
datos y su hora, con un aviso; se vuelve a intentar en la siguiente actualización.

## Verificación

Las pruebas `LocationDisplayInspectionTests` cubren los handlers públicos,
coincidencias exactas/parciales, estados, suma de saldos, inventario a cero,
contenido completo, áreas, ubicaciones retiradas y español/inglés.

`tests/javascript/location-display-inspection.browser.cjs` usa pantallas HTML
generadas por esos tests y solicitudes simuladas en Edge. Cubre pausa/retorno,
resaltados, sugerencias al escribir, selección con flechas/Enter, Enter directo
para HID, Escape/foco, actualización, fallos de red, respuestas tardías,
conservación del rack y geometría del croquis, temas y anchos de 768 a 1280 px. Las suites existentes
conservan la cobertura del carrusel, agrupación, orientación y gestos.

La revisión con fixtures no valida la instalación publicada ni dispositivos
físicos. Queda pendiente comprobar tablet Android y lector HID reales. No se
publicó ni reinició la aplicación o el servicio Windows.

Verificación inicial: 78 pruebas focales de servidor/inventario/localización/CSP y
22 pruebas JavaScript del carrusel aprobadas; escenarios de consulta y regresión
de agrupación aprobados en Edge. La suite ampliada de mapa tuvo 104 aprobadas y
3 fallos en contratos antiguos de navegación/formularios ajenos a la exhibición.

El ajuste visual y del selector pasó 52 pruebas focales de servidor/localización/
CSP/scripts y 22 pruebas JavaScript del carrusel. En Edge se comprobó la geometría
equivalente entre recorrido y consulta, sugerencias y teclado ES/EN, conservación
del rack, respuestas tardías y retorno a pantallas agrupadas. La suite de
agrupación/orientación también pasó (uno, dos y tres racks, ambos temas).

Los tres handlers se comprobaron también mediante GET contra la instancia local
ya abierta en el puerto 5142. El usuario confirmó que recompilar resolvió la
falta de estilos de esa instancia. La validación automatizada de interfaz
corresponde a los fixtures de la suite de navegador.
