# Auditoría del flujo del programa diario de producción

Fecha: 21 de septiembre de 2026. Revisión de la base Git `b6c32a6` y del trabajo local presente en Warehouse EPI. El hash identifica la base del repositorio; los archivos nuevos o modificados del módulo diario pueden no estar incluidos todavía en esa revisión Git.

## 1. Dictamen

La estructura funcional debe conservarse: **Programa semanal → Captura diaria → Balance**, con **Surtimientos** y **Producción avanzada** como salidas para materiales y excepciones. No se recomienda reemplazar nuevamente el módulo.

El siguiente incremento debe concentrarse en cuatro puntos:

1. Confirmar exactamente el reparto FIFO que el operador revisó.
2. Hacer coherentes los filtros y significados del balance.
3. Conservar fecha, área y turno entre capturas consecutivas.
4. Separar claramente producción del día, acumulado y pendiente al cierre.

Estas mejoras reducen riesgo operativo y pasos repetidos sin cambiar el motor existente de órdenes, lotes, recetas, materiales, inventario, NIP, reversos o auditoría.

## 2. Alcance y nivel de evidencia

La auditoría cubre:

- configuración inicial de áreas y turnos;
- creación, edición, publicación, cierre y reapertura de semanas;
- búsqueda y selección de SKU;
- revisión y confirmación de captura diaria;
- reparto FIFO y consumo automático;
- balance diario y semanal;
- filtros, detalle de capturas y exportación;
- importación inicial de Excel;
- navegación hacia Surtimientos y Producción avanzada;
- operación con teclado, HID, tablet y localización ES/EN.

Niveles usados:

- **C — Código:** comprobado mediante el recorrido Razor → PageModel → servicio.
- **A — Automatizado existente:** existe cobertura focal en las pruebas actuales, pero esta auditoría no vuelve a certificar toda la aplicación.
- **V — Validación pendiente:** necesita observación en navegador, tablet o con operadores.

Esta entrega es documental. No cambia el comportamiento del sistema, no aplica migraciones, no importa el Excel y no despliega una versión.

## 3. Responsables y recorridos actuales

| Persona o función | Recorrido esperado | Necesidad principal |
|---|---|---|
| ADMIN / programación | Crear borrador → agregar líneas → revisar → publicar | Preparar una semana sin crear órdenes manualmente. |
| Producción | Seleccionar contexto → SKU → cantidad → revisar reparto → NIP | Registrar piezas buenas rápido y sin asignarlas a una orden incorrecta. |
| Responsable de producción | Filtrar balance → identificar pendiente → abrir detalle o tarea | Decidir qué debe fabricar el siguiente turno. |
| Bodega | Abrir Surtimientos → preparar y entregar materiales | Resolver faltantes sin salir del contexto de la orden. |
| ADMIN / excepción | Abrir Producción avanzada | Revertir, conciliar, registrar merma/retrabajo o corregir movimientos. |

## 4. Elementos que deben conservarse

- Una sola fuente de verdad para programa, avance e inventario.
- Reparto FIFO entre líneas y órdenes pendientes.
- Confirmación con NIP por operación.
- Identificadores idempotentes para evitar duplicados por reintento.
- Transacciones completas: ninguna asignación parcial si falla una parte.
- Capturas inmutables y reverso ADMIN con motivo.
- Consumo proporcional y trazabilidad por lote, orden y proceso.
- Ready to Pack sin entrada automática a inventario terminado.
- Recepción posterior a bodega como operación separada.
- `ProductId` como identidad del SKU; nunca texto libre persistido.
- Punto decimal, hasta cuatro decimales y cultura operativa sin cambios.
- Datos humanos sin traducir: SKU, catálogo, referencias, notas e historial.
- Flujo avanzado para merma, retrabajo, diferencias y diagnósticos.
- Razor Pages, Bootstrap, JavaScript local, antiforgery y permisos de servidor.

## 5. Hallazgos prioritarios

Prioridad **P1**: antes o durante el primer piloto operativo. **P2**: mejora directa de continuidad y claridad. **P3**: refinamiento posterior.

### PD-01 · P1 · La confirmación no queda vinculada al reparto revisado [C]

**Evidencia:** `ProductionDailyCaptureService.ConfirmAsync` vuelve a ejecutar `PreviewAsync`. El comando de confirmación incluye fecha, área, turno, producto, cantidad, notas y NIP, pero no una versión o huella de la previsualización. La vista mantiene editables los campos después de mostrar el reparto.

