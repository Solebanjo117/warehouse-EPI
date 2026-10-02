---
name: playwright-cli
description: Revisar Warehouse EPI en un navegador aislado con Playwright CLI, obtener capturas y comprobar tamaños, temas, teclado y recuperación de captura. Usar para validación visual o exploración de flujos en navegador de este repositorio; no confundir viewport emulado con validación física.
metadata:
  author: microsoft
  cli-version: "0.1.21"
  upstream-commit: "74354ecc7a43da16d91a9bc54fa8db8283a3fcf5"
---

# Playwright CLI para Warehouse EPI

La dependencia de desarrollo está fijada en `tools/playwright/package.json` y `pnpm-lock.yaml`. Ejecuta desde la raíz mediante `pwsh ./scripts/playwright.ps1`; el wrapper invoca Node directamente, conserva argumentos con espacios o `&`, y funciona sin instalación global ni descargas implícitas con `npx`.

Lee el [contrato visual](../warehouse-epi-ui/references/visual-contract.md) y la [verificación de UI](../warehouse-epi-ui/references/verification.md) cuando revises una pantalla. Para auditoría de código, complementa con [web-design-guidelines](../web-design-guidelines/SKILL.md).

## Entorno

- Usa una instancia que el usuario haya abierto o autorizado abrir, o fixtures locales. No arranques, detengas ni reemplaces el servicio Windows como parte de una revisión visual.
- La configuración `.playwright/cli.config.json` usa Edge, perfil aislado, modo headless y salida en `artifacts/ui/playwright`. En otra máquina, instala Edge o cambia explícitamente el canal disponible. `--headed` solo corresponde si el usuario solicita ver el navegador.
- Usa una sesión con nombre, por ejemplo `-s=epi-ui`, e inspecciona un snapshot antes de elegir referencias. Después de navegar o cambiar la pantalla, vuelve a inspeccionar; no inventes referencias `eN`.
- En una instancia con datos reales, una revisión visual se limita a lectura salvo autorización para la operación concreta. Usa un entorno de pruebas o fixtures para confirmar capturas, ajustes y movimientos.
- Evita registrar NIP, cookies o secretos. No generes trazas, vídeos o capturas con esos datos visibles ni guardes estados de autenticación por defecto.

## Uso

```powershell
pwsh ./scripts/playwright.ps1 --help
# Abrir la URL local autorizada; copiarla de la instancia actual, no asumir puerto.
pwsh ./scripts/playwright.ps1 -s=epi-ui open 'URL_LOCAL_AUTORIZADA'
pwsh ./scripts/playwright.ps1 -s=epi-ui snapshot
pwsh ./scripts/playwright.ps1 -s=epi-ui resize 768 1024
pwsh ./scripts/playwright.ps1 -s=epi-ui press Tab
pwsh ./scripts/playwright.ps1 -s=epi-ui press Enter
pwsh ./scripts/playwright.ps1 -s=epi-ui screenshot --filename=artifacts/ui/playwright/tablet.png
pwsh ./scripts/playwright.ps1 -s=epi-ui close
```

Comprueba anchos a ambos lados de 900 y 1200 px, Claro/Oscuro/Sistema mediante los controles reales del tema, zoom, foco, drawer/modal, textos largos, errores y scroll. Emular el color del sistema solo verifica el modo Sistema; no prueba por sí mismo los temas elegidos dentro de la aplicación. Usa los mismos códigos y fixtures en capturas comparables.

Consulta `--help` o la [referencia oficial instalada](upstream-skill.md) para comandos adicionales. En sus ejemplos, reemplaza `playwright-cli` por `pwsh ./scripts/playwright.ps1`. Las instrucciones de instalación global y los ejemplos de publicación no forman parte de esta configuración local.

Lee solo la referencia necesaria para la tarea:

- [Código Playwright](references/running-code.md), [mocking de red](references/request-mocking.md) y [atributos de elementos](references/element-attributes.md).
- [Sesiones](references/session-management.md) y [almacenamiento](references/storage-state.md).
- [Pruebas Playwright](references/playwright-tests.md) y [generación de pruebas](references/test-generation.md) si la tarea pide automatización repetible.
- [Trazas](references/tracing.md) y [vídeo](references/video-recording.md) cuando sean necesarios y no capturen secretos.

La suite opcional `tests/javascript/production-balance-workspace.browser.cjs` usa la API de Playwright y fixtures HTTP; no es un test de la CLI ni valida la aplicación real. Conserva ambas formas de verificación según el objetivo. Informa qué se comprobó en navegador y qué queda pendiente en tablet, HID, cámara o impresora físicos.

## Procedencia

Adaptación de [la skill oficial de Microsoft](https://github.com/microsoft/playwright-cli/tree/74354ecc7a43da16d91a9bc54fa8db8283a3fcf5/skills/playwright-cli), instalada el 28 de septiembre de 2026. Conserva las referencias oficiales y la skill original. Para actualizar, revisa la nueva versión, las referencias y el lockfile; no reemplaces automáticamente las adaptaciones locales.
