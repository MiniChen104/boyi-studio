export function installDrawers(){
  const $=selector=>document.querySelector(selector);
  const makeButton=(id,label)=>{const button=document.createElement('button');button.id=id;button.type='button';button.className='secondary drawer-trigger';button.textContent=label;return button;};
  const library=$('.library'),workspace=$('.workspace'),panel=$('#testPanel');
  if(!library||!workspace||!panel)return;
  document.body.classList.add('drawer-layout');

  const picker=document.createElement('dialog');picker.className='library-drawer';picker.id='libraryDrawer';picker.setAttribute('aria-labelledby','libraryDrawerTitle');
  const heading=document.createElement('header'),title=document.createElement('h2');title.id='libraryDrawerTitle';title.textContent='选择下一道题';heading.append(title);
  const close=makeButton('closeLibraryDrawer','收起');close.setAttribute('aria-label','关闭题库抽屉');heading.append(close);picker.append(heading,library);document.body.append(picker);
  const trigger=makeButton('openLibraryDrawer','题库 /');trigger.setAttribute('aria-haspopup','dialog');trigger.setAttribute('aria-controls','libraryDrawer');trigger.setAttribute('aria-expanded','false');$('.workspace-header .crumb').prepend(trigger);
  let returnFocus=null;
  function openLibrary(event){
    if(picker.open){$('#search').focus();return;}
    returnFocus=event?.currentTarget instanceof HTMLElement&&event.currentTarget.matches('button')?event.currentTarget:document.activeElement===document.body?trigger:document.activeElement;picker.showModal();trigger.setAttribute('aria-expanded','true');
    requestAnimationFrame(()=>{$('#search').focus();window.dispatchEvent(new Event('resize'));});
  }
  function closeLibrary(){if(!picker.open)return;picker.close();trigger.setAttribute('aria-expanded','false');if(returnFocus?.isConnected)returnFocus.focus({preventScroll:true});}
  trigger.onclick=openLibrary;close.onclick=closeLibrary;
  picker.addEventListener('cancel',event=>{event.preventDefault();closeLibrary();});
  picker.addEventListener('click',event=>{if(event.target!==picker)return;const r=picker.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)closeLibrary();});
  window.addEventListener('boyi-problem',closeLibrary);
  $('#problemList').addEventListener('click',event=>{const row=event.target.closest('.problem-item');if(row?.dataset.id===window.boyiUI.current()?.problem.id)closeLibrary();});
  document.querySelectorAll('.sidebar [data-view]').forEach(button=>button.addEventListener('click',openLibrary));
  const categories=$('.categories');if(categories){const details=document.createElement('details');details.className='drawer-categories';const summary=document.createElement('summary');summary.textContent='按知识点筛选';details.append(summary,categories);$('.library-heading').after(details);$('.sidebar .label-top')?.remove();}

  // Move the existing input/output nodes: no editor, draft or test state is recreated.
  $('.workspace-footer').before(panel);panel.classList.add('test-dock');panel.hidden=true;panel.setAttribute('role','region');panel.setAttribute('aria-label','本地测试台');
  const testTrigger=makeButton('testDrawerToggle','测试台');testTrigger.setAttribute('aria-controls','testPanel');testTrigger.setAttribute('aria-expanded','false');$('.editor-actions #run').after(testTrigger);
  const testClose=makeButton('closeTestDock','收起');testClose.setAttribute('aria-label','收起测试台');$('.test-header').append(testClose);
  function openTest(){panel.hidden=false;panel.classList.remove('collapsed');testTrigger.setAttribute('aria-expanded','true');window.dispatchEvent(new Event('resize'));}
  function closeTest(){panel.hidden=true;testTrigger.setAttribute('aria-expanded','false');testTrigger.focus({preventScroll:true});window.dispatchEvent(new Event('resize'));}
  testTrigger.onclick=()=>panel.hidden?openTest():closeTest();testClose.onclick=closeTest;$('#toggleTest').onclick=closeTest;$('#toggleTest').title='收起测试台';
  new MutationObserver(()=>{testTrigger.title=$('#testStatus').textContent;testTrigger.classList.toggle('has-test-result',!panel.hidden);}).observe($('#testStatus'),{subtree:true,childList:true,characterData:true});
  document.addEventListener('keydown',event=>{
    const editing=['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName)||document.activeElement?.isContentEditable;
    if(event.key==='/'&&!editing&&!document.querySelector('dialog[open]')){event.preventDefault();event.stopImmediatePropagation();openLibrary();}
    if(event.key==='Escape'&&!document.querySelector('dialog[open]')&&!panel.hidden){event.preventDefault();event.stopImmediatePropagation();closeTest();}
  },true);
  window.addEventListener('boyi-account-reset',()=>{closeLibrary();panel.hidden=true;testTrigger.setAttribute('aria-expanded','false');});
  window.boyiDrawers={openLibrary,closeLibrary,openTest,closeTest};
}
