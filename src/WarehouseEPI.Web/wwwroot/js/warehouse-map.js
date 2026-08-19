(() => {
  const mapRoot = document.querySelector("[data-warehouse-map]");
  if (mapRoot) {
    const svg = mapRoot.querySelector("svg");
    const placeholder = mapRoot.querySelector(".map-detail-placeholder");
    let box = { x: 0, y: 0, width: 1600, height: 900 };
    const applyBox = () => svg?.setAttribute("viewBox", `${box.x} ${box.y} ${box.width} ${box.height}`);
    const open = (id) => {
      mapRoot.querySelectorAll("[data-map-detail]").forEach((item) => { item.hidden = item.dataset.mapDetail !== id; });
      mapRoot.querySelectorAll("[data-map-open]").forEach((item) => item.classList.toggle("is-selected", item.dataset.mapOpen === id));
      if (placeholder) placeholder.hidden = true;
      const panel = mapRoot.querySelector(`[data-map-detail="${CSS.escape(id)}"]`);
      panel?.querySelector("[data-map-position]")?.click(); panel?.scrollIntoView({ block: "nearest" });
    };
    mapRoot.querySelectorAll("[data-map-open]").forEach((item) => {
      item.addEventListener("click", () => open(item.dataset.mapOpen));
      item.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(item.dataset.mapOpen); } });
    });
    mapRoot.querySelectorAll("[data-map-position]").forEach((button) => button.addEventListener("click", () => {
      const section = button.closest("[data-map-detail]"); section?.querySelectorAll("[data-position-detail]").forEach((item) => { item.hidden = item.dataset.positionDetail !== button.dataset.mapPosition; });
      section?.querySelectorAll("[data-map-position]").forEach((item) => item.classList.toggle("is-selected", item === button));
    }));
    mapRoot.querySelectorAll("[data-map-close]").forEach((button) => button.addEventListener("click", () => { button.closest("[data-map-detail]").hidden = true; if (placeholder) placeholder.hidden = false; }));
    document.querySelector("[data-map-zoom='in']")?.addEventListener("click", () => { box.width *= .8; box.height *= .8; applyBox(); });
    document.querySelector("[data-map-zoom='out']")?.addEventListener("click", () => { box.width = Math.min(1600, box.width * 1.25); box.height = Math.min(900, box.height * 1.25); applyBox(); });
    document.querySelector("[data-map-fit]")?.addEventListener("click", () => { box = { x: 0, y: 0, width: 1600, height: 900 }; applyBox(); });
    mapRoot.querySelector("[data-map-target='true']")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  }

  const editor = document.querySelector("[data-map-editor]");
  if (!editor) return;
  const svg = editor.querySelector("[data-editor-svg]");
  const field = editor.querySelector("[data-editor-geometry]");
  const elements = [...editor.querySelectorAll("[data-editor-element]")];
  let selected = null; let interaction = null; let undo = []; let redo = [];
  const number = (element, key) => Number.parseFloat(element.dataset[key] || "0");
  const snapshot = () => elements.map((element) => ({ id: element.dataset.editorElement, x: number(element, "x"), y: number(element, "y"), width: number(element, "width"), height: number(element, "height"), rotation: Number(element.dataset.rotation), zIndex: Number(element.dataset.z), isVisible: element.dataset.visible === "true" }));
  const updateField = () => { if (field) field.value = JSON.stringify(snapshot().map((item) => ({ Id: item.id, X: item.x, Y: item.y, Width: item.width, Height: item.height, Rotation: item.rotation, ZIndex: item.zIndex, IsVisible: item.isVisible }))); };
  const render = (element) => { const x=number(element,"x"), y=number(element,"y"), width=number(element,"width"), height=number(element,"height"); element.setAttribute("transform", `translate(${x} ${y}) rotate(${element.dataset.rotation || 0})`); element.querySelector("rect:not(.editor-resize-handle)")?.setAttribute("width", width); element.querySelector("rect:not(.editor-resize-handle)")?.setAttribute("height", height); const handle=element.querySelector(".editor-resize-handle"); handle?.setAttribute("x", width-10); handle?.setAttribute("y", height-10); element.classList.toggle("is-hidden", element.dataset.visible !== "true"); updateField(); };
  const restore = (state) => { state.forEach((item) => { const element=elements.find(value=>value.dataset.editorElement===item.id); if(!element)return; Object.assign(element.dataset,{x:item.x,y:item.y,width:item.width,height:item.height,rotation:item.rotation,z:item.zIndex,visible:String(item.isVisible)}); render(element); }); };
  const checkpoint = () => { undo.push(snapshot()); if(undo.length>50)undo.shift(); redo=[]; editor.querySelector("[data-editor-undo]").disabled=false; editor.querySelector("[data-editor-redo]").disabled=true; };
  const select = (element) => { selected?.classList.remove("is-selected"); selected=element; selected?.classList.add("is-selected"); };
  elements.forEach((element) => {
    element.addEventListener("pointerdown", (event) => { event.preventDefault(); checkpoint(); select(element); element.setPointerCapture(event.pointerId); interaction={pointer:event.pointerId,startX:event.clientX,startY:event.clientY,x:number(element,"x"),y:number(element,"y"),width:number(element,"width"),height:number(element,"height"),resize:event.target.classList.contains("editor-resize-handle")}; });
    element.addEventListener("pointermove", (event) => { if(!interaction||interaction.pointer!==event.pointerId)return; const rect=svg.getBoundingClientRect(), scale=1600/rect.width, dx=(event.clientX-interaction.startX)*scale, dy=(event.clientY-interaction.startY)*scale; if(interaction.resize){element.dataset.width=String(Math.max(20,Math.min(1600-interaction.x,interaction.width+dx)));element.dataset.height=String(Math.max(20,Math.min(900-interaction.y,interaction.height+dy)));}else{element.dataset.x=String(Math.max(0,Math.min(1600-number(element,"width"),interaction.x+dx)));element.dataset.y=String(Math.max(0,Math.min(900-number(element,"height"),interaction.y+dy)));}render(element); });
    element.addEventListener("pointerup", () => { interaction=null; });
    element.addEventListener("click", () => select(element)); render(element);
  });
  editor.querySelector("[data-editor-rotate]")?.addEventListener("click",()=>{if(!selected)return;checkpoint();selected.dataset.rotation=String((Number(selected.dataset.rotation)+90)%360);render(selected);});
  editor.querySelector("[data-editor-hide]")?.addEventListener("click",()=>{if(!selected)return;checkpoint();selected.dataset.visible="false";render(selected);});
  editor.querySelectorAll("[data-editor-place]").forEach(button=>button.addEventListener("click",()=>{const element=elements.find(item=>item.dataset.editorElement===button.dataset.editorPlace);if(!element)return;checkpoint();element.dataset.visible="true";element.dataset.x="100";element.dataset.y="100";render(element);select(element);button.hidden=true;}));
  editor.querySelector("[data-editor-undo]")?.addEventListener("click",()=>{if(!undo.length)return;redo.push(snapshot());restore(undo.pop());editor.querySelector("[data-editor-redo]").disabled=false;editor.querySelector("[data-editor-undo]").disabled=undo.length===0;});
  editor.querySelector("[data-editor-redo]")?.addEventListener("click",()=>{if(!redo.length)return;undo.push(snapshot());restore(redo.pop());editor.querySelector("[data-editor-undo]").disabled=false;editor.querySelector("[data-editor-redo]").disabled=redo.length===0;});
  window.addEventListener("keydown",event=>{if(!selected)return;if(event.key==="Escape"){select(null);return;}if(!["ArrowLeft","ArrowRight","ArrowUp","ArrowDown"].includes(event.key))return;event.preventDefault();checkpoint();const step=event.shiftKey?10:1;selected.dataset.x=String(Math.max(0,Math.min(1600-number(selected,"width"),number(selected,"x")+(event.key==="ArrowLeft"?-step:event.key==="ArrowRight"?step:0))));selected.dataset.y=String(Math.max(0,Math.min(900-number(selected,"height"),number(selected,"y")+(event.key==="ArrowUp"?-step:event.key==="ArrowDown"?step:0))));render(selected);});
  editor.querySelector("[data-map-save-form]")?.addEventListener("submit",event=>{updateField();const button=event.submitter;if(button){button.disabled=true;button.textContent="Guardando…";}});
  updateField();
})();