**Riesgo:** la asignación confirmada puede ser distinta de la mostrada si el usuario cambia un campo, otra estación consume disponibilidad o una orden cambia entre revisar y confirmar. El servidor protege consistencia e idempotencia, pero no demuestra que el operador haya aceptado el reparto nuevo.

**Propuesta:** devolver una huella de revisión basada en configuración, semana, órdenes, líneas, cantidades y versiones. La confirmación debe comparar esa huella. Si cambió:

- no registrar producción;
- regenerar la previsualización;
- explicar qué cambió;
- exigir una nueva confirmación.

Cambiar fecha, área, turno, SKU o cantidad en el navegador debe invalidar inmediatamente la revisión visible. Notas puede permanecer fuera de la huella si se define así explícitamente.

**Aceptación:** una confirmación registra exactamente las mismas asignaciones y cantidades mostradas. Un cambio concurrente nunca se confirma silenciosamente.

### PD-02 · P1 · Los filtros no representan el mismo conjunto en matriz y detalle [C]

**Evidencia:** el filtro de turno se envía a `GetCaptureDetailsAsync`, pero no modifica las filas de `Balance`. Referencia filtra la matriz, no el detalle. La búsqueda de la matriz acepta SKU o descripción; el detalle sólo comprueba SKU. Exportar descarga por semana y no por el conjunto filtrado visible.

**Riesgo:** la pantalla parece presentar una sola consulta aunque matriz, detalle y archivo pueden contener universos diferentes.

**Propuesta:** definir explícitamente dos capas:

- **Balance total:** plan, arrastre, pendiente y acumulado de toda la semana.
- **Producción filtrada:** cantidades del día/turno/área y capturas que explican ese subconjunto.

Si un filtro no puede modificar el pendiente sin desvirtuarlo, debe indicarse como «Pendiente total» y no aparentar que pertenece sólo al turno. La exportación debe decir «Exportar semana completa» o recibir exactamente los mismos filtros.

**Aceptación:** matriz, métricas, detalle y exportación declaran el mismo alcance o explican claramente la diferencia.

### PD-03 · P1 · Se pierde el contexto entre capturas consecutivas [C,V]

**Evidencia:** después de confirmar, la página redirige conservando únicamente `WeekId`. En una carga nueva se inicializan fecha actual y primer turno configurado; área usa su valor inicial. SKU, cantidad, notas y NIP también se reinician.

**Efecto:** en Costura/T2, cada SKU siguiente puede requerir volver a escoger área y turno. El reinicio también aumenta el riesgo de confirmar la próxima captura en Cutting/T1 por descuido.

**Propuesta:** mantener un «contexto de estación» visible con:

- fecha efectiva;
- área;
- turno.

Tras éxito, limpiar producto, cantidad, notas y NIP, generar nueva operación idempotente y devolver el foco al buscador SKU. El usuario debe poder cambiar el contexto con una acción clara.

**Aceptación:** registrar diez SKU consecutivos en la misma área/turno sólo requiere seleccionar producto, cantidad, revisar y confirmar cada uno. El NIP nunca se repuebla.

### PD-04 · P1 · Las filas diarias muestran cantidades acumuladas sin decirlo [C]

**Evidencia:** `ProductionDailyBalanceService` suma capturas con `EffectiveDate <= day` y presenta el resultado dentro de una fila rotulada con ese día.

**Riesgo:** si el lunes se hicieron 50 y el martes 30, la fila del martes puede mostrar 80 como «hecho». Un lector puede interpretar que el martes por sí solo produjo 80.

**Propuesta:** mostrar tres valores diferenciados:

- producido del día;
- acumulado hasta el día;
- pendiente al cierre del día.

En tablet, priorizar pendiente y producido del día; el acumulado puede quedar como contexto secundario. En la matriz semanal, encabezados y ayudas deben usar esos nombres completos.

**Aceptación:** el ejemplo 50 + 30 muestra martes = 30 del día y 80 acumulado; nunca presenta 80 como producción exclusiva del martes.

### PD-05 · P1 · El NIP se exige antes de saber si la captura puede confirmarse [C]

**Evidencia:** `OnPostPreviewAsync` valida todo `CaptureInput`, cuyo NIP es obligatorio. Aunque la previsualización no persiste producción, el operador debe introducir NIP antes de descubrir falta de materiales, fecha cerrada o cantidad no disponible.

**Propuesta:** dividir el formulario lógico:

1. fecha, área, turno, SKU y piezas;
2. revisar reparto y bloqueos;
3. introducir NIP y confirmar.

