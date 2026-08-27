(() => {
  "use strict";
  const root = document.querySelector("[data-label-editor]");
  if (!root) return;
  const editable = root.dataset.editable === "true";
  const jsonInput = root.querySelector("[data-design-json]");
  const canvas = root.querySelector("[data-canvas]");
  const stage = root.querySelector("[data-stage]");
  const propertyBox = root.querySelector("[data-properties]");
  const noSelection = root.querySelector("[data-no-selection]");
  const fieldBox = root.querySelector("[data-fields]");
  const warnings = root.querySelector("[data-client-warnings]");
  const sizeSelect = root.querySelector("select[name='Input.Size']");
  const svgNs = "http://www.w3.org/2000/svg";
  let documentModel;
  try { documentModel = JSON.parse(jsonInput.value); } catch { documentModel = { schemaVersion: 1, fields: [], elements: [] }; }
  documentModel.fields ||= []; documentModel.elements ||= [];
  let width = Number(root.dataset.width), height = Number(root.dataset.height), zoom = 1;
  let selected = new Set(), undo = [], redo = [], drag = null;

  const svg = (name, attrs = {}) => { const node = document.createElementNS(svgNs, name); Object.entries(attrs).forEach(([key,value]) => node.setAttribute(key, value)); return node; };
  const snapshot = () => JSON.stringify(documentModel);
  const commit = before => { const after = snapshot(); if (before !== after) { undo.push(before); if (undo.length > 80) undo.shift(); redo = []; sync(); } };
  const sync = () => { jsonInput.value = snapshot(); jsonInput.dispatchEvent(new Event("change")); validateClient(); };
  const bindingLabel = binding => ({ "product.sku":"SKU", "product.description":"Descripción", "product.unit":"Unidad", "product.externalReference":"Referencia externa", "input.quantity":"Cantidad", "input.manufacturingDate":"Fecha MFG", "input.isRepack":"REPACK" }[binding] || documentModel.fields.find(f => f.key === binding)?.label || binding || "Campo");
  const byId = id => documentModel.elements.find(item => item.id === id);
  const selectedItems = () => documentModel.elements.filter(item => selected.has(item.id));
  const normalize = value => Math.round(Number(value) || 0);
  const snap = value => root.querySelector("[data-snap]")?.checked ? Math.round(value / 50) * 50 : Math.round(value);
  const guid = () => crypto.randomUUID();

  function visual(element) {
    const group = svg("g", { class: `label-node${selected.has(element.id) ? " is-selected" : ""}`, "data-id": element.id, transform: `rotate(${element.rotation || 0} ${element.x + element.width / 2} ${element.y + element.height / 2})`, tabindex: "0" });
    const common = { x: element.x, y: element.y, width: element.width, height: element.height };
    if (element.type === "line") group.append(svg("line", { x1: element.x, y1: element.y + element.height / 2, x2: element.x + element.width, y2: element.y + element.height / 2, stroke: element.color, "stroke-width": Math.max(4, element.borderWidth * 8) }));
    else if (element.type === "rectangle") group.append(svg("rect", { ...common, fill: element.backgroundColor, stroke: element.color, "stroke-width": element.borderWidth * 8 }));
    else if (element.type === "image" && (element.assetId || element.builtInAssetKey)) group.append(svg("image", { ...common, href: element.builtInAssetKey === "extra-packaging-logo" ? "/images/labels/extra-packaging-logo.svg" : `/Labels/Assets/${element.assetId}`, preserveAspectRatio: "xMidYMid meet" }));
    else if (element.type === "code128") {
      group.append(svg("rect", { ...common, fill: "#fff", stroke: "#ddd", "stroke-width": 5 }));
      const bars = 38; for (let i=0;i<bars;i+=2) group.append(svg("rect", { x: element.x + 80 + i * (element.width-160)/bars, y: element.y + 30, width: (1+(i%4))*(element.width-160)/bars/2, height: element.height-100, fill: "#000" }));
      const t=svg("text", { x: element.x+element.width/2, y: element.y+element.height-25, "text-anchor":"middle", "font-size": Math.min(90,element.height/5), fill:"#000" }); t.textContent=bindingLabel(element.binding); group.append(t);
    } else {
      const text = svg("text", { x: element.align === "center" ? element.x+element.width/2 : element.align === "right" ? element.x+element.width-12 : element.x+12, y: element.y + Math.min(element.height*.68, element.fontSize*14), "text-anchor": element.align === "center" ? "middle" : element.align === "right" ? "end" : "start", "font-family": element.fontFamily, "font-size": element.fontSize*12, "font-weight": element.bold ? "700" : "400", fill: element.color });
      text.textContent = element.type === "text" ? element.text : `{${bindingLabel(element.binding)}}`; group.append(svg("rect", { ...common, fill: element.backgroundColor, opacity: element.backgroundColor === "#FFFFFF" ? "0" : "1" })); group.append(text);
      if (element.blankLine) group.append(svg("line", { x1:element.x, y1:element.y+element.height-12, x2:element.x+element.width, y2:element.y+element.height-12, stroke:element.color, "stroke-width":5 }));
    }
    group.append(svg("rect", { ...common, class:"label-selection" }));
    group.append(svg("rect", { x:element.x+element.width-35, y:element.y+element.height-35, width:70, height:70, class:"label-resize-handle", "data-resize":"true" }));
    group.addEventListener("pointerdown", event => beginPointer(event, element));
    group.addEventListener("click", event => { event.stopPropagation(); select(element.id, event.shiftKey); });
    return group;
  }

  function render() {
    canvas.replaceChildren(); canvas.setAttribute("viewBox", `0 0 ${width} ${height}`); canvas.setAttribute("width", width); canvas.setAttribute("height", height);
    canvas.append(svg("rect", { x:0,y:0,width,height,fill:"#fff" }));
    canvas.append(svg("rect", { x:140,y:140,width:width-280,height:height-280,class:"label-safe-area" }));
    [...documentModel.elements].sort((a,b)=>(a.zIndex||0)-(b.zIndex||0)).forEach(item => canvas.append(visual(item)));
    stage.classList.toggle("is-grid", root.querySelector("[data-grid]")?.checked ?? false);
    stage.style.transform = `scale(${zoom})`; stage.style.marginBottom = `${Math.max(0,(zoom-1)*stage.offsetHeight)}px`;
    showProperties(); renderFields(); sync();
  }

  function select(id, multiple=false) { if (!multiple) selected.clear(); selected.has(id) && multiple ? selected.delete(id) : selected.add(id); render(); }
  canvas.addEventListener("click", () => { selected.clear(); render(); });

  function beginPointer(event, element) {
    if (!editable || event.button !== 0) return;
    event.preventDefault(); event.stopPropagation();
    if (!selected.has(element.id)) { if (!event.shiftKey) selected.clear(); selected.add(element.id); }
    const point = clientPoint(event), before = snapshot(), originals = selectedItems().map(item => ({ id:item.id,x:item.x,y:item.y,width:item.width,height:item.height }));
    drag = { pointer:event.pointerId, start:point, before, originals, resize:event.target.dataset.resize === "true" };
    canvas.setPointerCapture(event.pointerId); render();
  }
  const clientPoint = event => { const rect=canvas.getBoundingClientRect(); return { x:(event.clientX-rect.left)*width/rect.width, y:(event.clientY-rect.top)*height/rect.height }; };
  canvas.addEventListener("pointermove", event => {
    if (!drag || drag.pointer !== event.pointerId) return;
    const point=clientPoint(event), dx=point.x-drag.start.x, dy=point.y-drag.start.y;
    drag.originals.forEach(original => { const item=byId(original.id); if (drag.resize && drag.originals.length===1) { item.width=Math.max(10,snap(original.width+dx)); item.height=Math.max(10,snap(original.height+dy)); } else { item.x=snap(original.x+dx); item.y=snap(original.y+dy); } clamp(item); }); render();
  });
  const finishPointer = event => { if (!drag || drag.pointer !== event.pointerId) return; const before=drag.before; drag=null; commit(before); };
  canvas.addEventListener("pointerup", finishPointer); canvas.addEventListener("pointercancel", finishPointer);
  function clamp(item) { item.width=Math.min(item.width,width); item.height=Math.min(item.height,height); item.x=Math.max(0,Math.min(item.x,width-item.width)); item.y=Math.max(0,Math.min(item.y,height-item.height)); }

  function showProperties() {
    const item=selectedItems()[0]; propertyBox.disabled=!editable || !item; noSelection.classList.toggle("d-none",!!item);
    root.querySelectorAll("[data-prop]").forEach(input => { if (!item) { if (input.type!=="checkbox") input.value=""; return; } const value=input.dataset.prop === "assetId" && item.builtInAssetKey ? `builtin:${item.builtInAssetKey}` : item[input.dataset.prop]; if (input.type==="checkbox") input.checked=!!value; else input.value=value ?? ""; });
  }
  root.querySelectorAll("[data-prop]").forEach(input => input.addEventListener("change", () => { const items=selectedItems(); if (!editable || items.length===0) return; const before=snapshot(), key=input.dataset.prop; items.forEach(item => { if(key==="assetId"&&input.value.startsWith("builtin:")){item.assetId=null;item.builtInAssetKey=input.value.slice(8);}else{item[key]=input.type==="checkbox" ? input.checked : input.type==="number" ? normalize(input.value) : input.value || null;if(key==="assetId")item.builtInAssetKey=null;} clamp(item); }); commit(before); render(); }));

  function newElement(type) { const asset=root.querySelector("[data-prop='assetId'] option[value]:not([value=''])")?.value||null; return { id:guid(), type, x:300, y:300, width:type==="line"?1200:1500, height:type==="line"?40:400, rotation:0, zIndex:Math.max(0,...documentModel.elements.map(e=>e.zIndex||0))+1, text:type==="text"?"Nuevo texto":null, binding:type==="field"||type==="code128"?"product.sku":null, assetId:type==="image"&&asset&&!asset.startsWith("builtin:")?asset:null, builtInAssetKey:type==="image"&&asset?.startsWith("builtin:")?asset.slice(8):null, fontFamily:"Arial", fontSize:18, bold:false, color:"#000000", backgroundColor:"#FFFFFF", borderWidth:1, align:"left", blankLine:false }; }
  root.querySelectorAll("[data-add]").forEach(button => button.addEventListener("click", () => { if(!editable)return; const before=snapshot(), item=newElement(button.dataset.add); documentModel.elements.push(item); selected=new Set([item.id]); commit(before); render(); }));

  function command(name) {
    if (name==="undo" && undo.length) { redo.push(snapshot()); documentModel=JSON.parse(undo.pop()); selected.clear(); render(); return; }
    if (name==="redo" && redo.length) { undo.push(snapshot()); documentModel=JSON.parse(redo.pop()); selected.clear(); render(); return; }
    if (name.startsWith("zoom")||name==="fit") { zoom=name==="zoom-in"?Math.min(2,zoom+.1):name==="zoom-out"?Math.max(.4,zoom-.1):1; render(); return; }
    if (!editable) return; const items=selectedItems(); if(!items.length)return; const before=snapshot();
    if(name==="delete") { documentModel.elements=documentModel.elements.filter(e=>!selected.has(e.id)); selected.clear(); }
    if(name==="front") items.forEach(e=>e.zIndex=Math.min(1000,e.zIndex+1));
    if(name==="back") items.forEach(e=>e.zIndex=Math.max(0,e.zIndex-1));
    if(name==="align-left") { const x=Math.min(...items.map(e=>e.x)); items.forEach(e=>e.x=x); }
    if(name==="align-top") { const y=Math.min(...items.map(e=>e.y)); items.forEach(e=>e.y=y); }
    if(name.startsWith("distribute") && items.length>2) { const axis=name.endsWith("x")?"x":"y", ordered=[...items].sort((a,b)=>a[axis]-b[axis]), start=ordered[0][axis], end=ordered.at(-1)[axis]; ordered.forEach((e,i)=>e[axis]=Math.round(start+(end-start)*i/(ordered.length-1))); }
    commit(before); render();
  }
  root.querySelectorAll("[data-command]").forEach(button=>button.addEventListener("click",()=>command(button.dataset.command)));

  function duplicate() { if(!editable)return; const before=snapshot(), copies=selectedItems().map(item=>({...item,id:guid(),x:item.x+100,y:item.y+100})); copies.forEach(clamp); documentModel.elements.push(...copies); selected=new Set(copies.map(e=>e.id)); commit(before); render(); }
  root.querySelector(".label-stage-wrap").addEventListener("keydown", event => { if(["INPUT","TEXTAREA","SELECT"].includes(event.target.tagName))return; if(event.key==="Escape"){selected.clear();render();return;} if(event.key==="Delete"){command("delete");event.preventDefault();return;} if(event.ctrlKey&&event.key.toLowerCase()==="d"){duplicate();event.preventDefault();return;} if(event.ctrlKey&&event.key.toLowerCase()==="z"){command("undo");event.preventDefault();return;} if(event.ctrlKey&&event.key.toLowerCase()==="y"){command("redo");event.preventDefault();return;} const delta={ArrowLeft:[-10,0],ArrowRight:[10,0],ArrowUp:[0,-10],ArrowDown:[0,10]}[event.key]; if(delta&&editable){const before=snapshot();selectedItems().forEach(item=>{item.x+=delta[0]*(event.shiftKey?10:1);item.y+=delta[1]*(event.shiftKey?10:1);clamp(item);});commit(before);render();event.preventDefault();} });

  function renderFields() {
    fieldBox.replaceChildren();
    const bindingSelect=root.querySelector("[data-prop='binding']"); bindingSelect.querySelectorAll("optgroup[data-custom]").forEach(e=>e.remove());
    const group=document.createElement("optgroup");group.label="Personalizados";group.dataset.custom="true";documentModel.fields.forEach(field=>{const option=document.createElement("option");option.value=field.key;option.textContent=field.label;group.append(option);});bindingSelect.append(group);
    documentModel.fields.forEach((field,index)=>{const box=document.createElement("div");box.className="label-custom-field";box.innerHTML=`<label>Clave</label><input class="form-control form-control-sm" data-field="key" value="${escapeAttribute(field.key)}"><label>Etiqueta</label><input class="form-control form-control-sm" data-field="label" value="${escapeAttribute(field.label)}"><label>Ayuda</label><input class="form-control form-control-sm" data-field="help" value="${escapeAttribute(field.help||"")}"><label>Tipo</label><select class="form-select form-select-sm" data-field="type"><option value="text">Texto</option><option value="number">Número</option><option value="date">Fecha</option><option value="boolean">Sí/no</option><option value="select">Lista</option></select><label>Valor inicial / opciones (una por línea)</label><textarea class="form-control form-control-sm" data-field="values">${escapeText(field.type==="select"?(field.options||[]).join("\n"):(field.defaultValue||""))}</textarea><label class="form-check mt-1"><input type="checkbox" class="form-check-input" data-field="required" ${field.required?"checked":""}><span class="form-check-label">Obligatorio</span></label><button type="button" class="btn btn-sm btn-link text-danger px-0" data-remove-field>Eliminar</button>`; box.querySelector("[data-field='type']").value=field.type; box.querySelectorAll("[data-field]").forEach(input=>input.addEventListener("change",()=>{const before=snapshot(),key=input.dataset.field;if(key==="values"){if(field.type==="select"){field.options=input.value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean);field.defaultValue=null;}else field.defaultValue=input.value||null;}else field[key]=input.type==="checkbox"?input.checked:(input.value||null);commit(before);render();}));box.querySelector("[data-remove-field]").addEventListener("click",()=>{const before=snapshot();documentModel.fields.splice(index,1);commit(before);render();});fieldBox.append(box);});
  }
  const escapeAttribute=value=>String(value??"").replaceAll("&","&amp;").replaceAll('"',"&quot;").replaceAll("<","&lt;");
  const escapeText=value=>String(value??"").replaceAll("&","&amp;").replaceAll("<","&lt;");
  root.querySelector("[data-add-field]").addEventListener("click",()=>{if(!editable)return;const before=snapshot(),number=documentModel.fields.length+1;documentModel.fields.push({key:`campo${number}`,label:`Campo ${number}`,help:null,type:"text",required:false,defaultValue:null,options:[]});commit(before);render();});
  root.querySelector("[data-grid]")?.addEventListener("change",render);
  sizeSelect?.addEventListener("change",()=>{const option=sizeSelect.selectedOptions[0];width=Number(option.dataset.width);height=Number(option.dataset.height);documentModel.elements.forEach(clamp);render();});

  function validateClient(){const issues=[];documentModel.elements.forEach(e=>{if(e.x<140||e.y<140||e.x+e.width>width-140||e.y+e.height>height-140)issues.push("Hay elementos dentro del margen seguro.");if((e.type==="field"||e.type==="code128")&&!e.binding)issues.push("Hay elementos sin campo asociado.");if(e.type==="code128"&&e.width<1200)issues.push("Hay un Code 128 potencialmente denso.");});warnings.textContent=[...new Set(issues)].join(" ");warnings.classList.toggle("d-none",issues.length===0);}
  root.querySelector("#editor-form")?.addEventListener("submit",sync);
  if(!editable) root.querySelectorAll("[data-add],[data-add-field],[data-command]").forEach(button=>button.disabled=true);
  render();
})();
