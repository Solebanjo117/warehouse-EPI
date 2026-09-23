(() => {
 'use strict';const root=document.querySelector('[data-supply-queue]');if(!root)return;
 const live=root.querySelector('[data-supply-live]');let busy=false,dirty=false;let last=root.dataset.checkedAt;
 root.querySelectorAll('form[method="post"]').forEach(form=>{form.addEventListener('input',()=>dirty=true);form.addEventListener('submit',()=>dirty=false);});
 const message=text=>{live.textContent=text+' Última consulta correcta: '+new Date(last).toLocaleTimeString('es-MX');};message('Cola consultada.');
 const refresh=async()=>{if(document.hidden||busy)return;busy=true;try{const response=await fetch(root.dataset.snapshotUrl,{headers:{Accept:'application/json'},cache:'no-store'});if(!response.ok)throw Error();const snapshot=await response.json();last=snapshot.checkedAt;message(snapshot.signature!==root.dataset.signature?'Hay cambios. Pulsa Actualizar para consultarlos.':'Sin cambios.');}catch{message('No se pudo actualizar. Se conserva la información anterior.');}finally{busy=false;}};
 root.querySelector('[data-queue-refresh]').addEventListener('click',()=>{if(!dirty||window.confirm('Hay captura sin guardar. ¿Actualizar y descartarla?'))location.reload();});
 window.setInterval(refresh,30000);
})();
