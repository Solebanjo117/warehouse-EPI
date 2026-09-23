# Interfaz bilingüe de Warehouse EPI

## Alcance

La interfaz comparte las mismas páginas Razor, rutas, datos y permisos. El selector
ES / EN del menú guarda la preferencia por navegador en `WarehouseEPI.Language`.
Español es el idioma inicial. La preferencia requiere POST con antiforgery y solo
permite redirecciones locales. No necesita usuario ni cambios de base de datos.

`UiLanguage` configura únicamente la cultura de interfaz elegida. La cultura
operativa existente se conserva para no cambiar la captura de decimales, los
formatos actuales ni el model binding. No se usa el idioma del navegador como
cambio implícito de las reglas operativas.

## Recursos

- `Localization/SharedTexts`: menú, inicio, módulos, componentes compartidos.
- `Localization/OperationsTexts`: movimientos, recepciones, conteos y etiquetas.
- `Localization/ProductionTexts`: operación, surtimiento y administración de producción.
- `Localization/CatalogTexts`: catálogos, administración, ubicaciones y reportes.

Los archivos están en `src/WarehouseEPI.Web/Resources/Localization`.
`*.resx` contiene el texto español de respaldo y `*.en.resx`, el inglés.
Las vistas inyectan `IStringLocalizer<T>` del módulo. Las claves conservan en
su mayoría la frase española para facilitar la revisión y la adopción gradual.

Para incorporar un texto:

1. Añadir la misma clave en ambos recursos del módulo.
2. Reemplazar únicamente su presentación en Razor mediante el localizador.
3. Para frases con datos, usar `{0}`, `{1}`, etc., en ambos idiomas; no traducir
   valores guardados ni construir claves con datos del usuario.
4. Usar `.Value` cuando se necesite un `string`, por ejemplo en `ViewData`.
5. Mantener codificados los resultados de Razor; no usar `Html.Raw` para traducciones.
6. Evitar claves que solo difieran en mayúsculas/minúsculas: ResGen las considera
   duplicadas. Usar una clave distinta y conservar el texto correspondiente en su valor.

Los textos de los scripts compartidos viajan como JSON codificado en un atributo
`data-ui-texts` del layout y se leen desde `site.js`. No se agrega JavaScript inline,
CDN, servicio externo ni paquete nuevo. Cada script de pantalla deberá recibir
sus propios textos cuando se convierta.

## Fronteras de esta conversión

Esta etapa convierte textos de presentación en vistas Razor y scripts compartidos.
No equivale a una versión inglesa integral: mensajes de validación de PageModels,
servicios, respuestas JSON, scripts específicos y documentos/exportaciones generados
en backend pueden seguir en español. Los nombres de productos, descripciones,
observaciones e historial capturado se conservan. Esos frentes requieren inventario
separado y traducción en su frontera de presentación, sin modificar códigos de dominio.

Antes de publicar se requiere revisión operativa del vocabulario inglés y validación
visual de ambas lenguas, especialmente tablas, impresión, tablet, HID y cámara.
No se inicia ni se publica la aplicación como parte de esta conversión.

## Verificación

Las pruebas `LocalizationTests` cubren selección del idioma, conservación de cultura
operativa, respaldo español, resolución de recursos, paridad de claves/argumentos y
rechazo de idiomas y redirecciones no admitidas. `LocalizationRouteTests` comprueba
el renderizado y antiforgery mediante el host de pruebas, no mediante la instalación
operativa. Los resultados de ejecución se registran en `docs/CONTEXT.md`.

## Vocabulario compartido

| Español | Inglés |
| --- | --- |
| Entrada (movimiento) | Receipt |
| Salida (movimiento) | Issue |
| Transferencia | Transfer |
| Ubicación | Location |
| Existencias | Stock |
| Saldo | Balance |
| Lote | Lot |
| Orden de trabajo | Work order |
| NIP | PIN |
| Merma | Scrap |
| Retrabajo | Rework |

Las frases más largas pueden adaptar estos términos a su contexto; los nombres
visibles del mismo movimiento deben coincidir entre captura y reportes.

## Producción diaria

Captura, Balance, Programa e Importación usan `ProductionTexts`; las tarjetas del
menú usan `SharedTexts`. `ProductionDailyText` adapta los mensajes del módulo en
la frontera web: reconoce claves estáticas y plantillas con argumentos, sin usar
SKU, números de orden ni frases interpoladas como claves. Los mensajes heredados
del motor avanzado no cubiertos por esta corrección conservan su texto original.
No se traducen catálogos, referencias, notas ni el archivo Excel.

El buscador recibe `data-no-description` codificado por Razor. Los días de semana
usan `CurrentUICulture`, mientras fechas HTML y captura de cantidades conservan
el contrato operativo (ISO en el envío de fechas, punto decimal y `ProductId`).
La configuración propuesta no se persiste en un GET; ADMIN debe guardarla.