El NIP sólo debe viajar en la confirmación. Una previsualización nunca debe autenticar ni conservar el NIP.

**Aceptación:** revisar un reparto no solicita NIP. Confirmar siempre lo solicita y lo limpia después de éxito o error.

### PD-06 · P1 · Los bloqueos informan, pero no conducen a la solución [C,V]

**Evidencia:** los bloqueos se presentan como párrafos de texto. Mensajes sobre materiales, orden o configuración no incluyen una acción contextual.

**Propuesta:** convertir los bloqueos conocidos en resultados estructurados con tipo y referencias seguras, por ejemplo:

- falta material → «Abrir Surtimientos»;
- orden no disponible → «Consultar orden»;
- configuración incompleta → «Configurar áreas y turnos» para ADMIN;
- merma/retrabajo → «Abrir Producción avanzada»;
- conflicto → «Revisar reparto actualizado».

No construir enlaces interpretando texto localizado. El servicio debe devolver códigos de problema y la presentación decidir el texto.

**Aceptación:** cada bloqueo recuperable ofrece una acción directa sin perder fecha, área, turno, SKU ni cantidad.

## 6. Mejoras de continuidad y eficiencia

### PD-07 · P2 · Captura y Balance no son entradas realmente distintas [C]

Las tarjetas de navegación envían `View=capture` y `View=balance`, pero el PageModel no usa ese valor. Ambas abren la misma parte superior de la página.

**Mejora:** conservar una sola ruta, con modos o anclas reales:

- Captura: foco en el flujo operativo y balance resumido.
- Balance: filtros y consulta primero; captura plegada o fuera de la vista inicial.

**Aceptación:** abrir Balance desde el menú lleva directamente a la consulta, sin obligar a recorrer el formulario de producción.

### PD-08 · P2 · La pantalla combina operación y consulta extensa [C,V]

Captura, matriz, tarjetas móviles, filtros y hasta 500 detalles comparten una página. Esto favorece continuidad, pero puede producir demasiado scroll en tablet y distraer durante la captura.

**Mejora:** mantener la misma ruta y servicios, pero separar visualmente:

- contexto y captura;
- confirmación/reparto;
- balance resumido;
- consulta detallada bajo demanda.

No convertirlo en una SPA ni añadir dependencias. Se pueden usar anclas, secciones plegables accesibles o modos GET.

### PD-09 · P2 · Falta recuperación visible ante error del buscador [C]

El JavaScript cierra las sugerencias ante una respuesta fallida. Para el usuario, «sin resultados» y «no se pudo consultar» se ven iguales.

**Mejora:** mostrar estados localizados:

- escribe al menos dos caracteres;
- buscando;
- sin coincidencias;
- no se pudo consultar, vuelve a intentar.

Conservar texto capturado y permitir reintento. No inventar un producto ni persistir sin `ProductId`.

### PD-10 · P2 · Programa semanal favorece una línea aislada, no una sesión de programación [C,V]

Al guardar se recarga la página. Una línea nueva vuelve a usar el primer día de la semana. No existe «Guardar y agregar otra» ni «Cancelar edición» visible. Las referencias 1–3 ocupan el mismo peso visual aunque son opcionales.

**Mejora:**

- conservar el último día capturado;
- devolver foco al buscador;
- ofrecer «Guardar y agregar otra»;
- ofrecer «Cancelar edición» al modificar;
- agrupar referencias opcionales;
- mostrar validación junto a fecha, cantidad y referencias;
- mostrar un resumen por día antes de publicar.

**Aceptación:** cargar varias líneas del mismo día no exige volver a seleccionar la fecha y editar una línea nunca se confunde con agregar otra.

### PD-11 · P2 · La configuración permanente compite con la programación [C,V]

La configuración de tres procesos y dos turnos permanece visible al lado del programa aun después de completarse.

**Mejora:** cuando esté válida, mostrar un resumen compacto «Configuración lista» con asociaciones y acción «Cambiar». Expandir automáticamente sólo si está incompleta, inactiva o ambigua.

**Aceptación:** el ADMIN ve de inmediato si puede publicar, pero los controles de configuración no desplazan la tabla semanal durante el trabajo normal.

### PD-12 · P2 · Falta resumen previo a publicar [C,V]

La publicación valida todo atómicamente, pero la interfaz no resume cuántas líneas, unidades, SKU y días se crearán ni presenta los bloqueos antes del NIP.

**Mejora:** acción «Revisar publicación» que muestre:

