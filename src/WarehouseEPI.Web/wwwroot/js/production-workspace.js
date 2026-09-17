(() => {
 'use strict';
 const root=document.querySelector('[data-production-workspace]');if(!root)return;
 const select=root.querySelector('[data-work-task]'),capture=root.querySelector('[data-work-capture]'),source=root.querySelector('[data-work-source]');
 if(!select){source.querySelectorAll('form[method="post"]').forEach(form=>form.hidden=true);source.querySelectorAll('.alert-success,.validation-summary-errors').forEach(message=>capture.prepend(message));return;}
 const forms=[...source.querySelectorAll('form[method="post"]')];
 const titles={BatchResult:'Resultado y consumo',Process:'Avance',Handoff:'Entrega o recepción',Warehouse:'Recepción en bodega',Material:'Devolución y merma',Planning:'Planificación',Batch:'Crear lote',Action:'Estado de la orden',ReverseResult:'Corregir resultado',ReverseMaterial:'Corregir material',AdjustPlan:'Ajustar materiales'};
 const handler=form=>new URL(form.action,location.href).searchParams.get('handler');
 const drafts=new Map(),original=new Map();let current=null,active=null,dirty=false,submitting=false;
 const values=form=>[...form.elements].filter(x=>(x.name||x.id)&&x.type!=='password'&&x.name!=='__RequestVerificationToken'&&!(x.type==='hidden'&&[...form.elements].some(other=>other.name===x.name&&other.type==='checkbox'))).map(x=>({name:x.name,index:[...form.elements].indexOf(x),value:x.value,checked:x.checked}));
 const restore=(form,items)=>items.forEach(item=>{const fields=[...form.elements].filter(x=>x.name===item.name);const field=form.elements[item.index]||fields.find(x=>x.type!=='hidden')||fields[0];if(field){field.value=item.value;if(field.type==='checkbox')field.checked=item.checked;}});
 forms.forEach((form,index)=>{
   form.dataset.taskContext=form.closest?.('tr')?.textContent?.trim().replace(/\s+/g,' ').slice(0,100)||'';
   const ids=new Map();form.querySelectorAll('[id]').forEach(field=>{ids.set(field.id,field.id+'-task-'+index);field.id=ids.get(field.id);});
   form.querySelectorAll('label[for]').forEach(label=>{if(ids.has(label.htmlFor))label.htmlFor=ids.get(label.htmlFor);});
   for(const attr of ['aria-describedby','aria-controls'])form.querySelectorAll('['+attr+']').forEach(field=>field.setAttribute(attr,field.getAttribute(attr).split(' ').map(id=>ids.get(id)||id).join(' ')));
   original.set(form,values(form));form.hidden=true;capture.append(form);form.addEventListener('input',event=>{if(event.isTrusted!==false)dirty=true;});form.addEventListener('change',event=>{if(event.isTrusted!==false)dirty=true;});form.addEventListener('submit',event=>{queueMicrotask(()=>{submitting=!event.defaultPrevented;});});});
 source.querySelectorAll('.accordion').forEach(x=>x.hidden=true);
 const error=source.querySelector('.validation-summary-errors');if(error)capture.prepend(error);
 const success=source.querySelector('.alert-success');if(success)capture.prepend(success);
 const taskMessage=root.querySelector('[data-task-message]');
 // Preserve existing correction forms as explicit, separate tasks.
 forms.filter(form=>['ReverseMaterial','ReverseResult','AdjustPlan'].includes(handler(form))).forEach((form,index)=>{const option=document.createElement('option');option.value='correction-'+index;option.dataset.handler=handler(form);option.dataset.formIndex=String(forms.indexOf(form));option.textContent=(titles[handler(form)]||'Corrección administrativa')+' · '+form.dataset.taskContext;select.append(option);});
 const choose=()=>{
   if(active&&current){drafts.set(current,values(active));active.querySelectorAll('input[type="password"]').forEach(x=>x.value='');active.hidden=true;}
   const option=select.selectedOptions[0];current=select.value;active=null;taskMessage.replaceChildren();
   if(!current)return;
   if(option.dataset.url){const link=document.createElement('a');link.href=option.dataset.url;link.className='btn btn-primary';link.textContent=option.textContent;taskMessage.append(link);return;}
   active=option.dataset.formIndex!==undefined?forms[Number(option.dataset.formIndex)]:forms.find(form=>handler(form)===option.dataset.handler&&(option.dataset.handler!=='Material'||form.elements.namedItem('Material.StageId')?.value===option.dataset.stage));
   if(!active){taskMessage.textContent='Esta acción requiere revisar su configuración o no tiene material disponible. Consulta el detalle de la orden.';return;}
   if(drafts.has(current))restore(active,drafts.get(current));else{
     restore(active,original.get(active));
     const set=(name,value)=>{const field=active.elements.namedItem(name);if(field&&value!==undefined)field.value=value;};
     if(!root.dataset.failedHandler){const operation=active.querySelector('input[name$="OperationId"]');if(operation)operation.value=crypto.randomUUID();}
     set('BatchResult.StageId',option.dataset.stage);set('Process.StageId',option.dataset.stage);set('BatchResult.ReworkCaseId',option.dataset.case);
     set('BatchResult.IsRework',option.dataset.case?'true':'false');const legacyRework=active.elements.namedItem('Process.IsRework');if(legacyRework)legacyRework.checked=current.startsWith('rework');set('Action.Mode',option.dataset.mode);set('Handoff.Mode',option.dataset.mode);
     set('Handoff.SourceStageId',option.dataset.stage);set('Handoff.DeliveryEventId',option.dataset.delivery);
     if(option.dataset.delivery){const delivery=root.querySelector('[data-delivery-context="'+option.dataset.delivery+'"]');if(delivery){set('Handoff.SourceStageId',delivery.dataset.source);set('Handoff.TargetStageId',delivery.dataset.target);}}
     else if(option.dataset.handler==='Handoff'&&option.dataset.stage){const from=active.elements.namedItem('Handoff.SourceStageId');const to=active.elements.namedItem('Handoff.TargetStageId');if(from&&to){const index=[...from.options].findIndex(x=>x.value===from.value);to.selectedIndex=index;}}
   }
   active.hidden=false;
   active.querySelectorAll('input[type="password"]').forEach(x=>x.value='');
   active.querySelector('[data-result-stage]')?.dispatchEvent(new Event('change',{bubbles:true}));
   if(option.dataset.handler==='Handoff'){
     ['SourceStageId','TargetStageId','DeliveryEventId','BatchId'].forEach(name=>{const field=active.elements.namedItem('Handoff.'+name);if(field&&field.type!=='hidden')field.closest('div').hidden=!!option.dataset.stage;});
     const reason=active.elements.namedItem('Handoff.Reason');if(reason)reason.closest('div').hidden=option.dataset.mode==='receive';
     const mode=active.elements.namedItem('Handoff.Mode');if(mode){mode.closest('div').hidden=!current.startsWith('reconcile');[...mode.options].forEach(x=>x.hidden=current.startsWith('reconcile')&&!['return','lost'].includes(x.value));}
   }
   taskMessage.textContent=option.textContent+'. Revisa las cantidades antes de confirmar.';
   if(option.dataset.delivery){const delivery=root.querySelector('[data-delivery-context="'+option.dataset.delivery+'"]');if(delivery)taskMessage.textContent+=' '+delivery.dataset.summary+' · Saldo pendiente actual: '+delivery.dataset.pending;}
   active.querySelectorAll('input[type="password"]').forEach(x=>x.value='');
   if(error){error.tabIndex=-1;error.focus();}else active.querySelector('input:not([type="hidden"]),select')?.focus();
 };
 if(root.dataset.failedHandler) {
   const failedStage=source.querySelector('[data-production-capture]')?.dataset.failedStage;
   const failed=forms.find(form=>handler(form)===root.dataset.failedHandler&&(!failedStage||form.elements.namedItem('Material.StageId')?.value===failedStage));
   if(failed){const option=document.createElement('option');option.value='failed-capture';option.dataset.handler=root.dataset.failedHandler;option.dataset.formIndex=String(forms.indexOf(failed));option.textContent='Revisar captura conservada · '+(titles[root.dataset.failedHandler]||'Operación');select.append(option);drafts.set(option.value,values(failed));}
 }
 const options=[...select.options];
 let target=options.find(x=>root.dataset.requestCase&&x.dataset.case===root.dataset.requestCase)
   ||options.find(x=>root.dataset.requestDelivery&&x.dataset.delivery===root.dataset.requestDelivery&&x.value.startsWith(root.dataset.requestTask||'receive'))
   ||options.find(x=>root.dataset.requestTask&&x.value===root.dataset.requestTask)
   ||options.find(x=>root.dataset.requestStage&&x.dataset.stage===root.dataset.requestStage);
 if(root.dataset.failedHandler)target=options.find(x=>x.value==='failed-capture')||target;
 select.value=target?.value||(!success?options[1]?.value:'')||'';
 if(success){const next=document.createElement('button');next.type='button';next.className='btn btn-primary';next.textContent='Continuar con la siguiente tarea';next.addEventListener('click',()=>{select.selectedIndex=1;choose();});success.append(next);}
 select.addEventListener('change',choose);choose();
 window.addEventListener('beforeunload',event=>{if(dirty&&!submitting){event.preventDefault();event.returnValue='';}});
})();
