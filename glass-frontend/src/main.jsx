import React,{useEffect,useRef,useState} from 'react';
import {createRoot} from 'react-dom/client';
import LiquidGlass from 'liquid-glass-react';
import {installDrawers} from './drawers.js';
import {installLearning} from './learning.jsx';
import {installWallpaperBackground} from './wallpaper.js';

const bridge=(action,data={})=>window.boyiUI.rpc(action,data);
const emitError=e=>window.boyiUI.toast(e.message||String(e),'error');

const stillMouse={x:0,y:0};
function OpticalSurface({parent,theme}){
  const [size,setSize]=useState({w:0,h:0});
  const opticalRef=useRef(null);
  useEffect(()=>{
    const filter=opticalRef.current?.querySelector('filter'),warp=opticalRef.current?.querySelector('.glass__warp');
    if(filter&&warp){
      // Apply the library's displacement map to the backdrop itself. Filtering
      // a transparent child creates an isolated backdrop root in Chromium.
      warp.style.filter='none';
      opticalRef.current.style.setProperty('--optical-backdrop',`url("#${filter.id}")`);
    }
  },[size.w,size.h]);
  useEffect(()=>{
    let timer;
    const update=()=>{const r=parent.getBoundingClientRect();const w=Math.round(r.width),h=Math.round(r.height);setSize(prev=>prev.w===w&&prev.h===h?prev:{w,h});};
    const obs=new ResizeObserver(()=>{clearTimeout(timer);timer=setTimeout(update,100);});obs.observe(parent);update();
    return()=>{clearTimeout(timer);obs.disconnect();};
  },[parent]);
  if(!size.w||!size.h)return null;
  return <div ref={opticalRef} className="optical-container"><LiquidGlass key={`${size.w}-${size.h}`} className="liquid-surface" displacementScale={28} blurAmount={(100-theme.glass)/2400} saturation={112} aberrationIntensity={.7} elasticity={0} globalMousePos={stillMouse} mouseOffset={stillMouse} cornerRadius={24} padding="0" mode="standard" overLight={!theme.dark} style={{position:'absolute',top:'50%',left:'50%'}}><div style={{width:size.w-2,height:size.h-2}}/></LiquidGlass></div>;
}
function Surface({parent}){
  const [theme,setTheme]=useState({dark:document.body.classList.contains('dark'),glass:78,optical:false});
  useEffect(()=>{
    const state=e=>{const next={dark:e.detail.settings.theme==='dark',glass:e.detail.settings.glass,optical:e.detail.settings.glassQuality==='optical'};setTheme(prev=>prev.dark===next.dark&&prev.glass===next.glass&&prev.optical===next.optical?prev:next);};
    window.addEventListener('boyi-state',state);return()=>window.removeEventListener('boyi-state',state);
  },[]);
  return theme.optical?<OpticalSurface parent={parent} theme={theme}/>:<div className="smooth-glass-surface"/>;
}

// Reuse the existing statement and splitter so the content, scroll position and
// width stay in sync with the normal workspace.
function installFocusStatementToggle(){
  const focus=document.querySelector('#focus'), statement=document.querySelector('#statementBody');
  if(!focus||!statement)return;
  const button=document.createElement('button');
  button.type='button';button.className='focus-statement-toggle secondary';
  button.setAttribute('aria-controls','statementBody');button.setAttribute('aria-expanded','false');
  button.textContent='查看题目';focus.before(button);
  const sync=()=>{
    const open=document.body.classList.contains('focus-mode')&&document.body.classList.contains('focus-statement-open');
    button.setAttribute('aria-expanded',String(open));button.textContent=open?'收起题目':'查看题目';
  };
  button.addEventListener('click',()=>{
    document.body.classList.toggle('focus-statement-open');sync();
    window.dispatchEvent(new Event('resize'));
  });
  new MutationObserver(()=>{
    if(!document.body.classList.contains('focus-mode')&&document.body.classList.contains('focus-statement-open'))document.body.classList.remove('focus-statement-open');
    sync();
  }).observe(document.body,{attributes:true,attributeFilter:['class']});
}

