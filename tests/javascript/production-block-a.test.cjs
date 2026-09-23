const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const script = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/production-traceability.js','utf8');
function element(value='') { return {value, dataset:{}, handlers:{}, checked:false, addEventListener(e,f){(this.handlers[e] ||= []).push(f);}, fire(e){for(const f of this.handlers[e]||[])f({preventDefault(){}});},setCustomValidity(message){this.error=message;}}; }
function setup() {
 const stage=element('stage'), input=element('10'), mode=element('false'), rework=element(), propose=element(), summary=element(), balance=element();
 const amounts={ 'BatchResult.GoodQuantity':element('10'),'BatchResult.ReworkQuantity':element('0'),'BatchResult.ScrapQuantity':element('0') };
 const rows=[0,1].map(i=>{const checkbox=element(),quantity=element('0');quantity.dataset={ratio:'2',pending:'100'};return {dataset:{stageId:'stage',productId:'product',issueId:String(i),created:String(i),sku:'MP',unit:'m',decimals:'true'},checkbox,quantity,querySelector:s=>s==='input[type="checkbox"]'?checkbox:quantity};});
 const form=element();form.elements={namedItem:n=>amounts[n]};form.querySelectorAll=()=>rows;form.querySelector=s=>({'[data-result-stage]':stage,'[data-result-input]':input,'[data-result-mode]':mode,'[data-rework-case]':rework,'[data-propose-consumption]':propose,'[data-consumption-summary]':summary,'[data-result-balance]':balance}[s]);
 vm.runInNewContext(script,{document:{querySelector:s=>s==='[data-batch-result-form]'?form:null},window:{confirm:()=>true,ProductionCapture:{parse:v=>/^\d+(?:\.\d{1,4})?$/.test(v)?Number(v):NaN}}});
 return {rows,stage,input,mode,propose,summary,form};
}
test('two supply links share one need, selected source takes priority',()=>{const h=setup();h.rows[1].checkbox.checked=true;h.propose.fire('click');assert.equal(h.rows[0].quantity.value,'0');assert.equal(h.rows[1].quantity.value,'20');});
test('manual quantities and selection survive processed quantity edits',()=>{const h=setup();h.rows[0].quantity.value='7';h.rows[0].checkbox.checked=false;h.input.value='11';h.form.fire('input');assert.equal(h.rows[0].quantity.value,'7');assert.equal(h.rows[0].checkbox.checked,false);});
test('availability caps proposal and leaves shortage visible',()=>{const h=setup();h.rows.forEach(r=>r.quantity.dataset.pending='4');h.propose.fire('click');assert.equal(h.rows.reduce((s,r)=>s+Number(r.quantity.value),0),8);assert.match(h.summary.textContent,/requerido 20, disponible 8/);});
test('rework never proposes the full recipe',()=>{const h=setup();h.mode.value='true';h.mode.fire('change');h.propose.fire('click');assert.equal(h.propose.disabled,true);assert.equal(h.rows.reduce((s,r)=>s+Number(r.quantity.value),0),0);});
test('selected materials from a different process block confirmation without erasing values',()=>{const h=setup();h.rows[0].checkbox.checked=true;h.rows[0].quantity.value='7';h.stage.value='other';h.form.fire('change');assert.match(h.stage.error,/otro proceso/);assert.equal(h.rows[0].quantity.value,'7');});
test('comma is never interpreted as a partial number',()=>{const h=setup();h.input.value='0,25';h.propose.fire('click');assert.equal(h.rows[0].quantity.value,'0');});
const recoverySource = fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/production-supply-preparation.js','utf8');
function recoveryHarness(kind, fetchResponse) {
 const nodes=[];
 function node(tag='div') {
   const n=element(); n.tagName=tag.toUpperCase();n.children=[];n.attributes={};n.disabled=false;
   n.append=(...items)=>n.children.push(...items); n.prepend=(...items)=>n.children.unshift(...items);
   n.setAttribute=(k,v)=>n.attributes[k]=v;n.hasAttribute=k=>k==='data-recovery-disabled'?n.dataset.recoveryDisabled!==undefined:k in n.attributes;
   n.focus=()=>{}; nodes.push(n);return n;
 }
 const controls=[node('input'),node('input')];controls[0].type='password';controls[0].name='Input.Pin';controls[0].value='6724';controls[1].name='__RequestVerificationToken';controls[1].value='csrf';
 const form=node('form');form.action='https://localhost/Operations/ProductionSupply/Prepare?lineId=line&handler='+kind;form.reportValidity=()=>true;
 const entries=[['Input.OperationId','same-operation'],['Input.LineId','line'],['Input.Pin','6724'],['Input.Sources[0].Quantity','3'],['__RequestVerificationToken','csrf']];
 class Data {constructor(original){this.items=original?entries.map(x=>[...x]):[];}append(k,v){this.items.push([k,v]);}get(k){return this.items.find(x=>x[0]===k)?.[1];}[Symbol.iterator](){return this.items[Symbol.iterator]();}}
 const root=node();root.dataset={lineId:'line'};root.querySelector=()=>controls[1];root.querySelectorAll=s=>s==='[data-supply-form],[data-supply-confirm]'?[form]:s==='form input,form select,form button'?controls:[];
 const storage=new Map(),calls=[],location={href:'https://localhost/Operations/ProductionSupply/Prepare?lineId=line',pathname:'/Operations/ProductionSupply/Prepare',assign:url=>location.assigned=url};
 vm.runInNewContext(recoverySource.slice(recoverySource.lastIndexOf('(() => {')),{document:{querySelector:()=>root,createElement:node},sessionStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},location,URL,FormData:Data,fetch:async(url,options)=>{calls.push({url:String(url),options});return fetchResponse(url,options,calls.length);}});
 return {form,nodes,storage,calls,location,click:label=>nodes.find(n=>n.textContent===label).fire('click'),flush:async()=>{await new Promise(resolve=>setImmediate(resolve));}};
}
test('lost save response recovers committed proof without another POST or storing PIN',async()=>{
 const h=recoveryHarness('Save',async(url,options)=>{if(options.method==='POST')throw Error('connection lost');return {ok:true,status:200,json:async()=>({isCurrent:false})};});
 h.form.fire('submit');await h.flush();
 const saved=[...h.storage.values()][0];assert.ok(saved);assert.doesNotMatch(saved,/6724|Input.Pin|csrf/);
 h.click('Consultar resultado');await h.flush();assert.equal(h.calls.filter(x=>x.options.method==='POST').length,1);assert.equal(h.storage.size,0);assert.ok(h.nodes.some(n=>/después cambió/.test(n.textContent||'')));
});
test('uncertain confirmation retries exactly the same operation with a fresh PIN',async()=>{
 let posts=0;const h=recoveryHarness('Confirm',async(url,options)=>{if(options.method==='POST'){if(++posts===1)throw Error('lost');return {ok:true,redirected:true,url:'https://localhost/confirmed'};}return {ok:false,status:404};});
 h.form.fire('submit');await h.flush();
 h.nodes.find(n=>n.id==='recovery-pin').value='6724';h.click('Reintentar la misma operación');await h.flush();
 assert.equal(posts,2);const requests=h.calls.filter(x=>x.options.method==='POST');assert.equal(requests[0].options.body.get('Input.OperationId'),requests[1].options.body.get('Input.OperationId'));assert.equal(requests[1].options.body.get('Input.Pin'),'6724');assert.equal(h.storage.size,0);
});