- rango y estado;
- líneas y SKU;
- cantidades por día;
- rutas/recetas/destinos listos o bloqueados;
- órdenes que se crearán;
- advertencia sobre el efecto transaccional.

Después de una revisión válida, solicitar NIP ADMIN para publicar.

### PD-13 · P2 · El detalle tiene un límite silencioso [C]

`GetCaptureDetailsAsync` devuelve hasta 500 capturas sin total ni paginador.

**Mejora:** paginación de servidor, total y orden visible. En la operación diaria se puede mostrar sólo lo reciente; el historial completo debe poder consultarse sin que 500 parezca «todo».

### PD-14 · P2 · Reverso ADMIN está incrustado en cada fila [C,V]

El formulario de reverso aparece dentro de un `<details>` de la celda de estado. Es válido, pero en tablet puede ser estrecho y la gravedad de la acción compite con la lectura.

**Mejora:** abrir una sección de reverso contextual con resumen de fecha, área, turno, SKU, cantidad y órdenes; solicitar motivo y NIP; confirmar explícitamente que se revertirán resultados, consumos y entregas relacionadas.

## 7. Refinamientos posteriores

### PD-15 · P3 · Métricas rápidas para decidir el siguiente turno

Agregar indicadores basados en la consulta real:

- pendiente de Cutting;
- pendiente de Sewing;
- pendiente de Ready to Pack;
- producción del turno seleccionado;
- SKU bloqueadas por material.

Cada indicador debe enlazar al conjunto que lo explica. Diferenciar cero, sin datos y error.

### PD-16 · P3 · Acciones rápidas desde el balance

Desde una fila, permitir preparar una nueva captura conservando día, área y SKU, sin registrar automáticamente. Para pendientes de materiales, abrir Surtimientos. Para excepciones, abrir el flujo avanzado.

### PD-17 · P3 · Preferencias de estación

Sólo después del piloto, evaluar recordar área y turno por navegador o estación. Debe ser una preferencia visible y corregible, no una selección oculta. Fecha futura o domingo nunca deben heredarse como válidos.

## 8. Flujo objetivo

### 8.1 Programación ADMIN

1. Elegir o crear semana.
2. Ver estado de configuración; expandir sólo si requiere atención.
3. Agregar líneas consecutivamente conservando el día.
4. Revisar resumen semanal y bloqueos.
5. Introducir NIP ADMIN.
6. Publicar en una sola transacción.
7. Ver confirmación con enlaces a Captura y Surtimientos.

### 8.2 Captura de producción

1. Establecer contexto de estación: fecha, área y turno.
2. Buscar y seleccionar SKU.
3. Introducir piezas buenas y notas opcionales.
4. Revisar reparto FIFO y sus efectos.
5. Si hay bloqueo, abrir la tarea correspondiente sin perder captura.
6. Introducir NIP.
7. Confirmar la huella revisada.
8. Ver confirmación y balance actualizado.
9. Capturar el siguiente SKU conservando fecha, área y turno.

### 8.3 Consulta y planeación del siguiente turno

1. Abrir Balance desde su entrada directa.
2. Seleccionar semana, día, área o turno.
3. Ver producido del día, acumulado y pendiente al cierre.
4. Abrir las capturas que forman el total.
5. Ir a Captura, Surtimientos u operación avanzada según el problema.

## 9. Secuencia de implementación recomendada

| Bloque | Hallazgos | Resultado esperado |
|---|---|---|
| A. Integridad de revisión | PD-01, PD-05, PD-06 | Reparto aceptado explícitamente, NIP al final y bloqueos accionables. |
| B. Coherencia del balance | PD-02, PD-04, PD-13 | Alcance de filtros claro, día/acumulado separados e historial paginado. |
| C. Velocidad de captura | PD-03, PD-07, PD-08, PD-09 | Contexto persistente, entrada directa y recuperación del buscador. |
| D. Programación ADMIN | PD-10, PD-11, PD-12 | Captura consecutiva, configuración compacta y publicación revisable. |
| E. Refinamiento/piloto | PD-14 a PD-17 | Consulta accionable y ajustes basados en operación real. |

El bloque A debe ejecutarse primero porque define qué acepta el operador. B y C pueden planearse juntos, pero deben probarse contra los mismos datos. D no debe retrasar la corrección de captura. E depende de evidencia del piloto.

## 10. Matriz mínima de aceptación

### Programa

- Semana vacía, actual, abierta anterior, cerrada e inexistente.
- Varias líneas del mismo día y de la misma SKU.
- Edición y cancelación sin convertir una línea en otra.
- Configuración completa, parcial, ambigua e inactiva.
- Publicación con todos los requisitos y con un bloqueo en la última línea.

