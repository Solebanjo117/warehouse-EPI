// Rendered Razor fixtures from WipDocumentRouteTests; no operational application or database.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const cliRequire = createRequire(fs.realpathSync(path.join(root, 'tools/playwright/node_modules/@playwright/cli/package.json')));
const { chromium } = cliRequire('playwright');
const fixtures = path.join(root, 'artifacts/ui/wip-document');
const assets = path.join(root, 'src/WarehouseEPI.Web/wwwroot');
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png' };
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const base = url.pathname.startsWith('/fixture/') ? fixtures : assets;
  const relative = url.pathname.replace(/^\/fixture\//, '').replace(/^\//, '');
  let file = path.resolve(base, relative);
  if (!fs.existsSync(file)) file = file.replace(/\.[a-z0-9]{10}(\.[a-z0-9]+)$/i, "$1");
  if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
  res.setHeader('Content-Type', mime[path.extname(file)] || 'application/octet-stream');
  res.end(fs.readFileSync(file));
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let screenshots = 0;
  try {
    for (const language of ['es', 'en']) {
      for (const width of [899, 900, 1199, 1200]) {
        const page = await browser.newPage({ viewport: { width, height: 1000 } });
        for (const name of ['map', 'document', 'report']) {
          await page.goto(`http://127.0.0.1:${server.address().port}/fixture/${name}-${language}.html`);
          if (name === 'map') {
            await page.locator('g[data-map-open]').first().click();
            await page.locator('button[data-map-position]').first().click();
            const detail = page.locator('[data-position-detail]:visible');
            assert.match(await detail.innerText(), language === 'es' ? /Sin control de existencias/ : /No inventory tracking/);
            assert.doesNotMatch(await detail.innerText(), language === 'es' ? /Con saldo|asignación\(es\)/ : /With stock|assignment\(s\)/);
          }
          for (const theme of ['light', 'dark']) {
            await page.locator(`[data-theme-choice="${theme}"]`).first().evaluate(button => button.click());
            assert.equal(await page.locator('html').getAttribute('data-bs-theme'), theme);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, `${name}/${language}/${width} overflows horizontally`);
            await page.screenshot({ path: path.join(fixtures, `${name}-${language}-${width}-${theme}.png`), fullPage: true });
            screenshots++;
          }
        }
        await page.close();
      }
    }
    console.log(`Verified ${screenshots} fixture views: Spanish/English, light/dark, widths 899/900/1199/1200.`);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