function OnlinePanel(){
  const [view,setView]=useState(null),[app,setApp]=useState(null),[data,setData]=useState(null),[busy,setBusy]=useState(false),[error,setError]=useState('');
  const request=useRef(0),activeView=useRef(null),listPath=useRef(''),indexPath=useRef(''),lastAccount=useRef(null),groupPath=useRef(''),groupIndex=useRef('');
  useEffect(()=>{
    const open=e=>{request.current++;const kind=typeof e.detail==='object'?e.detail?.kind:e.detail;activeView.current=kind;setView(kind);setData(null);setError('');if(e.detail?.path){const saved=window.boyiLearning?.resume();if(kind==='list'&&saved?.path===e.detail.path){listPath.current=e.detail.path;setData(saved);setBusy(false);}else load(kind,e.detail.path);}};
    const update=e=>{
      if(!e.detail.loggedIn || (lastAccount.current && lastAccount.current!==e.detail.account)){
        request.current++;setData(null);setBusy(false);setError('');
        if(activeView.current==='list'){activeView.current='lists';setView('lists');}if(activeView.current==='group'){activeView.current='groups';setView('groups');}listPath.current='';indexPath.current='';groupPath.current='';groupIndex.current='';
      }
      lastAccount.current=e.detail.account;setApp(e.detail);
    };
    window.addEventListener('boyi-online',open);window.addEventListener('boyi-state',update);
    bridge('init').then(setApp).catch(emitError);
    return()=>{window.removeEventListener('boyi-online',open);window.removeEventListener('boyi-state',update);};
  },[]);
  useEffect(()=>{if(view && !['list','group'].includes(view) && app?.loggedIn)load(view);},[view,app?.loggedIn,app?.account]);
  async function load(kind,path='',append=false){
    if(kind==='list'){if(path&&!append)listPath.current=path;else if(!path)path=listPath.current;}
    if(kind==='group'){if(path)groupPath.current=path;else path=groupPath.current;}
    if(kind==='groups'){if(path&&!append)groupIndex.current=path;else if(!path)path=groupIndex.current;}
    if(kind==='lists'){if(path&&!append)indexPath.current=path;else if(!path)path=indexPath.current;}
    const token=++request.current;setBusy(true);setError('');
    try{
      const next=await bridge(kind==='lists'?'onlineLists':kind==='history'?'onlineHistory':kind==='groups'?'onlineGroups':kind==='group'?'onlineGroup':'onlineList',{path});
      if(token!==request.current)return;
      setData(prev=>append&&prev?{...next,items:[...new Map([...(prev.items||[]),...(next.items||[])].map(x=>[x.id||x.path,x])).values()],rows:[...(prev.rows||[]),...(next.rows||[])]}:next);
      if(['list','group','lists','groups'].includes(kind)){activeView.current=kind;setView(kind);}
    }catch(e){if(token===request.current)setError(e.message);}
    finally{if(token===request.current)setBusy(false);}
  }
  // A selected list has already loaded with its specific path.
  if(!view)return null;
  const isHistory=view==='history',isGroups=view==='groups',isGroup=view==='group';
  const passed=app?.acceptedCount??app?.catalog?.filter(p=>p.record.status==='accepted').length??0;
  return <div className="online-content">
    <header className="online-header"><div><div className="eyebrow">CONNECTED TO YOUR JOURNEY</div><h1>{isHistory?'每一次尝试，都算数。':isGroups?'一起练习，彼此进步。':isGroup?(data?.title||'小组详情'):view==='list'?(data?.title||'题单详情'):'沿着题单，慢慢进步。'}</h1><p>来自东方博宜账号的数据，在你熟悉的空间里继续。</p></div><button className="secondary" onClick={()=>window.boyiUI.closeOnline()}>返回编辑器 ↗</button></header>
    <div className="account-overview"><div><span className="avatar">{(app?.account||'B').slice(0,1).toUpperCase()}</span><span><strong>{app?.account||'连接你的账号'}</strong><small>{app?.loggedIn?'官网账号已连接':'当前为本地模式'}</small></span></div><div><strong>{passed}</strong><small>{app?.progressSyncing?'上次记录 · 正在更新':'已记录通过题目'}</small></div><div><strong>{app?.catalog?.filter(p=>p.draft).length||0}</strong><small>本地练习</small></div><button className="secondary" disabled={!app?.loggedIn||app?.progressSyncing} onClick={()=>bridge('syncProgress').catch(emitError)}>{app?.progressSyncing?'正在同步…':'同步官网进度'}</button></div>
    <div className="sync-details"><span>通过进度：{app?.progressSyncedAt?new Date(app.progressSyncedAt).toLocaleString('zh-CN'):'尚未同步'}</span><span>提交列表：{isHistory?(busy?'正在读取':data?(data.scope==='recent'?'仅最近记录':'当前已加载页面'):'尚未读取'):'进入提交记录时读取'}</span><span>历史源码：打开时按需读取；本地快照可离线查看</span></div>
    <div className="online-subhead"><span>{app?.progressMessage||'登录后同步你原有的做题进度'}</span>{app?.progressSyncedAt&&<span>上次同步 {new Date(app.progressSyncedAt).toLocaleString('zh-CN')}</span>}</div>
    {!app?.loggedIn?<div className="online-empty"><div className="empty-orbit">↗</div><h2>你的进度，跟着账号走。</h2><p>连接东方博宜账号，即可查看题单和提交记录。<br/>已有通过记录会同步到题库，不必从头开始。</p><button className="primary" onClick={()=>window.boyiUI.account()}>连接我的账号</button></div>:<>
      <div className="online-tools"><button className="secondary" onClick={()=>bridge('officialWorkspace',{path:isHistory?'/status/index?SolutionSearch[user_id]='+encodeURIComponent(app.account):isGroup?(data?.path||'/group/index'):isGroups?'/group/index':view==='list'?listPath.current:'/lists/index'}).catch(emitError)}>官网完整功能 ↗</button><span>{isGroups?'当前账号可访问的小组':isGroup?'小组功能':isHistory?(data?.scope==='recent'?'我的最近提交':'我的提交记录'):view==='list'?'题单中的题目':'账号可访问的题单'}</span>{view==='list'?<button className="secondary" onClick={()=>{activeView.current='lists';setView('lists');setData(null);}}>← 返回题单</button>:<button className="secondary" disabled={busy} onClick={()=>load(view)}>刷新</button>}</div>
      {isHistory&&data?.note&&<p className="modal-note">{data.note}</p>}
      {error&&<div className="online-error" role="alert">{error}<button disabled={busy} onClick={()=>load(view)}>重试</button></div>}
      {busy&&!data&&<div className="online-empty" role="status">正在读取官网数据…</div>}
      {(view==='lists'||isGroups)&&!!data?.filters?.length&&<div className="online-filter-tabs">{data.filters.map(f=><button className="secondary" key={f.path} disabled={busy} onClick={()=>load(isGroups?'groups':'lists',f.path)}>{f.title}</button>)}</div>}
      {!isGroup&&!busy&&!error&&data&&!((data.items?.length||0)+(data.rows?.length||0))&&<div className="online-empty">当前页面没有可显示的记录。</div>}
      {(view==='lists'||isGroups)&&<div className="online-cards">{data?.items?.map((item,i)=><button className="list-card" key={item.path} disabled={busy} onClick={()=>load(isGroups?'group':'list',item.path)}><span className="card-index">{String(i+1).padStart(2,'0')}</span><h3>{item.title}</h3><p>{item.description||(isGroups?'查看小组内容与完整功能。':'进入题单，查看你的完成进度。')}</p><span className="card-arrow">↗</span></button>)}</div>}
      {(view==='lists'||isGroups)&&<DirectoryTools kind={view} account={app.account} load={load} busy={busy}/>}
      {view==='list'&&<ListPractice data={data} path={listPath.current} busy={busy} loadMore={()=>load(view,data.next,true)}><div className="online-problems">{data?.items?.map(item=><button key={item.id} disabled={!item.available} onClick={()=>window.boyiLearning.practiceList({...data,path:listPath.current,scrollTop:document.querySelector('.online-content')?.scrollTop||0},item.id).catch(emitError)}><span className="problem-id">{item.id}</span><strong>{item.title}</strong><span className={item.accepted?'done':''}>{item.accepted?'✓ 已通过':item.available?'开始练习 ↗':'在线加载题目'}</span></button>)}</div></ListPractice>}
      {isHistory&&!!data?.rows?.length&&<div className="history-scroll"><table className="history-table"><thead><tr>{data.headers.map((h,i)=><th key={i}>{h}</th>)}<th>我的代码</th></tr></thead><tbody>{data.rows.map((r,i)=><tr key={i}>{r.cells.map((c,j)=><td key={j} className={/通过|Accepted/.test(c)?'done':''}>{c}</td>)}<td><button className="secondary" disabled={!r.sid} onClick={()=>window.boyiLearning.submission(r)}>查看代码</button></td></tr>)}</tbody></table></div>}
      {isGroup&&<><p>{data?.note}</p><button className="secondary" disabled={busy} onClick={()=>load('groups')}>返回小组</button><div className="online-cards">{data?.links?.map(link=><button className="list-card" key={link.path} onClick={()=>link.path.startsWith('/lists/view?')?load('list',link.path):bridge('officialWorkspace',{path:link.path.startsWith('/contest/')?data.path:link.path}).catch(emitError)}>{link.title} ↗</button>)}</div></>}
      {data?.next&&<button className="secondary online-more" disabled={busy} onClick={()=>load(view,data.next,true)}>{busy?'正在加载…':'加载下一页'}</button>}
    </>}
    <footer className="online-footer">东方博宜提供题目与评测 · 练习室保存你的本地创作</footer>
  </div>;
}