### Captura

- T1/T2, las tres áreas y rutas con procesos omitidos.
- Una SKU, varias líneas y reparto entre varias órdenes.
- Cambio concurrente después de revisar y antes de confirmar.
- Falta de materiales, orden no disponible y cantidad superior a la disponible.
- Reintento por respuesta de red incierta sin duplicar.
- NIP incorrecto conservando datos no sensibles.
- Registro consecutivo conservando contexto y limpiando NIP.
- Teclado, Enter, HID, táctil y foco progresivo.

### Balance

- Diferencia demostrable entre día y acumulado.
- Filtros de semana, día, SKU/descripción, área, turno y referencia.
- Matriz, detalle y exportación con alcance declarado.
- Arrastre, adelanto, proceso no aplicable y pendiente cero.
- Captura revertida excluida inmediatamente.
- Más de 500 capturas con paginación/total.

### Presentación

- Español e inglés sin traducir datos de catálogo o usuario.
- Tema claro y oscuro.
- Escritorio, tablet horizontal y tablet vertical.
- Objetivos táctiles de al menos 44 px.
- Errores visibles y anunciados; foco en la acción que requiere corrección.
- Pérdida de red distinguida de «sin resultados».

## 11. Indicadores para el piloto

No se fija una meta sin línea base. Durante una semana piloto se deben medir:

- tiempo desde abrir Captura hasta registrar una SKU;
- número de cambios manuales de fecha/área/turno por turno;
- capturas bloqueadas por materiales, configuración o disponibilidad;
- repartos que cambiaron después de la primera revisión;
- reintentos por conexión y duplicados evitados;
- correcciones/reversos por área o turno equivocado;
- tiempo para localizar qué falta producir;
- discrepancias entre balance del sistema y muestra controlada del Excel.

Registrar observaciones por escritorio/tablet y por T1/T2. No usar únicamente opinión general; anotar la tarea, duración, error y recuperación.

## 12. Límites y riesgos de la recomendación

- Persistir el contexto de estación no debe conservar NIP ni una fecha inválida.
- Una huella de previsualización no sustituye la transacción serializable ni la idempotencia.
- Filtrar por turno no debe convertir el pendiente global en un pendiente ficticio del turno.
- Los accesos directos a Surtimientos o Producción avanzada deben conservar autorización y antiforgery.
- La simplificación visual no puede eliminar trazabilidad, motivo de reverso, referencias ni auditoría.
- Un viewport simulado no demuestra operación física con guantes, HID o Wi-Fi de planta.

## 13. Estado de esta auditoría

- **Auditado en código:** sí.
- **Cambios funcionales aplicados por esta auditoría:** ninguno.
- **Pruebas nuevas ejecutadas para esta auditoría:** ninguna; se inventariaron las pruebas focales existentes.
- **Validación visual en navegador:** pendiente.
- **Validación física en tablet/HID:** pendiente.
- **Migración generada o aplicada:** no.
- **Importación ejecutada:** no.
- **Despliegue realizado:** no.

## 14. Archivos principales revisados

- `src/WarehouseEPI.Web/Pages/Admin/Production/Schedule.cshtml(.cs)`
- `src/WarehouseEPI.Web/Pages/Admin/Production/ScheduleImport.cshtml(.cs)`
- `src/WarehouseEPI.Web/Pages/Operations/Production/Index.cshtml(.cs)`
- `src/WarehouseEPI.Web/wwwroot/js/production-daily.js`
- `src/WarehouseEPI.Web/wwwroot/css/production-daily.css`
- `src/WarehouseEPI.Web/Navigation/ModuleNavigation.cs`
- `src/WarehouseEPI.Infrastructure/Production/ProductionDailyCaptureService.cs`
- `src/WarehouseEPI.Infrastructure/Production/ProductionDailyBalanceService.cs`
- `src/WarehouseEPI.Infrastructure/Production/ProductionDailyScheduleService.cs`
- `src/WarehouseEPI.Infrastructure/Production/ProductionDailyContracts.cs`
- `tests/WarehouseEPI.Tests/Production/ProductionDailyModuleTests.cs`
- `tests/WarehouseEPI.Tests/Web/ProductionDailyRouteTests.cs`
- `tests/WarehouseEPI.Tests/Web/ProductionDailySetupTests.cs`
- `tests/WarehouseEPI.Tests/Web/ProductionDailyUxContractTests.cs`
- `tests/javascript/production-daily.test.cjs`

