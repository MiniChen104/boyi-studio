'use strict';
const $=s=>document.querySelector(s);
const $$=s=>[...document.querySelectorAll(s)];
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icon=name=>`<svg><use href="#i-${name}"/></svg>`;
const pending=new Map();let serial=0;
function rpc(action,data={}){
  return new Promise((resolve,reject)=>{
    if(!window.chrome?.webview){reject(new Error('请从桌面上的博宜练习室打开。'));return;}
    const id=String(++serial),timer=setTimeout(()=>{pending.delete(id);reject(new Error('操作暂未完成，请稍后查看。'));},['officialLogin','officialWorkspace'].includes(action)?3600000:90000);
    pending.set(id,{resolve,reject,timer});window.chrome.webview.postMessage({id,action,data});
  });
}
let state={catalog:[],queue:[],logs:[],settings:{theme:'light',glass:78,fontSize:15,wrap:false,interval:60},running:false,loggedIn:false};
let current=null,view='all',category='',filter='all',query='',limit=100,tab='draft',dirty=false,saveTimer=null,loadToken=0,ignoreChange=false,modalKind='',lastCountdown='';
const favorites=new Set(JSON.parse(localStorage.getItem('favorites')||'[]'));
function loadFavorites(){
  if(!state.account)return;const key='boyi.favorites.'+state.account;
  if(!localStorage.getItem('boyi.favoritesMigrated')){if(!localStorage.getItem(key))localStorage.setItem(key,localStorage.getItem('favorites')||'[]');localStorage.setItem('boyi.favoritesMigrated',state.account);}
  favorites.clear();try{JSON.parse(localStorage.getItem(key)||'[]').forEach(id=>favorites.add(id));}catch{}
}
const categories=[['基础问题','#9ab7d6'],['分支问题','#c3a9c4'],['简单循环','#97bcb4'],['嵌套循环','#e0bc91'],['数组问题','#a6afd4'],['字符串','#a9c3d1'],['动态规划','#b5b2cf'],['数学','#b9c9a6']];
const editor=CodeMirror.fromTextArea($('#editor'),{mode:'text/x-c++src',lineNumbers:true,indentUnit:4,tabSize:4,matchBrackets:true,styleActiveLine:true,viewportMargin:20,lineWrapping:false,extraKeys:{Tab:cm=>cm.replaceSelection('    '),'Ctrl-S':()=>saveCurrent(true),'Ctrl-Enter':()=>runCode()}});
editor.on('change',()=>{
  if(ignoreChange || !current || tab!=='draft')return;
  current.draft=editor.getValue();dirty=true;$('#saveStatus').textContent='正在保存…';$('#saveDot').style.background='#c9b08e';
  clearTimeout(saveTimer);saveTimer=setTimeout(()=>saveCurrent(),650);
});
editor.on('cursorActivity',()=>{const c=editor.getCursor();$('#cursorStatus').textContent=`行 ${c.line+1}，列 ${c.ch+1}`;});
function toast(message,kind='info'){const el=document.createElement('div');el.className='toast '+kind;el.textContent=message;$('#toasts').append(el);setTimeout(()=>el.remove(),4500);}
function guard(fn){return async(...args)=>{try{return await fn(...args);}catch(e){toast(e.message||String(e),'error');}};}
let editorFrame=0,appliedFont,appliedWrap;
function scheduleEditorRefresh(){if(!editorFrame)editorFrame=requestAnimationFrame(()=>{editorFrame=0;editor.refresh();});}
function applySettings(){
  const s=state.settings;
  document.body.classList.toggle('dark',s.theme==='dark');
  document.documentElement.dataset.glassEngine=s.glassQuality==='optical'?'liquid-glass-react':'composited';
  document.documentElement.style.setProperty('--glass',String(1-s.glass/125));
  if(appliedFont!==s.fontSize){appliedFont=s.fontSize;document.documentElement.style.setProperty('--font-size',s.fontSize+'px');scheduleEditorRefresh();}
  if(appliedWrap!==s.wrap){appliedWrap=s.wrap;editor.setOption('lineWrapping',s.wrap);scheduleEditorRefresh();}
  window.dispatchEvent(new CustomEvent('boyi-state',{detail:{...state,settings:{...state.settings}}}));
}
function updateChrome(){
  $('#acceptedTotal').textContent=state.acceptedCount??state.catalog.filter(p=>(p.record?.status==='accepted'||p.record?.everAccepted)).length;
  $('#progressStatus').textContent=state.loggedIn?(state.progressMessage||'准备同步官网进度'):'登录后同步你的原有进度';
  $('#syncProgress').disabled=!!state.progressSyncing;
  window.dispatchEvent(new CustomEvent('boyi-state',{detail:{...state,settings:{...state.settings}}}));
  $('#totalBadge').textContent=state.catalog.length;$('#draftBadge').textContent=state.catalog.filter(p=>p.draft).length;$('#queueBadge').textContent=state.queue.length;
  $('#accountName').textContent=state.account||'我的账号';$('#accountStatus').textContent=state.loggedIn?'官网账号已连接':'本地进度 · 点击连接';
  $('#queueSummary').textContent=state.running?(lastCountdown||`正在处理 · 剩余 ${state.queue.length} 题`):state.queue.length?`${state.queue.length} 题待提交`:'提交队列为空';
  $('#syncText').textContent=state.syncing?'正在同步题库…':'本地题库 · 随时开始';
  $('#librarySummary').textContent=`${state.catalog.filter(p=>p.reference).length} 份参考答案 · ${state.catalog.filter(p=>p.cached).length} 份题面`;
}
async function refresh(){const previousAccount=state.account;state=await rpc('init');loadFavorites();if(previousAccount && state.account!==previousAccount){current=null;dirty=false;loadToken++;}applySettings();updateChrome();renderList();if(current){const p=state.catalog.find(p=>p.id===current.problem.id);if(p)current.record=p.record;updateHeading();}if(modalKind==='queue')renderQueue();}
function filtered(){return state.catalog.filter(p=>(view!=='drafts'||p.draft)&&(view!=='favorites'||favorites.has(p.id))&&(!category||p.tags.includes(category))&&(filter!=='todo'||!(p.record?.status==='accepted'||p.record?.everAccepted))&&(filter!=='reference'||p.reference)&&(!query||[p.id,p.title,...p.tags].join(' ').toLowerCase().includes(query.toLowerCase())));}
const listElement=$('#problemList'),ROW_HEIGHT=124,OVERSCAN=5;
let listRows=[],listStart=-1,listEnd=-1,listFrame=0;
let topSpacer,bottomSpacer;
function rowHtml(p){return `<button class="problem-item ${p.id===current?.problem.id?'selected':''}" data-id="${p.id}" aria-label="${esc(p.id+' '+p.title)}"><div class="item-meta"><span>NO. ${p.id}</span>${(p.record?.status==='accepted'||p.record?.everAccepted)?`<span class="done">${icon('check')}已通过</span>`:p.draft?'<span>有草稿</span>':'<span>'+esc(p.difficulty)+'</span>'}</div><div class="item-title">${esc(p.title)}</div><div class="item-tags"><span>${esc(p.tags[0]||'未分类')}</span>${p.reference?'<span class="ref">参考答案</span>':''}</div></button>`;}
function paintList(force=false){
  const height=listElement.clientHeight||700;
  const block=Math.floor(Math.min(Math.max(0,listRows.length-1),Math.floor(listElement.scrollTop/ROW_HEIGHT))/8)*8;
  const start=Math.max(0,block-OVERSCAN);
  const end=Math.min(listRows.length,block+8+Math.ceil(height/ROW_HEIGHT)+OVERSCAN);
  if(!force&&start===listStart&&end===listEnd)return;
  listStart=start;listEnd=end;
  const focusId=document.activeElement?.closest?.('.problem-item')?.dataset.id;
  if(!listRows.length){listElement.innerHTML='<div class="no-results">这里暂时没有题目<br>试试调整筛选条件</div>';topSpacer=bottomSpacer=null;return;}
  if(force||!topSpacer){
    listElement.innerHTML=`<div aria-hidden="true"></div>`+listRows.slice(start,end).map(rowHtml).join('')+`<div aria-hidden="true"></div>`;
    topSpacer=listElement.firstElementChild;bottomSpacer=listElement.lastElementChild;
  }else{
    const visible=listRows.slice(start,end),ids=new Set(visible.map(p=>p.id));
    const existing=new Map();
    listElement.querySelectorAll('.problem-item').forEach(el=>{if(ids.has(el.dataset.id))existing.set(el.dataset.id,el);else el.remove();});
    let cursor=topSpacer.nextElementSibling;
    for(const p of visible){
      let el=existing.get(p.id);
      if(!el){const template=document.createElement('template');template.innerHTML=rowHtml(p);el=template.content.firstElementChild;}
      if(el!==cursor)listElement.insertBefore(el,cursor);
      cursor=el.nextElementSibling;
    }
  }
  topSpacer.style.height=start*ROW_HEIGHT+'px';bottomSpacer.style.height=(listRows.length-end)*ROW_HEIGHT+'px';
  if(focusId)listElement.querySelector(`[data-id="${focusId}"]`)?.focus({preventScroll:true});
}
function scheduleListPaint(){if(!listFrame)listFrame=requestAnimationFrame(()=>{listFrame=0;paintList();});}
function renderList(reset=false){
  listRows=filtered();$('#listTitle').textContent=category||({all:'全部题目',drafts:'我的练习',favorites:'收藏题目'}[view]);$('#listCount').textContent=listRows.length;
  if(reset)listElement.scrollTop=0;
  else listElement.scrollTop=Math.min(listElement.scrollTop,Math.max(0,listRows.length*ROW_HEIGHT-(listElement.clientHeight||700)));
  paintList(true);
}
function updateListSelection(){listElement.querySelectorAll('.problem-item').forEach(el=>el.classList.toggle('selected',el.dataset.id===current?.problem.id));}
listElement.addEventListener('scroll',scheduleListPaint,{passive:true});
listElement.addEventListener('click',guard(e=>{const item=e.target.closest('.problem-item');if(item)return openProblem(item.dataset.id);}));
listElement.addEventListener('keydown',e=>{
  const item=e.target.closest('.problem-item');if(!item)return;
  const i=listRows.findIndex(p=>p.id===item.dataset.id);
  let next=e.key==='ArrowDown'?i+1:e.key==='ArrowUp'?i-1:e.key==='Home'?0:e.key==='End'?listRows.length-1:null;
  if(e.key==='Tab'&&((!e.shiftKey&&i===listEnd-1&&listEnd<listRows.length)||(e.shiftKey&&i===listStart&&listStart>0)))next=i+(e.shiftKey?-1:1);
  if(next===null)return;e.preventDefault();next=Math.max(0,Math.min(listRows.length-1,next));
  if(next*ROW_HEIGHT<listElement.scrollTop)listElement.scrollTop=next*ROW_HEIGHT;
  else if((next+1)*ROW_HEIGHT>listElement.scrollTop+listElement.clientHeight)listElement.scrollTop=(next+1)*ROW_HEIGHT-listElement.clientHeight;
  paintList();listElement.querySelector(`[data-id="${listRows[next].id}"]`)?.focus({preventScroll:true});
});
new ResizeObserver(scheduleListPaint).observe(listElement);
async function saveCurrent(manual=false){
  clearTimeout(saveTimer);
  if(!current || !dirty){if(manual)toast('草稿已是最新版本');return;}
  const item=current,id=item.problem.id,code=item.draft;
  try{await rpc('saveDraft',{id,code});if(current===item && current.draft===code){dirty=false;$('#saveStatus').textContent='已保存到本地';$('#saveDot').style.background='';}const row=state.catalog.find(p=>p.id===id);if(row)row.draft=true;if(manual)toast('已保存','success');}
  catch(e){$('#saveStatus').textContent='保存失败 · 请重试';toast(e.message,'error');throw e;}
}
async function openProblem(id){
  if(current?.problem.id===id)return;
  await saveCurrent();const token=++loadToken;$('#problemTitle').textContent='正在打开题目…';
  const data=await rpc('detail',{id});if(token!==loadToken)return;
  current=data;
  const references=current.references||[];
  const saved=localStorage.getItem('boyi.referenceChoice.'+id);
  const selected=references.find(r=>r.key===saved)||['format-default','readable-control','iostream-format'].map(key=>references.find(r=>r.key===key && r.status!=='unverified')).find(Boolean)||references.find(r=>!r.key.startsWith('research-'));
  if(selected){current.referenceKey=selected.key;current.reference=selected.code;current.referenceStatus=selected.status;}
  else{current.referenceKey='';current.reference='';current.referenceStatus='unverified';}
  dirty=false;tab='draft';localStorage.setItem('selectedProblem',id);updateHeading();renderStatement();setTab('draft');updateListSelection();$('#saveStatus').textContent=state.catalog.find(p=>p.id===id)?.draft?'已保存到本地':'自动保存已开启';
  const row=state.catalog.find(p=>p.id===id);if(row)row.cached=true;
  if(current.problem.samples?.length)$('#testInput').value=current.problem.samples[0].input;
  else $('#testInput').value='';
  $('#testOutput').textContent='运行代码后，输出会显示在这里。';$('#testStatus').textContent='样例是起点，不是全部';window.dispatchEvent(new CustomEvent('boyi-problem',{detail:{id}}));
}
function updateHeading(){if(!current)return;const p=current.problem;$('#problemNumber').textContent='NO. '+p.id;$('#problemTitle').textContent=p.title;$('#difficulty').textContent=p.difficulty;$('#currentCategory').textContent=p.tags[0]||'题库';$('#acceptedLabel').textContent=(current.record.status==='accepted'||current.record.everAccepted)?'✓ 已通过':'';$('#favorite').classList.toggle('is-favorite',favorites.has(p.id));$('#limits').textContent=[p.parameters?.['时间限制'],p.parameters?.['内存限制']].filter(Boolean).join(' · ');}
function renderStatement(){
  $('#statementBody').innerHTML=(current.problem.sections||[]).filter(s=>!['来源','标签'].includes(s.heading)).map(s=>`<section><h3>${esc(s.heading.replace(/[\uf000-\uf8ff]/g,''))}</h3>${s.html}</section>`).join('')+`<div class="source-note">题目来源：东方博宜 OJ<br>本地缓存 · ${esc((current.problem.fetched||'').split('T')[0])}</div>`;
  $('#statementBody').scrollTop=0;
  if(window.katex)$$('#statementBody .math').forEach(el=>{try{katex.render(el.textContent,el,{throwOnError:false,trust:false,displayMode:el.classList.contains('display')||el.classList.contains('block')});}catch{}});
}
async function setTab(next){
  if(!current)return;await saveCurrent();tab=next;
  $$('.editor-tabs button').forEach(b=>{b.classList.toggle('active',b.dataset.tab===next);b.setAttribute('aria-selected',String(b.dataset.tab===next));});
  const value=current[next]||'';ignoreChange=true;editor.setValue(value);editor.setOption('readOnly',next==='draft'?false:'nocursor');ignoreChange=false;
  $('#emptyVersion').classList.toggle('hidden',!!value||next==='draft');$('#copyReference').classList.toggle('hidden',next!=='reference'||!value);$('#run').disabled=!value;$('#stage').disabled=!value||next==='staged';
  $('#stage').innerHTML=(next==='reference'?'使用参考答案':'设为待提交')+icon('arrow');$('#importCode').classList.toggle('hidden',next!=='draft');
  $('#versionNotice').classList.toggle('hidden',next==='draft'||!value);
  const references=current.references||[];
  const selected=references.find(r=>r.key===current.referenceKey);
  $('#referenceTools').classList.toggle('hidden',next!=='reference'||!references.length);
  $('#referenceChoice').innerHTML=(!selected?'<option value="">请选择参考版本</option>':'')+references.map(r=>`<option value="${esc(r.key)}">${esc(r.title)}</option>`).join('');
  if(selected)$('#referenceChoice').value=selected.key;
  $('#referenceIdea').textContent=selected?.idea||'这份参考答案暂未附解题说明。';
  $('#referenceComplexity').textContent=selected?[selected.timeComplexity&&'时间：'+selected.timeComplexity,selected.spaceComplexity&&'空间：'+selected.spaceComplexity].filter(Boolean).join(' · '):'';
  const verification=current.referenceStatus==='accepted'?'✓ 这份代码已通过网站评测。':current.referenceStatus==='research'?'受限研究版：不满足完整范围或时限，请先阅读思路中的限制。':current.referenceStatus==='conditional'?`规则待确认：匹配 ${selected?.samples||0} 组公开样例，另有 ${selected?.extraTests||0} 组规则测试；具体冲突见思路。`:current.referenceStatus==='samples'?`已通过 ${selected?.samples||0} 组公开样例、${selected?.extraTests||0} 组补充测试；尚未确认官网评测。`:current.referenceStatus==='compiled'?'已编译检查，尚未确认网站评测结果。':'这份参考代码尚未验证。';
  $('#versionNotice').textContent=next==='reference'?verification+' 可复制到草稿修改。':`待提交版本来自${current.record.stageSource==='draft'?'我的代码':current.record.stageSolutionTitle||'参考答案'}。继续编辑草稿不会改变本版本。`;
  $('#emptyTitle').textContent=next==='staged'?'还没有待提交代码':references.length?'请选择参考版本':'这道题还没有参考答案';
  $('#emptyDescription').textContent=next==='staged'?'写好代码后，点击“设为待提交”保存一份提交版本。':references.length?'当前只有受限研究版，请先在上方选择并阅读其限制。':'这里不会用空模板冒充答案。你可以先写下自己的解法。';
  $('#bottomHint').textContent=next==='draft'?'先独立写，再与参考答案比较。':next==='reference'?'参考答案的正确性以实际评测为准。':'提交前，网站已通过的题目会自动跳过。';
  scheduleEditorRefresh();
}
async function stage(){
  if(!current)return;await saveCurrent();const source=tab==='reference'?'reference':'draft';
  // 空白草稿也先保存，才能由后端检查并拒绝空代码。
  if(source==='draft')await rpc('saveDraft',{id:current.problem.id,code:current.draft});
  const result=await rpc('stage',{id:current.problem.id,source,solutionKey:current.referenceKey||current.references?.[0]?.key||'default'});current.staged=result.code;current.record.stageSource=source;current.record.stageSolutionKey=result.solutionKey;current.record.stageSolutionTitle=result.solutionTitle;
  showModal('stage','READY TO SUBMIT','待提交版本已更新',`<p class="modal-note">第 ${esc(current.problem.id)} 题的待提交代码已替换为${source==='draft'?'你自己的代码':'参考答案'}。原参考答案和草稿仍然保留。</p><div class="form-actions"><button class="secondary" id="stageDone">先不加入队列</button><button class="primary" id="addStaged">加入提交队列 ${icon('plus')}</button></div>`);
  $('#stageDone').onclick=closeModal;if(current.record.status==='accepted'||current.record.everAccepted){const retry=document.createElement('button');retry.id='retryStaged';retry.className='secondary';retry.textContent='主动再次提交';$('#addStaged').before(retry);retry.onclick=guard(async()=>{if(!confirm('再次提交当前待提交版本？这次不会跳过已通过题，原通过记录会保留。'))return;state.queue=await rpc('retrySubmission',{id:current.problem.id});updateChrome();closeModal();toast('已加入重做队列，请在提交队列中开始。','success');});}
  $('#addStaged').onclick=guard(async()=>{const r=await rpc('queueAdd',{ids:[current.problem.id]});state.queue=r.queue;updateChrome();closeModal();toast(r.count?'已加入队列':'此题已通过或已经在队列中',r.count?'success':'info');});
}
async function runCode(){
  window.boyiDrawers?.openTest();
  if(!current||$('#run').disabled)return;await saveCurrent();const button=$('#run');button.disabled=true;$('#testStatus').textContent='编译并运行中…';$('#testPanel').classList.remove('collapsed');
  const id=current.problem.id;
  try{const result=await rpc('run',{id,code:editor.getValue(),input:$('#testInput').value});if(current?.problem.id!==id)return;
    $('#testOutput').textContent=result.output+(result.error?'\n'+result.error:'');$('#testStatus').textContent=result.kind;
    if(result.ok && current.problem.samples?.length){const sample=current.problem.samples.find(s=>s.input.trim()===$('#testInput').value.trim());if(sample){const norm=x=>x.replace(/\r/g,'').trimEnd();$('#testStatus').textContent=norm(sample.output)===norm(result.output)?'✓ 与该样例输出一致':'与样例输出不同，请检查';}}
  }catch(e){$('#testOutput').textContent=e.message;$('#testStatus').textContent='运行未完成';}finally{button.disabled=false;}
}
function showModal(kind,eyebrow,title,html){modalKind=kind;$('#modalEyebrow').textContent=eyebrow;$('#modalTitle').textContent=title;$('#modalBody').innerHTML=html;if(!$('#modal').open)$('#modal').showModal();}
function closeModal(){$('#modal').close();modalKind='';}
function accountModal(){
  if(state.loggedIn){showModal('account','YOUR ACCOUNT','账号已连接',`<p class="modal-note">${esc(state.account)}<br>登录状态仅保留到软件关闭，密码不会保存。<br>官网原有通过进度会同步到本地，切换账号时各自的草稿和队列分别保存。<br>${esc(state.progressMessage||'')}<br>${state.progressSyncedAt?'上次完成同步：'+esc(new Date(state.progressSyncedAt).toLocaleString('zh-CN')):''}</p><div class="form-actions"><button class="secondary" id="syncAccount" ${state.progressSyncing?'disabled':''}>同步官网进度</button><button class="secondary" id="logout">退出登录</button><button class="primary" id="accountDone">完成</button></div>`);$('#syncAccount').onclick=guard(async()=>{await rpc('syncProgress');closeModal();toast('正在同步官网进度');});$('#accountDone').onclick=closeModal;$('#logout').onclick=guard(async()=>{await rpc('logout');state.loggedIn=false;updateChrome();closeModal();});return;}
  showModal('account','WELCOME BACK','登录东方博宜',`<p class="modal-note">用你原来的账号继续练习，同步官网已有的通过进度、题单和提交记录。<br>账号密码直接发送到 oj.czos.cn，不保存在本地。</p><form id="loginForm"><label class="form-field">账号<input id="username" autocomplete="username" value="${esc(state.account)}"></label><label class="form-field">密码<div class="input-row"><input id="password" type="password" autocomplete="current-password"><button type="button" id="showPassword">显示</button></div></label><p id="loginError" class="modal-small"></p><div class="form-actions"><button type="button" class="secondary" id="officialLogin">官网验证登录</button><button type="submit" class="primary" id="loginButton">登录 ${icon('arrow')}</button></div></form>`);
  $('#officialLogin').onclick=guard(async()=>{await saveCurrent();const username=$('#username').value.trim();$('#password').value='';closeModal();if(await rpc('officialLogin',{username})){await refresh();toast('官网账号已连接','success');}});
  $('#showPassword').onclick=()=>{const p=$('#password');p.type=p.type==='password'?'text':'password';$('#showPassword').textContent=p.type==='password'?'显示':'隐藏';};
  $('#loginForm').onsubmit=async e=>{e.preventDefault();$('#loginButton').disabled=true;$('#loginError').textContent='正在连接…';try{await saveCurrent();const result=await rpc('login',{username:$('#username').value.trim(),password:$('#password').value});$('#password').value='';state.account=result.account;state.loggedIn=true;await refresh();closeModal();toast('登录成功，正在同步原有进度','success');if(!current && state.catalog.length)await openProblem(state.catalog[0].id);}catch(err){$('#loginError').textContent=err.message;$('#loginButton').disabled=false;}};
  setTimeout(()=>$('#password')?.focus(),50);
}
function settingsModal(){
  const s=state.settings;
  showModal('settings','MAKE IT YOURS','舒适，按你的习惯',`<p class="modal-note">更通透的玻璃，更安静的编辑空间。</p><div class="setting-row"><label>界面主题</label><select id="theme"><option value="light">晨光 · 浅色</option><option value="dark">夜色 · 深色</option></select></div><div class="setting-row"><label>玻璃透明度</label><input id="glass" type="range" min="15" max="90" value="${s.glass}"><output id="glassValue">${s.glass}%</output></div><div class="setting-row"><label>代码字号</label><input id="fontSize" type="range" min="12" max="24" value="${s.fontSize}"><output id="fontValue">${s.fontSize}px</output></div><div class="setting-row"><label for="wrap">代码自动换行</label><input id="wrap" type="checkbox" ${s.wrap?'checked':''}></div><label class="form-field">提交间隔（不少于60秒）<input id="interval" type="number" min="60" max="86400" value="${s.interval}"></label><label class="form-field">C++编译器路径<input id="compiler" value="${esc(s.compiler||'')}"></label><p class="modal-small">当前有 ${state.catalog.filter(p=>p.reference).length} 份参考答案，其余题目会明确显示“暂无参考答案”。不会自动生成空模板作为答案。</p><div class="form-actions"><button class="secondary" id="importReferences">导入参考答案包</button><button class="secondary" id="openData">数据目录</button><button class="primary" id="saveSettings">保存设置</button></div>`);
  $('#theme').value=s.theme;
  $('#theme').closest('.setting-row').insertAdjacentHTML('afterend','<div class="setting-row"><label for="glassQuality">玻璃效果</label><select id="glassQuality"><option value="smooth">流畅优先 · 推荐</option><option value="optical">完整折射 · 更高画质</option></select></div><p class="modal-small">流畅优先保留通透玻璃与边缘高光，减少大面积折射。动画跟随屏幕刷新率，实际帧率取决于当前负载。</p>');
  $('#glassQuality').value=s.glassQuality==='optical'?'optical':'smooth';
  const live=()=>{state.settings={...state.settings,theme:$('#theme').value,glassQuality:$('#glassQuality').value,glass:+$('#glass').value,fontSize:+$('#fontSize').value,wrap:$('#wrap').checked};$('#glassValue').textContent=state.settings.glass+'%';$('#fontValue').textContent=state.settings.fontSize+'px';applySettings();};
  ['theme','glassQuality','glass','fontSize','wrap'].forEach(id=>$('#'+id).oninput=live);
  $('#openData').onclick=guard(()=>rpc('openData'));
  $('#importReferences').onclick=guard(async()=>{const result=await rpc('importReferences');if(result){closeModal();await refresh();toast('已导入'+result.count+'份参考答案','success');}});
  $('#saveSettings').onclick=guard(async()=>{live();state.settings.interval=Math.max(60,+$('#interval').value||60);state.settings.compiler=$('#compiler').value.trim();state.settings=await rpc('settings',state.settings);closeModal();toast('设置已保存','success');});
}
function renderQueue(){
  showModal('queue','ONE AT A TIME','提交队列',`<p class="modal-note">按顺序提交，每两次至少间隔 ${state.settings.interval} 秒。普通队列跳过已通过题；主动重交除外，失败会暂停。<br>本次只使用已经保存的“待提交”版本。</p><div class="queue-head"><span class="pill">${state.queue.length} 题 ${state.running?'· 运行中':''}</span><button class="secondary" id="clearQueue" ${state.running?'disabled':''}>清空队列</button></div><div class="queue-items">${state.queue.map((id,i)=>{const p=state.catalog.find(p=>p.id===id);return `<div class="queue-row"><span class="queue-no">${String(i+1).padStart(2,'0')}</span><div class="queue-info"><strong>${esc(id)} · ${esc(p?.title||'')}</strong><small>${p?.record.stageSource==='draft'?'我的代码':'参考答案'} ${p?.record.explicitRetry?'· 主动重交':''} ${p?.record.result?'· '+esc(p.record.result):''}</small></div><button class="remove-queue" data-id="${id}" ${state.running?'disabled':''}>移除</button></div>`;}).join('')||'<div class="queue-empty">队列还空着。<br>写好代码，点击“设为待提交”后加入这里。</div>'}</div><div class="log-list" id="queueLogs">${logsHtml()}</div><div class="form-actions"><span class="modal-small" id="countdownLabel">${esc(lastCountdown)}</span><button class="secondary" id="pauseQueue" ${state.running?'':'disabled'}>暂停</button><button class="primary" id="startQueue" ${state.running||!state.queue.length?'disabled':''}>${state.loggedIn?'开始 / 继续':'登录后开始'} ${icon('play')}</button></div>`);
  $('#clearQueue').onclick=guard(async()=>{await rpc('queueClear');state.queue=[];updateChrome();renderQueue();});
  $$('.remove-queue').forEach(b=>b.onclick=guard(async()=>{state.queue=await rpc('queueRemove',{id:b.dataset.id});updateChrome();renderQueue();}));
  $('#pauseQueue').onclick=guard(async()=>{await rpc('stop');toast('正在暂停，当前请求结束后会停止。');});
  $('#startQueue').onclick=guard(async()=>{if(!state.loggedIn){accountModal();return;}await rpc('start');state.running=true;updateChrome();renderQueue();});
}
function logsHtml(){return state.logs.slice(-50).map(l=>`<div class="log-row ${esc(l.kind)}"><span>${esc(l.time)}</span>${esc(l.message)}</div>`).join('')||'任务记录会显示在这里。';}
window.chrome?.webview?.addEventListener('message',async e=>{
  const m=e.data;
  if(m.id){const p=pending.get(m.id);if(!p)return;clearTimeout(p.timer);pending.delete(m.id);m.ok?p.resolve(m.result):p.reject(new Error(m.error));return;}
  if(m.topic==='log'){state.logs.push(m.data);if($('#queueLogs')){$('#queueLogs').innerHTML=logsHtml();$('#queueLogs').scrollTop=$('#queueLogs').scrollHeight;}if(m.data.kind==='error')toast(m.data.message,'error');}
  if(m.topic==='countdown'){lastCountdown=`${m.data.seconds} 秒后提交 ${m.data.id}`;$('#queueSummary').textContent=lastCountdown;if($('#countdownLabel'))$('#countdownLabel').textContent=lastCountdown;}
  if(m.topic==='refresh'){lastCountdown='';await guard(refresh)();}
  if(m.topic==='record'){const row=state.catalog.find(p=>p.id===m.data.id);if(row)row.record=m.data.record;if(current?.problem.id===m.data.id){current.record=m.data.record;updateHeading();}renderList();}
  if(m.topic==='accountChanged'){current=null;dirty=false;loadToken++;await refresh();if(state.catalog.length)await openProblem(state.catalog[0].id);}
  if(m.topic==='progressSync'){state.progressMessage=m.data.message;state.progressSyncing=true;updateChrome();}
  if(m.topic==='sync'){state.syncing=true;$('#syncText').textContent=m.data.phase==='目录'?`目录：${m.data.done} 题`:`题面：${m.data.done}/${m.data.total}`;}
});
$('#categories').innerHTML=categories.map(([name,color])=>`<button class="category" data-category="${name}"><span class="dot" style="--tag-color:${color}"></span>${name.replace('问题','')}</button>`).join('');
$$('[data-category]').forEach(b=>b.onclick=()=>{closeOnline();category=category===b.dataset.category?'':b.dataset.category;limit=100;$$('[data-category]').forEach(x=>x.classList.toggle('active',x.dataset.category===category));renderList(true);});
$$('[data-view]').forEach(b=>b.onclick=()=>{closeOnline();view=b.dataset.view;category='';limit=100;$$('[data-view]').forEach(x=>x.classList.toggle('active',x===b));$$('[data-category]').forEach(x=>x.classList.remove('active'));renderList(true);});
$$('[data-filter]').forEach(b=>b.onclick=()=>{filter=b.dataset.filter;limit=100;$$('[data-filter]').forEach(x=>x.classList.toggle('active',x===b));renderList(true);});
let searchTimer;$('#search').oninput=()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{query=$('#search').value.trim();limit=100;renderList(true);},120);};
$$('[data-tab]').forEach(b=>b.onclick=guard(()=>setTab(b.dataset.tab)));
$('#referenceChoice').onchange=guard(async()=>{if(!current)return;const solution=current.references?.find(r=>r.key===$('#referenceChoice').value);if(!solution)return;current.referenceKey=solution.key;current.reference=solution.code;current.referenceStatus=solution.status;localStorage.setItem('boyi.referenceChoice.'+current.problem.id,solution.key);await setTab('reference');});
$('#favorite').onclick=()=>{if(!current)return;const id=current.problem.id;if(favorites.has(id))favorites.delete(id);else favorites.add(id);localStorage.setItem('boyi.favorites.'+state.account,JSON.stringify([...favorites]));updateHeading();if(view==='favorites')renderList();};
$('#source').onclick=guard(()=>current&&rpc('openSource',{id:current.problem.id}));
$('#focus').onclick=()=>{document.body.classList.toggle('focus-mode');$('#focus span').textContent=document.body.classList.contains('focus-mode')?'退出专注':'专注模式';scheduleEditorRefresh();};
$('#syncProgress').onclick=guard(async()=>{if(!state.loggedIn){accountModal();return;}await rpc('syncProgress');});
$('#openOnlineGroups').onclick=guard(()=>showOnline('groups'));
$('#openOnlineLists').onclick=guard(()=>showOnline('lists'));$('#openOnlineHistory').onclick=guard(()=>showOnline('history'));
$('#run').onclick=guard(runCode);$('#stage').onclick=guard(stage);$('#openAccount').onclick=accountModal;$('#openSettings').onclick=settingsModal;
$('#openQueue').onclick=renderQueue;$('#queueShortcut').onclick=renderQueue;$('#closeModal').onclick=closeModal;$('#modal').addEventListener('cancel',()=>modalKind='');
$('#syncButton').onclick=guard(async()=>{if(state.syncing){toast('正在同步，已缓存的题目可以正常练习。');return;}await rpc('sync');state.syncing=true;updateChrome();toast('开始同步题库，完成后会自动更新。');});
$('#toggleTest').onclick=()=>{$('#testPanel').classList.toggle('collapsed');$('#testChevron').textContent=$('#testPanel').classList.contains('collapsed')?'›':'⌄';scheduleEditorRefresh();};
$('#fillSample').onclick=()=>{if(current?.problem.samples?.length)$('#testInput').value=current.problem.samples[0].input;else toast('这道题没有可读取的样例输入。');};
$('#clearOutput').onclick=()=>$('#testOutput').textContent='';
$('#copyReference').onclick=()=>{if(!current?.reference)return;showModal('copy','YOUR WORK MATTERS','复制到我的代码？','<p class="modal-note">这会替换当前草稿中的内容。参考答案本身不会改变。</p><div class="form-actions"><button class="secondary" id="cancelCopy">取消</button><button class="primary" id="confirmCopy">替换草稿</button></div>');$('#cancelCopy').onclick=closeModal;$('#confirmCopy').onclick=guard(async()=>{await replaceDraft(current.reference,'复制参考答案前备份');await setTab('draft');closeModal();});};
$('#importCode').onclick=guard(async()=>{if(!current)return;await saveCurrent();const code=await rpc('import',{id:current.problem.id});if(code!==null){current.draft=code;dirty=false;await setTab('draft');toast('已导入为我的代码','success');}});
$('#exportCode').onclick=guard(async()=>{if(current&&await rpc('export',{id:current.problem.id,code:editor.getValue()}))toast('代码已导出','success');});
const splitter=$('#splitter');let dragging=false,dragRect,dragX,dragFrame=0;
function paintSplit(){dragFrame=0;const width=Math.max(200,Math.min(dragRect.width-330,dragX-dragRect.left));$('.statement').style.width=width+'px';}
splitter.onpointerdown=e=>{dragging=true;dragRect=$('.studio').getBoundingClientRect();dragX=e.clientX;splitter.setPointerCapture(e.pointerId);document.body.classList.add('resizing');};
splitter.onpointermove=e=>{if(!dragging)return;dragX=e.clientX;if(!dragFrame)dragFrame=requestAnimationFrame(paintSplit);};
function finishSplit(){if(!dragging)return;dragging=false;document.body.classList.remove('resizing');if(dragFrame){cancelAnimationFrame(dragFrame);paintSplit();}scheduleEditorRefresh();}
splitter.onpointerup=finishSplit;splitter.onpointercancel=finishSplit;splitter.onlostpointercapture=finishSplit;
document.addEventListener('keydown',e=>{if(e.key==='/' && !['INPUT','TEXTAREA'].includes(document.activeElement.tagName)){e.preventDefault();$('#search').focus();}if(e.key==='Escape' && !document.querySelector('dialog[open]') && document.body.classList.contains('focus-mode'))$('#focus').click();});
window.addEventListener('resize',scheduleEditorRefresh);
window.requestSafeClose=async()=>{try{await saveCurrent();window.chrome.webview.postMessage({id:'close',action:'windowCloseReady',data:{}});}catch{toast('草稿保存失败，请先导出代码再关闭。','error');}};
guard(async()=>{await refresh();const preferred=localStorage.getItem('selectedProblem')||'1606';const item=state.catalog.find(p=>p.id===preferred)||state.catalog[0];if(item)await openProblem(item.id);})().catch(()=>{});

