# Binlogs de MSBuild

`Directory.Build.rsp` genera automaticamente un binlog unico en `.binlogs/`
para las invocaciones de MSBuild por linea de comandos; no agregues `-bl` por
costumbre. Si un comando necesita `-bl` explicito, indica tambien `.binlogs/`
en la ruta, por ejemplo `'-bl:.binlogs/{}.binlog'` desde la raiz en PowerShell.
MSBuild creara dos binlogs en ese caso. Un `-bl:{}` sin ruta crea el segundo
binlog en la raiz.
