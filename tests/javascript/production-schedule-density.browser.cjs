// Isolated browser over renewed Razor HTTP fixtures. No operational service or database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname,'../..');
const packages = path.join(root,'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages,installed,'node_modules/playwright'));
const before = process.argv.includes('--before');
const measureOnly = process.argv.includes('--measure-only');
const flowsOnly = process.argv.includes('--flows-only');
const detailsOnly = process.argv.includes('--details-only');
const captureOnly = process.argv.includes('--capture-only');
const evidence = path.join(root,'artifacts/tablet-tanda3');
const fixtures = process.env.WAREHOUSE_TABLET_UI_FIXTURES || path.join(evidence,before ? 'before/fixtures' : 'fixtures');
const output = process.env.WAREHOUSE_TABLET_UI_OUTPUT || path.join(evidence,before ? 'before/browser' : 'browser');
const widths = [360,768,899,900,991,992,1199,1200,1280];
const screens = ['program-draft','program-open','program-closed','products','products-open','products-closed',
  'summary-draft','summary-open','summary-closed','review','review-blocked','empty','summary-units','review-units','summary-empty'];
const targets=detailsOnly ? ['products','summary-units'] : screens;
fs.mkdirSync(output,{recursive:true});
(async () => {
 const browser = await chromium.launch({channel:'msedge',headless:true});
 const context = await browser.newContext({viewport:{width:1280,height:900},hasTouch:true});
 const page = await context.newPage();
 const errors = [], results = [], geometry = [];
 let screen='program-draft', language='es', ratio, shortSku=false, notApplicable=false, writes=0;
 page.on('pageerror',error => errors.push(error.message));
 page.on('dialog',dialog => dialog.accept());
 await context.route('**/*',async route => {
  const request = route.request(), url = new URL(request.url());
  if(url.hostname !== 'density.test') return route.abort();
  if(request.method() !== 'GET') { writes++; return route.fulfill({status:405}); }
  const handler = url.searchParams.get('handler');
  if(handler === 'WorkspaceOpenings') return route.fulfill({json:[]});
  if(handler === 'WorkspaceOperation') return route.fulfill({json:{saved:false}});
  if(handler === 'WorkspaceProducts') return route.fulfill({json:[{id:'00000000-0000-0000-0000-000000000099',sku:'NEW-SKU',unit:'EA',allowsDecimals:true}]});
  if(url.pathname === '/Admin/Production/Schedule') {
   let body = fs.readFileSync(path.join(fixtures,`${screen}-${language}.html`),'utf8');
   if(ratio !== undefined) body = body.replace(/(&quot;ratio&quot;:)(?:[\d.]+|null)/g,`$1${ratio}`);
   if(shortSku) body = body.replaceAll('TABLET-LONG-SKU-ABCDEFGHIJKLMNOPQRSTUVWXYZ-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ','SHORT-SKU');
   if(notApplicable) body = body.replace(/data-progress="([^"]+)"/g, (_,encoded) => {
    const product=JSON.parse(encoded.replaceAll('&quot;','"').replaceAll('&amp;','&'));
    product.days.forEach(day => { day.areas[1].applies=false; });
    return `data-progress="${JSON.stringify(product).replaceAll('&','&amp;').replaceAll('"','&quot;')}"`;
   });
   return route.fulfill({body,contentType:'text/html'});
  }
  const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/,'');
  const relative = path.join('src/WarehouseEPI.Web/wwwroot',`.${asset}`);
  const saved = process.env.WAREHOUSE_SCHEDULE_ASSET_BASELINE
    ? path.join(process.env.WAREHOUSE_SCHEDULE_ASSET_BASELINE, path.basename(asset)) : path.join(evidence,'before',relative), current = path.join(root,relative);
  const file = before && fs.existsSync(saved) ? saved : current;
  if(file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({path:file});
  return route.fulfill({status:204});
 });
 const frames = () => page.evaluate(async () => { await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); });
 const open = async (name,lang,width,theme='light',height) => {
  screen=name; language=lang;
  await page.setViewportSize({width,height:height || (width===360 ? 800 : width===1280 ? 900 : 1024)});
  await page.goto('http://density.test/Admin/Production/Schedule');
  await page.locator('.production-schedule').waitFor();
  await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
  await frames();
 };
 const overflow = async label => assert.ok(await page.evaluate(() => Math.max(document.body.scrollWidth,document.documentElement.scrollWidth) <= innerWidth + 1),`Page overflow: ${label}`);
 const contrast = async () => {
  const values = await page.locator('.production-schedule-progress__metric, .production-schedule-progress__produced').evaluateAll(nodes => nodes.filter(el => el.getClientRects().length).map(el => {
   const rgba = value => value.match(/[\d.]+/g).map(Number);
   const fg = rgba(getComputedStyle(el).color);
   let current = el, bg;
   while(current) { const color=rgba(getComputedStyle(current).backgroundColor); if(color.length===3 || color[3]===1) { bg=color; break; } current=current.parentElement; }
   const luminance = color => color.slice(0,3).map(x => { x/=255; return x<=.04045 ? x/12.92 : ((x+.055)/1.055)**2.4; }).reduce((n,x,i) => n+x*[.2126,.7152,.0722][i],0);
   if(!bg) return {text:el.textContent,ratio:0};
   const alpha=fg[3] ?? 1;
   const blended=fg.slice(0,3).map((value,index) => value*alpha+bg[index]*(1-alpha));
   const a=luminance(blended), b=luminance(bg); return {text:el.textContent,ratio:(Math.max(a,b)+.05)/(Math.min(a,b)+.05)};
  }));
  for(const value of values) assert.ok(value.ratio >= 4.5,`Contrast ${value.ratio}: ${value.text}`);
 };
 const touch = async () => {
  const controls = await page.locator('[data-week-workspace] summary, [data-week-workspace] input:not([type="hidden"]), [data-week-workspace] button, .production-schedule-products summary, .production-schedule-products .btn, .production-schedule-summary summary').evaluateAll(nodes => nodes.filter(el => el.getClientRects().length && el.checkVisibility()).map(el => ({text:el.textContent || el.id,height:el.getBoundingClientRect().height})));
  for(const item of controls) assert.ok(item.height >= 44,`Touch ${item.height}px: ${item.text}`);
 };
 const checkProgram = async width => {
  const w=page.locator('[data-week-workspace]');
  assert.equal(await w.locator('[data-schedule-indicators]').count(),7);
  assert.equal(await w.locator('.production-schedule-progress__heading').count(),7);
  assert.equal(await w.locator('.production-schedule-progress__breakdown').count(),7);
  assert.equal(await w.locator('.production-schedule-progress__metric').count(),21);
  assert.equal(await w.locator('.production-schedule-progress__produced').count(),21);
  assert.equal(await w.locator('.production-schedule-progress__breakdown[open]').count(),0);
  assert.equal(await w.locator('[data-schedule-orders] [data-schedule-indicators]').count(),0);
  
  
  const main=w.locator('[data-workspace-rows] > tr').first();
  assert.equal(await main.evaluate(el => getComputedStyle(el).display),width<576 ? 'grid':'table-row');
  assert.ok(await main.locator('.production-schedule-progress__produced').first().isVisible());
  assert.ok(!(await main.innerText()).includes(language==='es' ? 'Cubierto:':'Covered:'));
  const daily=main.locator('[data-workspace-day-cell="0"]');
  assert.equal(await daily.locator('.production-schedule-progress__metric').count(),3);
  if(screen==='program-closed') {
   assert.equal(await w.locator('input.production-week-workspace__quantity').count(),0);
   assert.match(await main.innerText(),language==='es' ? /Ver pedidos \(2\)/ : /View orders \(2\)/);
  } else {
   assert.match(await w.locator('[data-schedule-orders] > summary').textContent(),language==='es' ? /Editar pedidos \(2\)/ : /Edit orders \(2\)/);
   await w.locator('[data-schedule-orders] > summary').tap();
   assert.equal(await w.locator('[data-workspace-rows] > tr').count(),1);
   assert.equal(await w.locator('input.production-week-workspace__quantity').count(),21);
   assert.equal(await w.locator('input.production-week-workspace__quantity:visible').count(),3);
   if(language==='en') assert.match(await w.locator('input.production-week-workspace__quantity').first().getAttribute('aria-label'),/Monday/);
   assert.equal(await w.locator('[data-schedule-disclosure^="row:"]:not([open])').count(),2);
  }
  await contrast(); await touch();
 };
 try {
  if(before && !measureOnly || captureOnly) {
   for(const theme of ['light','dark']) for(const width of [360,768,1280]) for(const name of ['program-draft','program-open','products','summary-draft','review','empty']) {
    await open(name,'es',width,theme);
    await page.screenshot({path:path.join(output,`${name}-es-${theme}-${width}.png`)});
   }
  } else if(!measureOnly && !flowsOnly) {
   for(const theme of ['light','dark','system']) {
    await page.emulateMedia({colorScheme:theme==='system' ? 'dark':'light'});
    for(const width of widths) for(const lang of ['es','en']) for(const name of targets) {
     if(page.url().startsWith('http://density.test')) await page.evaluate(() => localStorage.clear());
     await open(name,lang,width,theme);
     const label=`${name}/${lang}/${theme}/${width}`;
     if(name.startsWith('program-')) await checkProgram(width);
     if(name.startsWith('products')) {
      const table=page.locator('.production-schedule-products');
      assert.equal(await table.locator('thead th').count(),6);
      assert.equal(await table.locator('tbody tr').count(),2);
      await table.locator('details > summary').first().tap();
      const labels=await table.locator('tbody tr').first().locator('dt').allTextContents();
      assert.deepEqual(labels,lang==='es' ? ['Pedidos','Tipo original','Notas','Anotación 1','Anotación 2'] : ['Orders','Original type','Notes','Annotation 1','Annotation 2']);
      await touch();
     }
     if(name.includes('summary') || name.startsWith('review')) {
      if(name==='summary-empty') assert.equal(await page.locator('[data-summary-unit]').count(),0);
      else {
       assert.equal(await page.locator('[data-summary-unit]').count(),name.endsWith('-units') ? 2:1);
       const rows=page.locator('[data-summary-day]');
       assert.equal(await rows.count(),name.endsWith('-units') ? 14:7);
       assert.equal(await rows.first().evaluate(el => getComputedStyle(el).display),width<992 ? 'grid':'table-row');
       await page.locator('[data-summary-total-detail] > summary').first().tap();
       await rows.first().locator('summary').tap();
       await touch();
      }
      if(name.startsWith('review')) assert.equal(await page.locator('[name="Publish.Pin"]').inputValue(),'');
     }
     await overflow(label);
     if(lang==='es' && [360,768,1280].includes(width) && theme!=='system' && ['program-draft','program-open','products','summary-draft','review','empty'].includes(name)) {
      // Comparable screenshots show the initial, closed disclosures in both versions.
      await open(name,lang,width,theme);
      await page.screenshot({path:path.join(output,`${name}-es-${theme}-${width}.png`)});
     }
     results.push({name,lang,theme,width,passed:true});
    }
    console.log(`Passed ${theme}: ${widths.length*2*targets.length} cases.`);
   }
  }
  // Same short SKU and three saved applicable areas, with all details closed.
  shortSku=true;
  for(const width of [768,1280]) {
   await open('program-draft','es',width);
   
   
   const row=page.locator('[data-workspace-rows] > tr').first();
   const height=await row.evaluate(el => el.getBoundingClientRect().height);
   geometry.push({width,height,parts:await row.locator('[data-workspace-day-cell="0"]').evaluate(el => [...el.querySelectorAll('*')].map(n => ({name:n.className,height:n.getBoundingClientRect().height,text:n.textContent}))) });
   if(!before && !measureOnly) {
    const baseline=process.env.WAREHOUSE_SCHEDULE_GEOMETRY_BASELINE;
    const previous=JSON.parse(fs.readFileSync(baseline || path.join(evidence,'before/browser/results.json'),'utf8')).geometry.find(item => item.width===width);
    assert.ok(height <= (baseline ? previous.height + 1 : previous.height*.7),`Density at ${width}: ${previous.height} -> ${height}`);
   }
   await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({top:400,behavior:'instant'}); }); await frames();
   await page.screenshot({path:path.join(output,`short-sku-${width}.png`)});
  }
  shortSku=false;
  if(!before && !measureOnly && !detailsOnly && !captureOnly) {
   for(const width of [768,1280]) {
    await open('program-draft','es',width);
    assert.ok((await page.locator('[data-workspace-rows] > tr').first().boundingBox()).y < (width===1280 ? 900:1024));
    const head=await page.locator('[data-workspace-scroll] > table > thead').boundingBox();
    assert.ok(head.y+head.height < (width===1280 ? 900:1024));
   }
   await open('program-draft','es',360);
   assert.ok((await page.locator('[data-workspace-search]').boundingBox()).y < 800);
   await overflow('360 initial');
   await open('program-editable','es',768);
   const group=page.locator('[data-schedule-orders]');
   await group.locator('summary').first().tap();
   const detail=group.locator('[data-schedule-disclosure^="row:"]').first();
   await detail.locator('summary').tap();
   const key=await detail.getAttribute('data-schedule-disclosure');
   const ref=detail.locator('input').first(); await ref.fill('PERSIST-ORDER');
   const note=detail.locator('input').last(); await note.fill('PERSIST-NOTES');
   const quantity=group.locator('input.production-week-workspace__quantity:visible').first();
   await quantity.fill('1.2345'); await quantity.press('Enter');
   assert.equal(await group.locator('input.production-week-workspace__quantity:visible').nth(1).evaluate(el => el===document.activeElement),true);
   const breakdown=page.locator('.production-schedule-progress__breakdown').first();
   await breakdown.locator('summary').tap();
   const breakdownKey=await breakdown.getAttribute('data-schedule-disclosure');
   await page.locator('[data-workspace-search]').fill('NEW');
   await page.locator('[data-workspace-results] button').first().tap();
   assert.ok(await page.locator(`[data-schedule-disclosure="${key}"]`).evaluate(el => el.open));
   assert.ok(await page.locator(`[data-schedule-disclosure="${breakdownKey}"]`).evaluate(el => el.open));
   assert.ok(await group.evaluate(el => el.open));
   assert.equal(await page.locator(`[data-schedule-disclosure="${key}"] input`).first().inputValue(),'PERSIST-ORDER');
   assert.equal(await page.locator(`[data-schedule-disclosure="${key}"] input`).last().inputValue(),'PERSIST-NOTES');
   await page.locator('[data-workspace-copy-toggle]').tap();
   await page.locator('[data-workspace-copy-toggle]').tap();
   assert.equal(await group.locator('input.production-week-workspace__quantity:visible').first().inputValue(),'1.2345');
   assert.equal(await page.locator('[data-workspace-progress-warning]').isVisible(),true);
   await group.locator('[data-workspace-row]').nth(1).getByRole('button',{name:/Eliminar fila/}).tap();
   await page.getByRole('button',{name:'Deshacer',exact:true}).tap();
   assert.ok(await group.evaluate(el => el.open));
   assert.ok(await page.locator(`[data-schedule-disclosure="${key}"]`).evaluate(el => el.open));
   assert.equal(await group.locator('input.production-week-workspace__quantity:visible').first().inputValue(),'1.2345');
   
   const scroll=page.locator('[data-workspace-scroll]');
   await scroll.evaluate(el => { el.scrollLeft=el.scrollWidth; }); await frames();
   const focused=group.locator('input.production-week-workspace__quantity').first();
   await focused.focus(); await frames();
   const overlap=await focused.evaluate(el => ({x:el.getBoundingClientRect().left,sku:el.closest('table').querySelector('th').getBoundingClientRect().right}));
   assert.ok(overlap.x >= overlap.sku-1,`SKU covers focus: ${JSON.stringify(overlap)}`);
   await page.evaluate(() => localStorage.clear());
   for(const [value,level] of [[0,'low'],[.7499,'low'],[.75,'near'],[.9999,'near'],[1,'complete'],[null,null]]) {
    ratio=value; await open('program-draft','es',768);
    const cells=page.locator('.production-schedule-progress__metric');
    const levels=await cells.evaluateAll(nodes => nodes.map(el => el.dataset.progressLevel || null));
    assert.ok(levels.every(actual => actual===level));
    if(value===.9999) assert.ok(!(await cells.first().textContent()).includes('100'));
    assert.equal(await page.locator('.production-schedule-progress__breakdown[open]').count(),0);
    await contrast();
   }
   ratio=undefined;
   notApplicable=true; await open('program-draft','es',768);
   assert.match(await page.locator('.production-schedule-progress__metric').nth(1).innerText(),/Costura.*No aplica/s);
   assert.equal(await page.locator('.production-schedule-progress__produced').count(),14);
   assert.equal(await page.locator('.production-schedule-progress__metric').nth(1).getAttribute('data-progress-level'),null);
   notApplicable=false;
   for(const theme of ['light','dark','system']) for(const lang of ['es','en']) for(const name of ['program-draft','program-closed','products','summary-units','review-blocked']) {
    // 640 CSS px represents a 1280 px viewport at 200% desktop zoom.
    await open(name,lang,640,theme,450); await overflow(`200% ${name}/${lang}/${theme}`); await touch();
   }
  }
  assert.deepEqual(errors,[]); assert.equal(writes,0);
  fs.writeFileSync(path.join(output,measureOnly ? 'geometry.json' : flowsOnly ? 'flows-results.json' : detailsOnly ? 'details-results.json' : captureOnly ? 'captures-results.json' : 'results.json'),JSON.stringify({passed:results.length,geometry,zoom200Emulated:before || detailsOnly || captureOnly ? 0:30,errors,writes,results},null,2));
  console.log(`Passed ${results.length} matrix cases; geometry ${JSON.stringify(geometry.map(({width,height}) => ({width,height})))}; errors ${errors.length}, writes ${writes}.`);
 } finally { await browser.close(); }
})().catch(error => {console.error(error);process.exitCode=1;});
