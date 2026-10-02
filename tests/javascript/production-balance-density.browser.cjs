// Isolated Edge over HTTP-rendered Razor fixtures; no operational service or database.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const packages = path.join(root, 'tools/playwright/node_modules/.pnpm');
const installed = fs.readdirSync(packages).find(name => name.startsWith('playwright@'));
const { chromium } = require(path.join(packages, installed, 'node_modules/playwright'));
const before = process.argv.includes('--before');
const singleSku = process.argv.includes('--single-sku');
const measureOnly = process.argv.includes('--measure-only') || singleSku;
const flowsOnly = process.argv.includes('--flows-only');
const zoomOnly = process.argv.includes('--zoom-only');
const reviewOnly = process.argv.includes('--review-capture');
const successOnly = process.argv.includes('--success-only');
const capturesOnly = process.argv.includes('--capture-only');
const evidence = path.join(root, 'artifacts/tablet-tanda4');
const fixtures = path.join(evidence, before ? 'before/fixtures' : 'fixtures');
const output = path.join(evidence, before ? 'before/browser' : 'browser');
fs.mkdirSync(output, { recursive: true });
const widths = [360, 768, 899, 900, 991, 992, 1199, 1200, 1280];
(async () => {
 const browser = await chromium.launch({ channel: 'msedge', headless: true });
 const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, hasTouch: true });
 const page = await context.newPage();
 let screen = 'admin', language = 'es', previewRatio, forceReason = false, uncertain = false;
 let confirmSuccess = false, savedCell, missingProduct = false;
 let postClosed = false;
 const confirmBodies = [], previewBodies = [];
 const errors = [], cases = [], geometry = [];
 page.on('pageerror', error => errors.push(error.message));
 await context.route('**/*', async route => {
  const request = route.request(), url = new URL(request.url());
  if (url.hostname !== 'balance4.test') return route.abort();
  const handler = url.searchParams.get('handler');
  if(request.method()==='POST'&&handler==='BalanceEditRestore') {
   const body=request.postDataJSON();
   const html=fs.readFileSync(path.join(fixtures,`admin-${language}.html`),'utf8');
   const plans=[...html.matchAll(/<input\b[^>]*data-plan-line="([^"]+)"[^>]*>/g)].map(match=>({lineId:match[1],quantity:Number(match[0].match(/data-original="([^"]+)"/)[1]),lineVersion:Number(match[0].match(/data-line-version="([^"]+)"/)[1]),weekVersion:Number(match[0].match(/data-week-version="([^"]+)"/)[1])}));
   return route.fulfill({json:{registered:false,conflict:false,html:'',blocked:false,unavailable:[],weekVersion:1,plans,cells:body.edit.cells.map(cell=>({cell,current:cell.observed,blocked:false,conflict:false}))}});
  }
  if(request.method() === 'POST' && handler === 'BalanceEditPreview') {
   const body = request.postDataJSON();
   assert.equal(body.pin, ''); previewBodies.push(body);
   const balance = JSON.parse(fs.readFileSync(path.join(fixtures, `model-${language}.json`), 'utf8'));
   for(const product of balance.products) for(const area of [product.cutting,product.sewing,product.readyToPack]) {
    if(previewRatio !== undefined) {
     for(const coverage of [area.dailyCoverage,area.accumulatedCoverage]) { coverage.ratio=previewRatio; coverage.covered=previewRatio===null?0:previewRatio*(coverage.beforeAdvance??coverage.target); }
     product.statusRatio=previewRatio;
    }
   }
   return route.fulfill({json:{canConfirm:true,fingerprint:'reviewed',errors:[],requiresReason:forceReason,requiresAdmin:body.planChanges.length>0,
    cells:body.cells.map(cell=>({cell,current:Number(cell.observed),sku:'REVIEW-SKU'})),
    plans:body.planChanges.map(change=>({change,current:Number(change.observed),sku:reviewOnly?'SHORT-SKU':'REVIEW-SKU'})),newPlans:[],balance}});
  }
  if(request.method() === 'POST' && handler === 'BalanceEditConfirm') {
   const body=request.postDataJSON(); delete body.pin; confirmBodies.push(body);
   if(uncertain) { uncertain=false; return route.abort(); }
   if(confirmSuccess) { savedCell=body.cells[0]; return route.fulfill({json:{success:true,operationId:body.operationId,recordId:'scroll-regression-record'}}); }
   return route.fulfill({json:{success:false,errors:['Controlled validation rejection']}});
  }
  if (request.method() !== 'GET') return route.fulfill({ status: 405 });
  if (url.pathname.startsWith('/Operations/Production')) {
   let html=fs.readFileSync(path.join(fixtures, `${savedCell&&postClosed?'closed':savedCell&&missingProduct?'filtered':screen}-${language}.html`), 'utf8');
   if(savedCell&&!missingProduct) {
    const id=`balance-produced-${savedCell.productId}-${savedCell.area}-${savedCell.shift}`;
    html=html.replace(new RegExp(`<input\\b[^>]*\\bid="${id}"[^>]*>`),tag=>tag.replace(/data-original="[^"]*"/,`data-original="${savedCell.requested}"`).replace(/\bvalue="[^"]*"/,`value="${savedCell.requested}"`));
   }
   return route.fulfill({ contentType: 'text/html', body: html });
  }
  const asset = decodeURIComponent(url.pathname).replace(/\.[a-z0-9]{10}(?=\.(?:js|css|woff2|woff|svg|png)$)/, '');
  const relative = path.join('src/WarehouseEPI.Web/wwwroot', `.${asset}`);
  const saved = path.join(evidence, 'before', relative), current = path.join(root, relative);
  const file = before && fs.existsSync(saved) ? saved : current;
  if (file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({ path: file });
  return route.fulfill({ status: 204 });
 });
 const frames = () => page.evaluate(async () => { await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); });
 const open = async (name, lang, width, theme = 'light', height) => {
  screen = name; language = lang;
  await page.setViewportSize({ width, height: height || (width === 360 ? 800 : width === 1280 ? 900 : 1024) });
  await page.goto('http://balance4.test/Operations/Production');
  await page.locator('[data-balance-context]').waitFor();
  await page.locator(`[data-theme-choice="${theme}"]`).evaluate(el => el.click());
  await frames();
 };
 const overflow = async label => assert.ok(await page.evaluate(() => Math.max(document.body.scrollWidth,document.documentElement.scrollWidth)<=innerWidth+1),`Page overflow: ${label}`);
 const touch = async () => {
  const items=await page.locator('.production-balance summary, .production-balance input:not([type="hidden"]), .production-balance select, .production-balance button, .production-balance .btn').evaluateAll(nodes=>nodes.filter(el=>el.getClientRects().length&&el.checkVisibility()).map(el=>({text:el.textContent||el.id,height:el.getBoundingClientRect().height})));
  for(const item of items) assert.ok(item.height>=44,`Touch ${item.height}: ${item.text}`);
 };
 const contrast = async () => {
  const values=await page.locator('.production-balance-progress,.production-weekly-status-label,[data-balance-field],.balance-cell-hint').evaluateAll(nodes=>nodes.filter(el=>el.getClientRects().length&&el.checkVisibility()).map(el=>{
   const rgba=value=>value.match(/[\d.]+/g).map(Number), fg=rgba(getComputedStyle(el).color);
   let current=el,bg;
   while(current) {const color=rgba(getComputedStyle(current).backgroundColor);if(color.length===3||color[3]===1){bg=color;break;}current=current.parentElement;}
   if(!bg)return {text:el.textContent,ratio:0};
   const lum=color=>color.slice(0,3).map(x=>{x/=255;return x<=.04045?x/12.92:((x+.055)/1.055)**2.4;}).reduce((n,x,i)=>n+x*[.2126,.7152,.0722][i],0);
   const alpha=fg[3]??1, a=lum(fg.slice(0,3).map((v,i)=>v*alpha+bg[i]*(1-alpha))),b=lum(bg);
   return {text:el.textContent,ratio:(Math.max(a,b)+.05)/(Math.min(a,b)+.05)};
  }));
  for(const item of values) assert.ok(item.ratio>=4.5,`Contrast ${item.ratio}: ${item.text}`);
 };
 const matrix = async () => {
  for(const theme of ['light','dark','system']) {
   await page.emulateMedia({colorScheme:theme==='system'?'dark':'light'});
   for(const width of widths) for(const lang of ['es','en']) for(const name of ['admin','open','closed','draft','configuration','filtered','empty','edges','future']) {
    await open(name,lang,width,theme);
    const label=`${name}/${lang}/${theme}/${width}`;
    assert.equal(await page.locator('html').getAttribute('lang'),lang);
    assert.equal(await page.locator('[data-balance-context]').count(),1);
    assert.equal(await page.locator('[name="WeekId"]').count(),1);
    assert.equal(await page.locator('[data-balance-week]').evaluate(el=>el.form?.id),'balance-filters');
    if(name==='empty') { assert.equal(await page.locator('#balance-editor').count(),0); await overflow(label); cases.push(label); continue; }
    assert.equal(await page.locator('[data-balance-day]').count(),7);
    assert.equal(await page.locator('[data-edit-review-button]').count(),1);
    assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
    assert.equal(await page.locator('#balance-product-search,#balance-product-results').count(),0);
    assert.equal(await page.locator('[data-balance-metric]').count(),0);
    const rows=page.locator('[data-balance-product]');
    if(await rows.count()) {
     const areaColors=await rows.first().locator('.balance-value--final').evaluateAll(nodes=>nodes.map(el=>getComputedStyle(el).backgroundColor));
     assert.equal(new Set(areaColors).size,3,`Distinct area colors ${label}`);
    }
    if(name!=='filtered') {
     const modelName=['edges','future'].includes(name)?`${name}-model`:'model';
     const model=JSON.parse(fs.readFileSync(path.join(fixtures,`${modelName}-${lang}.json`),'utf8'));
     assert.equal(await rows.count(),model.products.length);
     assert.equal(await page.locator('[data-balance-disclosure="breakdown"][open]').count(),0);
     assert.equal(await rows.first().evaluate(el=>getComputedStyle(el).display),width<992?'grid':'table-row');
     if(width<992) {
      const labels=await rows.locator('.balance-value--shift1,.balance-value--shift2').evaluateAll(nodes=>nodes.map(el=>({shown:JSON.parse(getComputedStyle(el,'::before').content),expected:el.dataset.label})));
      for(const item of labels) assert.equal(item.shown,item.expected,`Full area and shift label ${label}`);
     }
     const fields=await rows.locator('[data-balance-field]').evaluateAll(nodes=>nodes.map(el=>({area:Number(el.dataset.area),field:el.dataset.balanceField,value:el.textContent})));
     assert.equal(fields.length,model.products.length*9);
     for(const product of model.products) {
      const row=page.locator(`[data-balance-product="${product.productId}"]`);
      for(const [index,area] of [product.cutting,product.sewing,product.readyToPack].entries()) {
       for(const [field,key] of [['opening','opening'],['pendingAfterShift1','pendingAfterShift1'],['netPending','netPending']]) assert.equal(await row.locator(`[data-balance-field="${field}"][data-area="${index}"]`).textContent(),area.applies?String(area[key]??area.pending):lang==='es'?'No aplica':'Not applicable');
      }
     }
     if(name==='admin') {
      assert.equal(await page.locator('[data-plan-line]').count(),4);
      assert.equal(await page.locator('[data-plan-line]:visible').count(),2);
      const details=page.locator('[data-balance-disclosure="program"]');
      await details.locator('summary').tap();
      assert.equal(await page.locator('[data-plan-line]:visible').count(),4);
      await page.locator('[data-balance-disclosure="breakdown"] summary').first().tap();
      assert.ok(await page.locator('[data-balance-breakdown]').first().isVisible());
      await touch();
      await details.locator('summary').tap();
     }
    }
    if(['closed','draft','configuration'].includes(name)) assert.ok(await page.locator('[data-balance-actions]').isHidden());
    await overflow(label); await contrast(); await touch();
    if(['admin','open'].includes(name)) {
     await page.evaluate(()=>{document.activeElement?.blur();window.scrollTo({top:400,behavior:'instant'});});await frames();
     const sticky=await page.locator('[data-balance-context]').evaluate(el=>{const ancestors=[];for(let x=el.parentElement;x;x=x.parentElement)ancestors.push({class:x.className,overflow:getComputedStyle(x).overflow,height:x.clientHeight,scrollHeight:x.scrollHeight,scrollTop:x.scrollTop});return {top:el.getBoundingClientRect().top,target:parseFloat(getComputedStyle(el).top),position:getComputedStyle(el).position,scrollY,ancestors};});
     assert.ok(Math.abs(sticky.top-sticky.target)<=1,`Context offset ${label}: ${JSON.stringify(sticky)}`);
     const input=page.locator('[data-edit-area]').first(); await input.focus();
     if(width<=900) {
      assert.equal(await page.locator('[data-balance-context]').evaluate(el=>getComputedStyle(el).position),'static');
      assert.equal(await page.locator('[data-balance-actions]').evaluate(el=>getComputedStyle(el).position),'static');
     }
     await input.evaluate(el=>el.blur());
     if(name==='admin'&&[360,768,1280].includes(width)) {
      await page.evaluate(()=>window.scrollTo({top:0,behavior:'instant'}));await frames();
      if(width===360) {
       const help=await page.locator('.balance-help').first().boundingBox(),toolbar=await page.locator('[data-balance-actions]').boundingBox();
       assert.ok(help.y+help.height<=Math.min(800,toolbar.y),`Balance help clear of toolbar ${label}: ${JSON.stringify({help,toolbar})}`);
      }
      else assert.ok((await rows.first().locator('.production-weekly-sku > strong').boundingBox()).y< (width===1280?900:1024),`Initial SKU ${label}`);
     }
    }
    cases.push(label);
   }
  }
 };
 const flows=async()=>{
  const flowResults=[];
  if(!zoomOnly) for(const lang of ['es','en']) for(const width of [360,768,900,1280]) {
   await open('admin',lang,width);
   await page.evaluate(()=>sessionStorage.clear());
   const planning=page.locator('[data-balance-disclosure="program"]');
   await planning.locator('summary').tap();
   const plan=planning.locator('[data-plan-line]').first();
   await plan.fill('31.2345');
   const planId=await plan.getAttribute('data-plan-line');
   await page.locator('[data-balance-disclosure="breakdown"] summary').first().tap();
   await page.setViewportSize({width:width===1280?768:1280,height:1024});await frames();
   assert.equal(await planning.getAttribute('open'),'');assert.equal(await plan.inputValue(),'31.2345');
   await page.reload();
   await page.waitForFunction(id=>document.activeElement?.dataset.planLine===id,planId);
   assert.equal(await planning.getAttribute('open'),'');assert.equal(await plan.inputValue(),'31.2345');
   assert.ok(await plan.isVisible());
   const draft=await page.evaluate(()=>Object.values(sessionStorage).map(value=>JSON.parse(value)));
   assert.ok(draft.every(item=>!('pin' in item.edit)&&!('fingerprint' in item.edit)));
   await page.locator('[data-edit-discard]').click();
   assert.equal(await plan.inputValue(),'30');
   await planning.locator('summary').tap();
   const first=page.locator('[data-edit-area="0"][data-edit-shift="1"]').first();
   await first.fill('4.1234');await first.press('Enter');
   assert.equal(await page.evaluate(()=>document.activeElement?.dataset.editShift),'2');
   if(width===1280) {
    await page.setViewportSize({width:1280,height:900});
    await page.locator('[data-balance-scroll]').evaluate(el=>el.scrollLeft=el.scrollWidth);
    await first.focus();await frames();
    const visible=await first.evaluate(el=>{const sku=el.closest('[data-balance-product]').querySelector('.production-weekly-sku').getBoundingClientRect(),box=el.getBoundingClientRect();return box.left>=sku.right+3;});
    assert.ok(visible,'Focused cell is clear of SKU');
   }
   for(const [index,ratio] of [0,.7499,.75,.9999,1,1.5,null].entries()) {
    previewRatio=ratio;
    const response=page.waitForResponse(r=>r.url().includes('handler=BalanceEditPreview')&&r.request().postDataJSON().cells.some(cell=>cell.requested===String(index+10)));
    await first.fill(String(index+10));await response;await frames();
    const row=first.locator('xpath=ancestor::tr');
    assert.equal(await row.locator('[data-balance-metric]').count(),0);
    const status=row.locator('.production-weekly-status');
    assert.equal(await status.getAttribute('data-status-band'),ratio===null?'no-base':ratio<.75?'low':ratio<1?'mid':'high');
    const shown=await status.locator('[data-balance-status]').textContent();
    if(ratio!==null&&ratio<1) assert.doesNotMatch(shown,/100[,.]0/);
    if(ratio===null) assert.match(shown,lang==='es'?/Sin meta/:/No target/);
    else assert.match(shown,/%/);
    assert.ok(await status.isVisible());
    await contrast();
    const details=first.locator('xpath=ancestor::tr').locator('[data-balance-breakdown="daily"][data-area="0"]');
    assert.match(await details.textContent(),lang==='es'?/Base de comparación/:/Comparison base/);
   }
   previewRatio=undefined;
   await first.fill('');assert.ok(await page.locator('[data-edit-review-button]').isDisabled());
   await first.press('Escape');assert.notEqual(await first.inputValue(),'');
   await first.fill('8');
   await page.locator('[data-edit-review-button]').click();
   await page.locator('[data-edit-confirm]').waitFor();
   assert.match(await page.locator('[data-edit-status]').textContent(),lang==='es'?/Vista previa sin guardar/:/Unsaved preview/);
   assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
   await first.fill('9');assert.ok(await page.locator('[data-edit-confirm]').isHidden());
   forceReason=true;
   await page.locator('[data-edit-review-button]').click();await page.locator('#balance-edit-reason').waitFor();
   await page.locator('#balance-edit-reason').fill('Controlled reason');
   await page.locator('[data-edit-review-button]').click();await page.locator('#balance-edit-pin').waitFor();
   await page.locator('#balance-edit-pin').fill('0123');
   uncertain=true;
   await page.locator('[data-edit-confirm]').click();
   await page.waitForFunction(()=>document.querySelector('#balance-edit-pin').value===''&&document.querySelector('[data-edit-confirm]').disabled===false);
   assert.ok(await first.isDisabled());
   await page.locator('#balance-edit-pin').fill('0123');
   await page.locator('[data-edit-confirm]').click();
   await page.waitForFunction(()=>document.querySelector('[data-edit-errors]').textContent.includes('Controlled'));
   assert.deepEqual(confirmBodies.at(-1),confirmBodies.at(-2));
   assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
   assert.ok(await first.isEnabled());
   await page.locator('[data-edit-discard]').click();
   forceReason=false;
   flowResults.push({language:lang,width,passed:true});
  }
  let zoom200Emulated=0;
  for(const lang of ['es','en']) for(const theme of ['light','dark','system']) for(const size of [{width:640,height:450},{width:384,height:512}]) {
   await open('admin',lang,size.width,theme,size.height);
   await overflow(`zoom200/${lang}/${theme}/${size.width}`);await touch();await contrast();
   const quantity=page.locator('[data-edit-area]').first();await quantity.focus();await frames();
   await page.waitForFunction(()=>{const box=document.activeElement.getBoundingClientRect();return box.top>=64&&box.bottom<=innerHeight;},null,{timeout:3000});
   const box=await quantity.boundingBox();
   assert.ok(box.y>=64&&box.y+box.height<=size.height,`Focused quantity visible at emulated 200% zoom: ${JSON.stringify({lang,theme,size,box})}`);
   await page.screenshot({path:path.join(output,`zoom200-${lang}-${theme}-${size.width}.png`)});
   zoom200Emulated++;
  }
  fs.writeFileSync(path.join(output,zoomOnly?'zoom-results.json':'flows-results.json'),JSON.stringify({passed:flowResults.length,flows:flowResults,zoom200Emulated,previewRequests:previewBodies.length,uncertainRetries:confirmBodies.length/2,errors},null,2));
 };
 const reviewCaptures=async()=>{
  forceReason=true;
  for(const theme of ['light','dark']) for(const width of [360,768,1280]) {
   await open('admin','es',width,theme);
   await page.locator('[data-balance-product]').filter({has:page.locator('strong',{hasText:/^SHORT-SKU$/})}).locator('[data-plan-line]').fill('25');
   await page.locator('[data-edit-review-button]').click();await page.locator('#balance-edit-reason').waitFor();
   await page.locator('#balance-edit-reason').fill('Corrección del programa revisada');
   await page.locator('[data-edit-review-button]').click();await page.locator('#balance-edit-pin').waitFor();
   assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
   await page.evaluate(()=>{document.activeElement?.blur();const editor=document.querySelector('#balance-editor');window.scrollTo({top:editor.getBoundingClientRect().bottom+scrollY-innerHeight,behavior:'instant'});});await frames();
   await page.screenshot({path:path.join(output,`review-es-${theme}-${width}.png`)});
  }
  forceReason=false;
 };
 const successFlows=async()=>{
  const completed=[];confirmSuccess=true;
  for(const lang of ['es','en']) for(const width of [360,768,900,1280]) for(const missing of [false,true]) {
   savedCell=null;missingProduct=missing;await open('admin',lang,width);
   await page.evaluate(()=>sessionStorage.clear());
   const row=page.locator('[data-balance-product]').filter({has:page.locator('strong',{hasText:/^SHORT-SKU$/})});
   const field=row.locator('[data-edit-area="2"][data-edit-shift="2"]');
   const id=await field.getAttribute('id');
   await field.fill('8');await page.locator('[data-edit-review-button]').click();
   await page.locator('#balance-edit-pin').waitFor();await page.locator('#balance-edit-pin').fill('0123');
   await Promise.all([page.waitForNavigation({waitUntil:'load'}),page.locator('[data-edit-confirm]').click()]);
   const expected=missing?await page.locator('[data-balance-week]').getAttribute('id'):id;
   await page.waitForFunction(id=>document.activeElement?.id===id,expected);
   await page.waitForFunction(()=>{const box=document.activeElement.getBoundingClientRect();return box.top>=64&&box.bottom<=innerHeight;});
   assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
   if(!missing) assert.equal(await page.locator(`[id="${id}"]`).inputValue(),'8');
   assert.ok(await page.evaluate(()=>!history.state?.epiBalanceReturn&&history.scrollRestoration==='auto'));
   assert.ok(await page.evaluate(()=>Object.keys(sessionStorage).every(key=>!key.startsWith('epi.balance.v1:'))));
   if(width>=992&&!missing) assert.ok(await field.evaluate(el=>el.getBoundingClientRect().left>=el.closest('tr').querySelector('.production-weekly-sku').getBoundingClientRect().right+3));
   if(width>=992&&!missing) assert.ok(await page.locator('#production-balance-table thead').evaluate(el=>el.getBoundingClientRect().top>=document.querySelector('[data-balance-context]').getBoundingClientRect().bottom), 'Area headers remain below context after confirmation');
   await overflow(`confirmed/${lang}/${width}/${missing}`);
   await page.screenshot({path:path.join(output,`confirmed-${lang}-${width}-${missing?'outside-filter':'product'}.png`)});
   completed.push({language:lang,width,missingProduct:missing,focused:expected,passed:true});
  }
  confirmSuccess=false;savedCell=null;missingProduct=false;
  confirmSuccess=true;postClosed=true;
  for(const lang of ['es','en']) for(const width of [768,1280]) {
   savedCell=null;await open('admin',lang,width);await page.evaluate(()=>sessionStorage.clear());
   const row=page.locator('[data-balance-product]').filter({has:page.locator('strong',{hasText:/^SHORT-SKU$/})});
   const product=await row.getAttribute('data-balance-product');
   await row.locator('[data-edit-area="2"][data-edit-shift="2"]').fill('8');
   await page.locator('[data-edit-review-button]').click();await page.locator('#balance-edit-pin').waitFor();
   await page.locator('#balance-edit-pin').fill('0123');
   await Promise.all([page.waitForNavigation({waitUntil:'load'}),page.locator('[data-edit-confirm]').click()]);
   await page.waitForFunction(id=>document.activeElement.matches('.production-weekly-sku')&&document.activeElement.closest('[data-balance-product]').dataset.balanceProduct===id,product);
   await page.waitForFunction(()=>{const box=document.activeElement.getBoundingClientRect();return box.top>=64&&box.bottom<=innerHeight;});
   assert.ok(await page.locator('[data-balance-actions]').isHidden());
   assert.equal(await page.locator('#balance-edit-pin').inputValue(),'');
   await page.screenshot({path:path.join(output,`confirmed-${lang}-${width}-closed.png`)});
   completed.push({language:lang,width,closedAfterConfirm:true,passed:true});
  }
  confirmSuccess=false;savedCell=null;postClosed=false;
  fs.writeFileSync(path.join(output,'success-results.json'),JSON.stringify({passed:completed.length,cases:completed,errors},null,2));
 };
 try {
  for (const width of [768, 1280]) {
   await open('admin', 'es', width);
   const row = page.locator('[data-balance-product]').filter({ has: page.locator('strong', { hasText: /^SHORT-SKU$/ }) });
   if(singleSku) await row.evaluate(el=>{for(const other of document.querySelectorAll('[data-balance-product]'))if(other!==el)other.closest('tbody').remove();});
   geometry.push(await row.evaluate(el => ({ width: innerWidth, height: el.getBoundingClientRect().height,
    tableWidth: document.querySelector('#production-balance-table').getBoundingClientRect().width,
    skuWidth: el.querySelector('.production-weekly-sku').getBoundingClientRect().width })));
  }
  const previous = !before && JSON.parse(fs.readFileSync(path.join(evidence, singleSku?'before/browser/single-sku-geometry.json':'before/browser/results.json'), 'utf8'));
  if (previous) for (const item of geometry) {
   const old = previous.geometry.find(x => x.width === item.width);
   assert.ok(item.height <= old.height * .8, `Row reduction at ${item.width}: ${old.height} -> ${item.height}`);
   if(item.width === 1280) assert.ok(item.tableWidth <= old.tableWidth * .85, `Table width reduction: ${old.tableWidth} -> ${item.tableWidth}`);
  }
  if(!before&&!measureOnly&&!flowsOnly&&!zoomOnly&&!reviewOnly&&!successOnly&&!capturesOnly) await matrix();
  if(flowsOnly||zoomOnly) await flows();
  if(reviewOnly) await reviewCaptures();
  if(successOnly) await successFlows();
  if (!singleSku && !reviewOnly && !successOnly && (before || !measureOnly && !flowsOnly && !zoomOnly)) {
   for (const theme of ['light', 'dark']) for (const width of [360, 768, 1280]) for (const name of ['admin', 'open', 'closed', 'draft']) {
    await open(name, 'es', width, theme);
    await page.screenshot({ path: path.join(output, `${name}-es-${theme}-${width}-initial.png`) });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({top:400,behavior:'instant'}); });
    await frames();
    await page.screenshot({ path: path.join(output, `${name}-es-${theme}-${width}-scroll.png`) });
   }
  }
  assert.deepEqual(errors, []);
  const resultFile=successOnly?'success-geometry.json':singleSku?'single-sku-geometry.json':reviewOnly?'review-geometry.json':flowsOnly?'flows-geometry.json':zoomOnly?'zoom-geometry.json':measureOnly?'geometry.json':capturesOnly?'captures-results.json':'results.json';
  fs.writeFileSync(path.join(output, resultFile), JSON.stringify({ geometry, passed: cases.length, errors }, null, 2));
  console.log(JSON.stringify({ geometry, passed: cases.length, errors }));
 } finally { await context.close(); await browser.close(); }
})();
