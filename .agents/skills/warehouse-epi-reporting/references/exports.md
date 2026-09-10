# Exportaciones Excel y CSV

La exportación debe reproducir la misma población y filtros de la pantalla, con una granularidad declarada y un límite verificable.

## Límite y resultado

- El límite normal es 10,000 filas de la granularidad exportada, salvo que el contrato actual de esa pantalla sea más restrictivo.
- Para movimientos, cuenta líneas de detalle cuando cada línea produce una fila.
- Consulta `límite + 1` o calcula el total de forma segura para detectar exceso.
- Si se supera el límite, rechaza el archivo completo con un mensaje que pida filtros más específicos. No truncar silenciosamente.
- No generes un archivo vacío como sustituto de un error.

## Excel con ClosedXML

- Escribe fechas como celdas de fecha, cantidades como números y conteos como enteros.
- Conserva precisión decimal compatible con `numeric(18,4)` y un formato visible como `#,##0.0000` cuando corresponda.
- Escribe códigos, SKU, folios, referencias y texto como texto; no permitas que Excel los interprete como fórmulas.
- Aplica sanitización de formula injection únicamente a campos textuales que comiencen con `=`, `+`, `-`, `@`, tabulación u otro prefijo cubierto por el helper actual.
- No conviertas un número negativo legítimo en texto sanitizado.
- Incluye título, fecha de generación local, zona horaria, total y filtros aplicados cuando el formato actual lo contemple.
- Mantén hoja, encabezados, filtros, inmovilización, anchos y estilos simples y legibles. No introduzcas macros, vínculos externos ni fórmulas necesarias para obtener el dato base.

## CSV

- Produce RFC 4180 y UTF-8 con BOM para compatibilidad operativa con Excel.
- Escapa comas, comillas y saltos de línea correctamente.
- Usa cultura invariante para números y fechas de contrato; no dejes que la cultura del proceso cambie el archivo.
- Aplica la misma defensa de formula injection a campos textuales.
- Conserva números negativos como números y no entrecomilles valores numéricos solo para neutralizarlos.
- Mantén metadatos de zona horaria y filtros según el contrato actual sin cambiar el significado de las columnas.

## Seguridad y privacidad

- Repite la autorización de la ruta; no confíes en que el botón estaba oculto.
- No exportes NIP, hashes, secretos, detalles internos de excepciones ni datos fuera de la población autorizada.
- Usa nombres de archivo seguros, estables y con fecha cuando ayuden a identificar el reporte.
- No escribas archivos temporales persistentes si el contenido puede generarse en memoria dentro de límites razonables.

## Fidelidad

Abrir el XLSX con ClosedXML confirma estructura y tipos, no fidelidad visual de impresión. Si el resultado se imprimirá, renderiza o prueba el formato con la herramienta especializada y deja pendiente la impresora física hasta validarla.
