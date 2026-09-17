(() => {
    "use strict";
    const root = document.querySelector("[data-supply-preparation]");
    if (!root) return;
    const quantities = Array.from(root.querySelectorAll("[data-source-quantity]"));
    const live = root.querySelector("[data-supply-live]");
    const saveForm = root.querySelector("[data-supply-form]");
    const scan = root.querySelector("[data-source-scan]");
    const rows = Array.from(root.querySelectorAll("[data-source-row]"));
    const normalize = value => (value || "").trim().toUpperCase();
    const selectSource = code => {
        const row = rows.find(item => normalize(item.dataset.sourceCode) === normalize(code));
        if (!row) {
            if (live) live.textContent = `No se encontró una ubicación operativa con el código ${code}.`;
            scan?.focus();
            return false;
        }
        row.scrollIntoView({ block: "center", behavior: "smooth" });
        row.querySelector("[data-source-quantity]")?.focus();
        if (live) live.textContent = `Origen ${row.dataset.sourceCode} seleccionado. Captura la cantidad.`;
        return true;
    };
    const confirmForm = root.querySelector('[data-supply-confirm]');
    const confirmButton = confirmForm?.querySelector('button[type="submit"]');
    const review = root.querySelector('[data-preparation-review]');
    const step = root.querySelector('[data-preparation-step-state]');
    const confirmState = root.querySelector('[data-confirmation-step-state]');
    let changed = false;
    const parse = value => /^\d+(?:\.\d{1,4})?$/.test(value) ? Number(value) : NaN;
    const updateDelivery = () => {
        const values = [...(confirmForm?.querySelectorAll('[name$=".Quantity"]') || [])].map(x => parse(x.value));
        const total = values.reduce((a,b)=>a+b,0);
        const output = root.querySelector('[data-delivery-review]');
        if(output) output.textContent = Number.isFinite(total) ? 'Se entregarán '+total+' '+root.dataset.unit+'. Pendiente resultante: '+Math.max(0,Number(root.dataset.pending)-total)+' '+root.dataset.unit+'.' : 'Corrige las cantidades entregadas.';
    };
    const update = () => {
        changed = quantities.some(input => parse(input.value) !== Number(input.closest('[data-source-row]').dataset.prepared));
        if(review) {
            review.replaceChildren();
            rows.forEach(row => {
                const value = row.querySelector('[data-source-quantity]').value;
                if(Number(value)>0 || Number(row.dataset.prepared)>0) {
                    const line=document.createElement('p');line.textContent=row.dataset.sourceCode+' → '+row.dataset.destination+': selección '+value+' '+root.dataset.unit+' · preparado '+row.dataset.prepared+' '+root.dataset.unit;review.append(line);
                }
            });
        }
        if(step)step.textContent=changed?'Selección modificada: guardar antes de entregar':root.dataset.preparationId?'Preparación guardada':'Pendiente de guardar';
        if(confirmState)confirmState.textContent=changed?'Guarda la selección modificada antes de confirmar la entrega.':'Revisa las cantidades realmente entregadas y el pendiente.';
        if(confirmButton)confirmButton.disabled=changed||root.dataset.requiresReview==='true';
        if(live)live.textContent=changed?'Revisa los orígenes y guarda la selección. Guardar no mueve material.':root.dataset.preparationId?'La selección coincide con la preparación guardada.':'Selecciona una cantidad por origen para continuar.';
        updateDelivery();
    };
    confirmForm?.addEventListener('input',updateDelivery);
    confirmForm?.addEventListener('submit',event=>{if(changed){event.preventDefault();event.stopImmediatePropagation();saveForm?.scrollIntoView({block:'center'});}},true);
    quantities.forEach(input => input.addEventListener("input", update));
    scan?.addEventListener("keydown", event => {
        if (event.key !== "Enter") return;
        event.preventDefault();
        if (selectSource(scan.value)) scan.value = "";
    });
    const cameraElement = root.querySelector("[data-camera-scanner]");
    const cameraButton = root.querySelector("[data-source-camera]");
    if (cameraElement && cameraButton && window.WarehouseEpiCreateCameraScanner) {
        const camera = window.WarehouseEpiCreateCameraScanner({
            element: cameraElement,
            instruction: "Centra el código de la ubicación de origen.",
            onCode: async code => ({ accepted: selectSource(code), message: "La ubicación no está entre los orígenes disponibles." })
        });
        cameraButton.addEventListener("click", () => camera.open());
    }
    update();
})();
(() => {
    'use strict';
    const root = document.querySelector('[data-supply-preparation]');
    if (!root) return;
    const key = 'production-pending:' + root.dataset.lineId;
    let pending = null;
    try { pending = JSON.parse(sessionStorage.getItem(key) || 'null'); } catch { /* Storage unavailable: keep capture in memory. */ }
    const panel = document.createElement('section');
    panel.className = 'alert alert-warning'; panel.hidden = true;
    const status = document.createElement('p'); status.setAttribute('role','status'); status.setAttribute('aria-live','polite');
    const check = document.createElement('button'); check.type='button'; check.className='btn btn-outline-primary'; check.textContent='Consultar resultado';
    const retry = document.createElement('button'); retry.type='button'; retry.className='btn btn-warning ms-2'; retry.textContent='Reintentar la misma operación';
    const pin = document.createElement('input'); pin.type='password'; pin.inputMode='numeric'; pin.autocomplete='off'; pin.className='form-control my-2'; pin.id='recovery-pin';
    const label=document.createElement('label');label.htmlFor=pin.id;label.textContent='NIP para reintentar (no se conserva)';
    panel.append(status,check,label,pin,retry); root.prepend(panel);
    const persist = () => { try { if(pending)sessionStorage.setItem(key,JSON.stringify(pending));else sessionStorage.removeItem(key); } catch {status.textContent+=' Mantén abierta esta pestaña para recuperar el resultado.';} };
    const lock = () => {
        panel.hidden = !pending;
        root.querySelectorAll('form input,form select,form button').forEach(input => {
            if(pending){ if(!input.hasAttribute('data-recovery-disabled')) input.dataset.recoveryDisabled=String(input.disabled);input.disabled=true;if(input.type==='password')input.value=''; }
            else if(input.hasAttribute('data-recovery-disabled')){input.disabled=input.dataset.recoveryDisabled==='true';delete input.dataset.recoveryDisabled;}
        });
    };
    const uncertain = () => {status.textContent='Resultado sin comprobar. Consulta el resultado antes de reintentar. Un resultado no encontrado todavía no descarta un guardado en curso.';pin.value='';check.disabled=retry.disabled=false;lock();};
    const resultUrl = () => {
        const url=new URL(location.href);url.searchParams.set('handler',pending.kind==='Save'?'SaveResult':'Result');url.searchParams.set('operationId',pending.operationId);url.searchParams.set('lineId',root.dataset.lineId);return url;
    };
    const lookup = async () => {
        if(!pending)return false;
        check.disabled=true;
        try {
            const response=await fetch(resultUrl(),{credentials:'same-origin',cache:'no-store'});
            if(response.status===404){uncertain();return false;}
            if(!response.ok)throw new Error('Consulta no disponible');
            const proof=await response.json();
            const wasSave=pending.kind==='Save';pending=null;persist();lock();panel.hidden=false;
            check.hidden=retry.hidden=pin.hidden=label.hidden=true;
            status.textContent=wasSave?(proof.isCurrent?'Confirmado: preparación guardada.':'Confirmado: se guardó la preparación; después cambió. Revisa el estado actual.'):'Confirmado: surtimiento registrado. Cantidad '+proof.confirmedQuantity+'; pendiente '+proof.pendingQuantity+'.';
            const link=document.createElement('a');const currentUrl=new URL(location.href);currentUrl.searchParams.delete('handler');currentUrl.searchParams.delete('operationId');currentUrl.searchParams.set('lineId',root.dataset.lineId);link.href=currentUrl;link.className='btn btn-primary';link.textContent='Consultar estado actual y continuar';panel.append(link);
            // Keep the old page's operations blocked until fresh versions are loaded.
            root.querySelectorAll('form button').forEach(button=>button.disabled=true);
            return true;
        } catch {uncertain();return false;} finally {check.disabled=false;}
    };
    const send = async (form, retrying=false) => {
        let data;
        if(retrying){
            if(!pin.value){pin.focus();return;}
            data=new FormData();pending.fields.forEach(([name,value])=>data.append(name,value));
            const token=root.querySelector('input[name="__RequestVerificationToken"]');if(token)data.append(token.name,token.value);
            data.append('Input.Pin',pin.value);pin.value='';
        }else{
            if(pending||!form.reportValidity())return;
            data=new FormData(form);
            pending={kind:new URL(form.action).searchParams.get('handler'),operationId:data.get('Input.OperationId'),fields:[...data].filter(([name])=>name!=='__RequestVerificationToken'&&!/pin/i.test(name))};
            persist();
        }
        lock();status.textContent='Enviando…';check.disabled=retry.disabled=true;
        const url=new URL(location.href);url.searchParams.set('handler',pending.kind);
        try {
            const response=await fetch(url,{method:'POST',body:data,credentials:'same-origin'});
            if(!response.ok)throw new Error('Respuesta sin comprobar');
            if(response.redirected){pending=null;persist();location.assign(response.url);return;}
            const html=await response.text();
            // A normal validation page is a completed response; retain its captured fields and errors.
            pending=null;persist();document.open();document.write(html);document.close();
        } catch {uncertain();}
    };
    root.querySelectorAll('[data-supply-form],[data-supply-confirm]').forEach(form=>form.addEventListener('submit',event=>{event.preventDefault();send(form);}));
    check.addEventListener('click',lookup);
    retry.addEventListener('click',async()=>{const enteredPin=pin.value;if(!(await lookup())&&pending){pin.value=enteredPin;await send(null,true);}});
    if(pending){if(!['Save','Confirm'].includes(pending.kind)||!Array.isArray(pending.fields)){pending=null;persist();}else uncertain();}
    const failed=root.dataset.failedHandler;
    if(failed){
        const form=[...root.querySelectorAll('form')].find(form=>new URL(form.action).searchParams.get('handler')===failed);
        const error=root.querySelector('.validation-summary-errors');if(error){error.tabIndex=-1;error.focus();}
        if(form){
            const versions=[['Input.ExpectedRequestVersion','requestVersion'],['Input.ExpectedPreparationVersion','preparationVersion']];
            if(versions.some(([name,attr])=>form.elements.namedItem(name)&&form.elements.namedItem(name).value!==root.dataset[attr])){
                const button=document.createElement('button');button.type='button';button.className='btn btn-warning';button.textContent='Revisar datos actualizados';
                const note=document.createElement('p');note.textContent='Compara la captura conservada con los orígenes y disponibilidad actuales. Los orígenes no disponibles requieren corrección.';form.prepend(note,button);
                const submit=form.querySelector('button[type="submit"]');if(submit)submit.disabled=true;
                button.addEventListener('click',()=>{versions.forEach(([name,attr])=>{const input=form.elements.namedItem(name);if(input)input.value=root.dataset[attr];});for(const [name,attr] of [['Input.PreparationId','preparationId'],['Input.DestinationLocationId','destinationId']]){const input=form.elements.namedItem(name);if(input)input.value=root.dataset[attr];}if(submit)submit.disabled=form.hasAttribute('data-supply-confirm')&&root.dataset.requiresReview==='true';button.remove();});
            }
        }
    }
})();
