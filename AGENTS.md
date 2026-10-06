# Reutilización de componentes de interfaz

Antes de crear o modificar una interfaz, consulta
[docs/UI_COMPONENTS.md](docs/UI_COMPONENTS.md) y la implementación indicada.
Reutiliza los componentes y patrones existentes cuando cubran el caso de uso,
sin esperar que el usuario lo pida explícitamente. En particular, al seleccionar
un producto, considera primero el buscador con lista de sugerencias existente.

Conserva sus contratos de datos, permisos, localización y captura por teclado,
lector o cámara que correspondan al flujo. No copies un script completo ni
supongas que un controlador de página es un componente genérico: si hace falta
compartirlo, extrae la parte común dentro del alcance de la tarea. Si no encaja,
explica brevemente la diferencia que justifica otra solución.

Al crear, extraer o cambiar un componente reutilizable, actualiza el catálogo
con su caso de uso, archivos, dependencias y un ejemplo real de integración.

# Binlogs de MSBuild

`Directory.Build.rsp` genera automaticamente un binlog unico en `.binlogs/`
para las invocaciones de MSBuild por linea de comandos; no agregues `-bl` por
costumbre. Si un comando necesita `-bl` explicito, indica tambien `.binlogs/`
en la ruta, por ejemplo `'-bl:.binlogs/{}.binlog'` desde la raiz en PowerShell.
MSBuild creara dos binlogs en ese caso. Un `-bl:{}` sin ruta crea el segundo
binlog en la raiz.
