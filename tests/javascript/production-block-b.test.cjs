const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const read=name=>fs.readFileSync('src/WarehouseEPI.Web/wwwroot/js/'+name,'utf8');
function node(value='') {return {value,dataset:{},handlers:{},children:[],addEventListener(k,f){(this.handlers[k]??=[]).push(f)},async fire(k,event={}){for(const f of this.handlers[k]||[])await f(event)},append(...x){this.children.push(...x)},prepend(...x){this.children.unshift(...x)},replaceChildren(){this.children=[]},focus(){},scrollIntoView(){},querySelector(){return null},querySelectorAll(){return []}};}
function queueHarness(fetch) {
 const live=node(),refresh=node(),form=node(),root=node();root.dataset={signature:'original',checkedAt:'2026-09-16T12:00:00Z',snapshotUrl:'/snapshot'};
 root.querySelector=s=>s==='[data-supply-live]'?live:refresh;root.querySelectorAll=()=>[form];
 let interval,reloads=0;const document={hidden:false,querySelector:()=>root};
 vm.runInNewContext(read('production-supply-queue.js'),{document,fetch,window:{setInterval:f=>interval=f,confirm:()=>false},location:{reload:()=>reloads++},Date});
 return {live,refresh,form,document,poll:()=>interval(),reloads:()=>reloads};
}
test('queue notices same-count content change and never reloads automatically',async()=>{
 const h=queueHarness(async()=>({ok:true,json:async()=>({signature:'changed',pendingOrders:1,checkedAt:'2026-09-16T12:01:00Z'})}));
 await h.poll();assert.match(h.live.textContent,/Hay cambios/);assert.equal(h.reloads(),0);
 await h.form.fire('input');await h.refresh.fire('click');assert.equal(h.reloads(),0);
});
test('queue does not overlap requests or poll a hidden tab',async()=>{
 let calls=0,resolve;const h=queueHarness(()=>{calls++;return new Promise(r=>resolve=r)});
 h.document.hidden=true;await h.poll();assert.equal(calls,0);h.document.hidden=false;
 const pending=h.poll();await h.poll();assert.equal(calls,1);resolve({ok:true,json:async()=>({signature:'original',checkedAt:'2026-09-16T12:01:00Z'})});await pending;
});
test('queue keeps last successful timestamp on network failure',async()=>{
 const h=queueHarness(async()=>{throw Error('offline')});await h.poll();assert.match(h.live.textContent,/Se conserva la información anterior/);assert.equal(h.reloads(),0);
});
function workspaceHarness(failed=false) {
 const fields=[['BatchResult.StageId','stage1'],['BatchResult.OperationId','original'],['BatchResult.GoodQuantity','0'],['BatchResult.Pin',''],['BatchResult.IsRework','false']].map(([name,value])=>Object.assign(node(value),{name,type:name.endsWith('Pin')?'password':'text',dispatchEvent(){}}));
 fields[1].type='hidden';const check=Object.assign(node('true'),{name:'BatchResult.Materials[0].Selected',type:'checkbox',checked:false});const fallback=Object.assign(node('false'),{name:check.name,type:'hidden'});fields.push(check,fallback);
 fields.namedItem=name=>fields.find(x=>x.name===name);
 const form=node();form.action='https://localhost/Work?handler=BatchResult';form.elements=fields;form.querySelectorAll=s=>s.includes('password')?[fields[3]]:[];form.querySelector=s=>s.includes('OperationId')?fields[1]:s==='[data-result-stage]'?fields[0]:null;
 const options=[{value:'',dataset:{}},{value:'result-stage1',dataset:{handler:'BatchResult',stage:'stage1'},textContent:'Avance 1'},{value:'result-stage2',dataset:{handler:'BatchResult',stage:'stage2'},textContent:'Avance 2'}];
 const select=node();select.options=options;select.append=o=>options.push(o);Object.defineProperty(select,'selectedOptions',{get:()=>options.filter(x=>x.value===select.value)});
 const source=node();source.querySelectorAll=s=>s.startsWith('form')?[form]:[];source.querySelector=s=>s==='[data-production-capture]'?{dataset:{}}:null;
 const capture=node(),message=node(),root=node();root.dataset=failed?{failedHandler:'BatchResult'}:{};root.querySelector=s=>({'[data-work-task]':select,'[data-work-source]':source,'[data-work-capture]':capture,'[data-task-message]':message}[s]);
 const window=node();vm.runInNewContext(read('production-workspace.js'),{document:{querySelector:()=>root,createElement:()=>node()},location:{href:'https://localhost/Work'},URL,Map,crypto:{randomUUID:()=>Math.random().toString()},Event:class{},window,queueMicrotask});
 return {select,form,fields,check,window};
}
test('task switch preserves independent quantities and checkbox state and always clears PIN',async()=>{
 const h=workspaceHarness();h.fields[2].value='3';h.fields[3].value='6724';h.check.checked=true;await h.form.fire('input');
 h.select.value='result-stage2';await h.select.fire('change');assert.equal(h.fields[2].value,'0');assert.equal(h.fields[3].value,'');assert.equal(h.check.checked,false);
 h.fields[2].value='7';h.select.value='result-stage1';await h.select.fire('change');assert.equal(h.fields[2].value,'3');assert.equal(h.check.checked,true);assert.equal(h.fields[3].value,'');
 let warned=false;await h.window.fire('beforeunload',{preventDefault(){warned=true}});assert.equal(warned,true);
});
test('failed task remains explicitly selectable even if current availability changed',()=>{
 const h=workspaceHarness(true);assert.equal(h.select.value,'failed-capture');assert.equal(h.form.hidden,false);assert.equal(h.fields[3].value,'');
});
function preparationHarness() {
 const source=read('production-supply-preparation.js');
 const save=node(),confirm=node(),submit=node(),review=node(),state=node(),live=node(),delivery=node();
 const rows=['3','2'].map((value,index)=>{const row=node();row.dataset={sourceCode:'Origen '+index,destination:'WIP',prepared:value};row.input=node(value);row.input.closest=()=>row;row.querySelector=()=>row.input;return row});
 const actual=[node('3'),node('2')];confirm.querySelectorAll=()=>actual;confirm.querySelector=()=>submit;
 const root=node();root.dataset={preparationId:'saved',requiresReview:'false',unit:'m',pending:'10'};
 root.querySelectorAll=s=>s==='[data-source-quantity]'?rows.map(r=>r.input):s==='[data-source-row]'?rows:[];
 root.querySelector=s=>({'[data-supply-form]':save,'[data-supply-confirm]':confirm,'[data-preparation-review]':review,'[data-preparation-step-state]':state,'[data-supply-live]':live,'[data-delivery-review]':delivery}[s]);
 vm.runInNewContext(source.slice(0,source.lastIndexOf('(() => {')),{document:{querySelector:()=>root,createElement:()=>node()},window:{}});
 return {rows,submit,confirm,review,state,delivery,actual};
}
test('editing a saved origin requires save before confirming delivery',async()=>{
 const h=preparationHarness();assert.equal(h.submit.disabled,false);assert.equal(h.review.children.length,2);
 h.rows[0].input.value='1';await h.rows[0].input.fire('input');assert.equal(h.submit.disabled,true);assert.match(h.state.textContent,/guardar antes/);
 let stopped=false;await h.confirm.fire('submit',{preventDefault(){stopped=true},stopImmediatePropagation(){}});assert.equal(stopped,true);
});
test('confirmation review uses actual partial quantities and resulting pending amount',async()=>{
 const h=preparationHarness();h.actual[0].value='1';await h.confirm.fire('input');assert.match(h.delivery.textContent,/entregarán 3 m/);assert.match(h.delivery.textContent,/resultante: 7 m/);
});