function DirectoryTools({kind,account,load,busy}){
  const [query,setQuery]=useState('');
  useEffect(()=>{setQuery('');},[kind,account]);
  useEffect(()=>{document.querySelectorAll('.online-cards .list-card').forEach(card=>{card.hidden=!!query&&!card.textContent.toLowerCase().includes(query.toLowerCase());});},[query,kind,busy]);
  return <div className="learning-tools"><input aria-label="搜索已加载目录" placeholder="筛选已加载的名称" value={query} onChange={e=>setQuery(e.target.value)}/>{kind==='groups'&&<button className="secondary" disabled={busy} onClick={()=>load('groups','/group/index?name='+encodeURIComponent(query))}>搜索官网小组</button>}<span>创建、编辑、加入与成员管理见“官网完整功能”。</span></div>;
}

function ListPractice({data,path,busy,loadMore,children}){
  const [query,setQuery]=useState(''),[todo,setTodo]=useState(false);
  useEffect(()=>{setQuery('');setTodo(false);const saved=window.boyiLearning?.resume();if(saved?.path===path)requestAnimationFrame(()=>{const root=document.querySelector('.online-content');if(root)root.scrollTop=saved.scrollTop||0;});},[path]);
  const resume=window.boyiLearning?.resume(),passed=data?.items?.filter(x=>x.accepted).length||0;
  const rows=React.Children.toArray(children.props.children).filter(child=>{
    const item=data?.items?.find(x=>x.id===child.key?.replace(/^\.\$/,''));
    return !item||(!todo||!item.accepted)&&(!query||`${item.id} ${item.title}`.toLowerCase().includes(query.toLowerCase()));
  });
  return <><div className="learning-tools"><input aria-label="搜索已加载题单题目" placeholder="搜索已加载的题号 / 标题" value={query} onChange={e=>setQuery(e.target.value)}/><label><input type="checkbox" checked={todo} onChange={e=>setTodo(e.target.checked)}/> 只看未完成</label><span>已加载 {data?.items?.length||0} 题 · 通过 {passed} 题{data?.next?' · 还有下一页':''}</span>{data?.next&&<button className="secondary" disabled={busy} onClick={loadMore}>加载更多题目</button>}{resume?.path===path&&<button className="secondary" onClick={()=>window.boyiUI.open(resume.lastId).catch(emitError)}>继续上次练习</button>}</div>{React.cloneElement(children,{},rows)}{!rows.length&&<p>当前已加载题目没有匹配项。</p>}</>;
}

for(const selector of ['.sidebar','.library','.studio']){
  const parent=document.querySelector(selector);if(!parent)continue;
  const host=document.createElement('div');host.className='liquid-host';host.setAttribute('aria-hidden','true');parent.prepend(host);parent.classList.add('liquid-panel');createRoot(host).render(<Surface parent={parent}/>);
}
createRoot(document.querySelector('#onlineRoot')).render(<OnlinePanel/>);
installFocusStatementToggle();
installWallpaperBackground();
installLearning();
installDrawers();
document.documentElement.dataset.glassEngine='composited';
