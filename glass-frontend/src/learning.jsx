import React,{useEffect,useRef,useState} from 'react';
import {createRoot} from 'react-dom/client';

const api=()=>window.boyiUI;
const rpc=(action,data={})=>api().rpc(action,data);
const date=v=>v?new Date(v).toLocaleString('zh-CN'):'';
const fail=e=>api().toast(e.message||String(e),'error');
let launch=()=>{},listContext=null,account='',sampleRun=0;
const contextKey=()=>`boyi.practiceList.${account}`;

function LearningDialog(){
  const [view,setView]=useState(null),[rows,setRows]=useState([]),[remote,setRemote]=useState(null),[selected,setSelected]=useState(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[review,setReview]=useState({status:'none',note:''}),[compare,setCompare]=useState(false),[lastKnown,setLastKnown]=useState('');
  const dialog=useRef(null),generation=useRef(0),currentView=useRef(null),returnFocus=useRef(null),reviewValue=useRef(review),savedReview=useRef(JSON.stringify(review));
  reviewValue.current=review;
  useEffect(()=>{
    launch=async next=>{
      if(currentView.current?.kind==='review'&&JSON.stringify(reviewValue.current)!==savedReview.current&&!window.confirm('复习笔记还没有保存，放弃本次修改？'))return;
      if(!currentView.current)returnFocus.current=document.activeElement;
      const token=++generation.current;currentView.current=next;setView(next);setSelected(null);setRows([]);setRemote(null);setError('');setCompare(false);setLastKnown('');setBusy(true);
      try{
        await api().save();
        if(next.kind==='submission'){const result=await rpc('submissionCode',{sid:next.row.sid,id:next.row.problemId||''});if(token===generation.current)setSelected(result);}
        else if(next.kind==='review'){const result=await rpc('review',{id:next.id});if(token===generation.current){savedReview.current=JSON.stringify(result);reviewValue.current=result;setReview(result);}}
        else if(next.kind==='reviewIndex'){const result=await rpc('reviewIndex');if(token===generation.current){setRows(result.items);setLastKnown(result.lastKnownSubmission||'');}}
        else {
          const result=await rpc(next.kind==='drafts'?'draftVersions':'localSubmissions',{id:next.id});
          if(token!==generation.current)return;
          setRows(result.items);setLastKnown(result.lastKnownSubmission||'');
          if(next.kind==='history'){
            const state=await rpc('init');
            if(token!==generation.current)return;
            if(state.loggedIn){const official=await rpc('problemHistory',{id:next.id,path:''});if(token===generation.current)setRemote(official);}
          }
        }
      }catch(e){if(token===generation.current)setError(e.message);}finally{if(token===generation.current)setBusy(false);}
    };
    const changed=()=>{generation.current++;currentView.current=null;setView(null);};window.addEventListener('boyi-account-reset',changed);
    return()=>window.removeEventListener('boyi-account-reset',changed);
  },[]);
  useEffect(()=>{if(view&&!dialog.current.open)dialog.current.showModal();},[view]);
  function close(){if(currentView.current?.kind==='review'&&JSON.stringify(reviewValue.current)!==savedReview.current&&!window.confirm('复习笔记还没有保存，放弃本次修改？'))return;generation.current++;currentView.current=null;setView(null);requestAnimationFrame(()=>returnFocus.current?.isConnected&&returnFocus.current.focus({preventScroll:true}));}
  async function task(fn){const token=++generation.current;setBusy(true);setError('');try{await fn(token);}catch(e){if(token===generation.current)setError(e.message);}finally{if(token===generation.current)setBusy(false);}}
  const loadVersion=row=>task(async token=>{const result=await rpc(view.kind==='drafts'?'draftVersion':'submissionVersion',{id:view.id,key:row.key});if(token===generation.current){setSelected(result);setCompare(false);}});
  const loadRemote=path=>task(async token=>{const result=await rpc('problemHistory',{id:view.id,path:path||''});if(token===generation.current)setRemote(previous=>path&&previous?{...result,rows:[...previous.rows,...result.rows]}:result);});
  const openRemote=row=>task(async token=>{setSelected(null);setCompare(false);const result=await rpc('submissionCode',{sid:row.sid,id:row.problemId||view.id});if(token===generation.current)setSelected(result);});
  if(!view)return null;
  const sameProblem=selected?.problemId===api().current()?.problem.id;
  const draft=sameProblem?(api().current()?.draft||''):'';
  const firstDifference=selected?selected.code.split('\n').findIndex((line,i)=>line!==draft.split('\n')[i]):-1;
  return <dialog className={'learning-dialog'+(compare?' is-comparing':'')} ref={dialog} onCancel={e=>{e.preventDefault();close();}} onClick={e=>{if(e.target!==dialog.current)return;const r=dialog.current.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)close();}} aria-labelledby="learningTitle">
    <header><div><small>你的练习记录</small><h2 id="learningTitle">{({history:'我的提交',submission:'提交代码',drafts:'草稿版本恢复',review:'复习笔记',reviewIndex:'错题与复习'})[view.kind]}{view.id?' · '+view.id:''}</h2></div><button className="secondary" onClick={close} aria-label="关闭">关闭</button></header>
    {view.id&&['history','drafts','review'].includes(view.kind)&&<nav className="learning-switcher" aria-label="学习记录分类">{[['history','我的提交','mySubmissions'],['drafts','草稿恢复','draftHistory'],['review','复习笔记','reviewNote']].map(([kind,label,id])=><button id={id} data-learning-view={kind} key={kind} aria-pressed={view.kind===kind} disabled={busy} onClick={()=>launch({kind,id:view.id})}>{label}</button>)}</nav>}
    {error&&<p className="learning-error" role="alert">{error}</p>}
    {busy&&<p role="status">正在读取…</p>}
    <div className="learning-body">
      {view.kind==='history'&&<div className="learning-tools"><button className="secondary" disabled={busy} onClick={()=>loadRemote()}>{remote?'刷新官网提交':'读取官网提交'}</button><button className="secondary" onClick={()=>rpc('officialWorkspace',{path:'/p/'+view.id}).catch(fail)}>官网提交记录 ↗</button>{lastKnown&&!rows.some(row=>row.submissionId===lastKnown)&&<button className="secondary" disabled={busy} onClick={()=>openRemote({sid:lastKnown,problemId:view.id})}>本题最近提交 #{lastKnown}</button>}<span>本地保存的代码可离线查看；官网代码在打开时读取并缓存。</span></div>}
      {view.kind==='drafts'&&<p>自动记录修改前的草稿，连续输入按分钟保留恢复点；导入和替换前单独备份。恢复操作也会保留当前草稿。</p>}
      {['history','drafts'].includes(view.kind)&&<div className="version-list">{view.kind==='history'&&!!rows.length&&<h3>本地保存</h3>}{rows.map(row=><button className="version-row" key={row.key} disabled={busy} onClick={()=>loadVersion(row)}><strong>{row.submissionId?'#'+row.submissionId:row.source}</strong><span>{row.result}</span><small>记录于 {date(row.createdAt)} · {row.source}</small></button>)}{!rows.length&&!busy&&!remote&&<p>还没有本地版本。{view.kind==='history'?'连接账号后可读取官网提交。':''}</p>}</div>}
      {remote&&<section><h3>官网记录</h3><p>{remote.note||'仅显示当前账号的提交；每次读取一页。'}</p>{remote.rows.map((row,i)=><button className="version-row" key={row.sid||i} disabled={busy||!row.sid} onClick={()=>openRemote(row)}>{row.cells.join(' · ')}<span>查看代码 →</span></button>)}{!remote.rows.length&&!busy&&<p>官网本次未返回提交记录。</p>}{remote.next&&<button className="secondary" disabled={busy} onClick={()=>loadRemote(remote.next)}>下一页</button>}</section>}
      {selected&&<section className="code-version"><div className="learning-tools"><strong>{selected.submissionId?'提交 #'+selected.submissionId:'草稿恢复点'} · {selected.source}</strong><button className="secondary" disabled={!sameProblem} onClick={()=>setCompare(!compare)}>{compare?'只看此版本':'与当前草稿比较'}</button><button className="secondary" disabled={busy||!selected.problemId} onClick={()=>task(async token=>{if(!api().current()||api().current().problem.id!==selected.problemId)await api().open(selected.problemId);if(token!==generation.current)return;if(!window.confirm('将此版本复制到当前草稿？现有草稿会保留为恢复点。'))return;await api().replaceDraft(selected.code,'历史版本恢复前备份');close();api().toast('已恢复到我的代码，原草稿已备份。','success');})}>复制到我的代码</button></div>
      {compare&&<p>{selected.code===draft?'与当前草稿完全相同':`从第 ${(firstDifference<0?selected.code.split('\n').length:firstDifference)+1} 行开始不同。左侧是历史版本，右侧是当前草稿。`}</p>}
      <div className={compare?'code-comparison':''}><pre tabIndex="0">{selected.code}</pre>{compare&&<pre tabIndex="0">{draft}</pre>}</div></section>}
      {view.kind==='submission'&&<button className="secondary" onClick={()=>rpc('officialWorkspace',{path:'/solution/source?id='+view.row.sid}).catch(fail)}>在官网查看此提交 ↗</button>}
      {view.kind==='review'&&<><label className="review-label">复习状态<select disabled={busy} value={review.status} onChange={e=>setReview({...review,status:e.target.value})}><option value="none">未标记</option><option value="review">待复习</option><option value="mastered">已掌握</option></select></label><label className="review-label">我的思路与错误原因<textarea disabled={busy} maxLength="20000" rows="12" value={review.note} onChange={e=>setReview({...review,note:e.target.value})} placeholder="记录为什么出错、关键思路、下次需要注意的地方…"/></label><button className="primary" disabled={busy} onClick={()=>task(async token=>{await rpc('saveReview',{id:view.id,...review});if(token===generation.current){savedReview.current=JSON.stringify(reviewValue.current);close();api().toast('复习笔记已保存','success');}})}>保存笔记</button></>}
      {view.kind==='reviewIndex'&&<>{rows.map(row=><div className="review-row" key={row.id}><button className="secondary" onClick={()=>{close();api().open(row.id).catch(fail);}}>{row.id} · {row.title}</button><span>{row.review.status==='review'?'待复习':row.review.status==='mastered'?'已掌握':row.record.status==='failed'?'尝试未通过':'有笔记'}</span><button className="secondary" onClick={()=>launch({kind:'review',id:row.id})}>笔记</button></div>)}{!rows.length&&!busy&&<p>暂时没有错题或复习笔记。可以在做题时点击“复习笔记”记录。</p>}</>}
    </div>
  </dialog>;
}

export function installLearning(){
  const host=document.createElement('div');document.body.append(host);createRoot(host).render(<LearningDialog/>);
  function button(parent,id,label,handler){const b=document.createElement('button');b.id=id;b.className='secondary';b.textContent=label;b.onclick=()=>Promise.resolve(handler()).catch(fail);parent.append(b);return b;}
  button(document.querySelector('.editor-toolbar'),'learningTools','学习记录',()=>api().current()&&launch({kind:'history',id:api().current().problem.id}));
  const reviewButton=button(document.querySelector('.nav-list'),'reviewIndex','错题与复习',()=>launch({kind:'reviewIndex'}));reviewButton.className='nav-item';reviewButton.innerHTML='<svg><use href="#i-code"></use></svg>错题与复习';
  const sample=button(document.querySelector('.test-header'),'runAllSamples','全部样例',async()=>{
    const item=api().current();if(!item)return;const samples=item.problem.samples||[];if(!samples.length){api().toast('此题没有可读取的样例。');return;}
    await api().save();window.boyiDrawers?.openTest();const code=api().editorCode(),token=++sampleRun;sample.disabled=true;const result=[];let passed=0;
    const out=document.querySelector('#testOutput'),status=document.querySelector('#testStatus');document.querySelector('#testPanel').classList.remove('collapsed');
    try{
      for(let i=0;i<samples.length;i++){
        if(token!==sampleRun||api().current()!==item)break;status.textContent=`正在测试 ${i+1}/${samples.length}…`;
        const run=await rpc('run',{id:item.problem.id,code,input:samples[i].input});if(token!==sampleRun||api().current()!==item)break;
        const normalize=v=>String(v||'').replace(/\r/g,'').trimEnd(),actual=normalize(run.output),expected=normalize(samples[i].output),ok=run.ok&&actual===expected;if(ok)passed++;
        let diff='';if(!ok){const a=actual.split('\n'),b=expected.split('\n');let n=0;while(n<Math.max(a.length,b.length)&&a[n]===b[n])n++;diff=`\n首处差异：第 ${n+1} 行\n期望：${b[n]??'（无此行）'}\n实际：${a[n]??'（无此行）'}`;}
        result.push(`样例 ${i+1} · ${ok?'通过':run.ok?'输出不同':run.kind}${diff}${run.error?'\n'+run.error:''}`);out.textContent=result.join('\n\n');
        if(!run.ok&&run.kind!=='运行完成')break;
      }
      if(token===sampleRun&&api().current()===item)status.textContent=`通过 ${passed}/${samples.length} 组公开样例 · 不代表官网评测通过`;
    }catch(e){if(token===sampleRun&&api().current()===item){status.textContent='测试已停止 · '+e.message;out.textContent=result.join('\n\n')+'\n'+e.message;}throw e;}finally{sample.disabled=false;}
  });
  const nav=document.createElement('div');nav.className='practice-navigation hidden';document.querySelector('.problem-heading').before(nav);
  function renderNavigation(){
    const id=api().current()?.problem.id;nav.replaceChildren();const index=listContext?.items?.findIndex(x=>x.id===id)??-1;nav.classList.toggle('hidden',index<0);if(index<0)return;
    const label=document.createElement('span');label.textContent=`${listContext.title} · ${index+1}/${listContext.items.length}`;nav.append(label);
    button(nav,'previousListProblem','上一题',()=>api().open(listContext.items[index-1].id)).disabled=index===0;
    button(nav,'nextListProblem','下一题',()=>api().open(listContext.items[index+1].id)).disabled=index===listContext.items.length-1;
    button(nav,'returnToList','返回题单',()=>api().online({kind:'list',path:listContext.path}));
    listContext.lastId=id;localStorage.setItem(contextKey(),JSON.stringify(listContext));
  }
  window.addEventListener('boyi-problem',()=>{sampleRun++;renderNavigation();});
  window.addEventListener('boyi-state',e=>{if(account!==e.detail.account){account=e.detail.account||'';sampleRun++;window.dispatchEvent(new Event('boyi-account-reset'));try{listContext=JSON.parse(localStorage.getItem(contextKey())||'null');}catch{listContext=null;}renderNavigation();}});
  window.boyiLearning={submission:row=>launch({kind:'submission',row}),practiceList:async(data,id)=>{listContext=data;await api().open(id);renderNavigation();},resume:()=>listContext};
}