function closeOnline(){document.body.classList.remove('online-mode');$('#openOnlineLists').classList.remove('active');$('#openOnlineHistory').classList.remove('active');$('#openOnlineGroups')?.classList.remove('active');window.dispatchEvent(new CustomEvent('boyi-online',{detail:null}));scheduleEditorRefresh();}
async function showOnline(kind){await saveCurrent();const key=typeof kind==='object'?kind.kind:kind;document.body.classList.add('online-mode');document.body.classList.remove('focus-mode');$$('[data-view]').forEach(x=>x.classList.remove('active'));$('#openOnlineLists').classList.toggle('active',key==='lists');$('#openOnlineHistory').classList.toggle('active',key==='history');window.dispatchEvent(new CustomEvent('boyi-state',{detail:{...state,settings:{...state.settings}}}));$('#openOnlineGroups')?.classList.toggle('active',key==='groups'||key==='group');window.dispatchEvent(new CustomEvent('boyi-online',{detail:kind}));}
async function replaceDraft(code,reason){
  if(!current)return;await saveCurrent();const item=current;await rpc('saveDraft',{id:item.problem.id,code,reason});if(current!==item)return;item.draft=code;dirty=false;await setTab('draft');
}
window.boyiUI={rpc,toast,closeOnline,account:accountModal,online:showOnline,current:()=>current,save:saveCurrent,replaceDraft,editorCode:()=>editor.getValue(),refresh,open:async id=>{await saveCurrent();if(!state.catalog.some(p=>p.id===id)){await rpc('ensureProblem',{id});await refresh();}closeOnline();await openProblem(id);}};
