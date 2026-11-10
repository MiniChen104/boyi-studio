// A single hardware-decoded background. No frame polling, canvas copies or
// desktop capture. Native code exposes only Wallpaper Engine's selected file.
export function installWallpaperBackground(){
  const atmosphere=document.querySelector('.atmosphere');
  if(!atmosphere)return;
  const layer=document.createElement('div');layer.className='wallpaper-layer';layer.setAttribute('aria-hidden','true');
  const video=document.createElement('video');video.muted=true;video.loop=true;video.playsInline=true;video.preload='auto';video.disablePictureInPicture=true;
  const poster=document.createElement('img');poster.alt='';
  layer.append(video,poster);atmosphere.prepend(layer);
  const reduced=window.matchMedia?.('(prefers-reduced-motion: reduce)')||{matches:false,addEventListener(){}};
  let enabled=true;
  try{enabled=localStorage.getItem('boyi-wallpaper')!=='off';}catch{}
  let busy=false,source='',kind='',note='正在连接 Wallpaper Engine…',nativeVisible=true,loaded=false;
  function setActive(value){document.body.classList.toggle('wallpaper-active',value);}
  function status(){
    const text=document.querySelector('#wallpaperStatus');
    if(text)text.textContent=enabled?note:'使用练习室默认背景。';
    const select=document.querySelector('#wallpaperMode');if(select)select.value=enabled?'follow':'plain';
  }
  function playback(){
    if(kind!=='video')return;
    if(!enabled||document.hidden||!nativeVisible||reduced.matches){video.pause();return;}
    video.play().catch(()=>{});
  }
  function ready(){loaded=true;setActive(enabled);playback();}
  video.addEventListener('loadeddata',ready);poster.addEventListener('load',ready);
  function failed(){if(!video.paused)video.pause();loaded=false;setActive(false);source='';note='当前壁纸无法播放，已恢复默认背景。';status();}
  video.addEventListener('error',failed);poster.addEventListener('error',failed);
  async function refresh(){
    if(!enabled||busy||document.hidden||!nativeVisible)return;
    busy=true;
    try{
      const state=await window.boyiUI.rpc('wallpaperState');
      if(!enabled)return;
      if(!state?.available||!['video','image'].includes(state.kind)){
        if(!video.paused)video.pause();setActive(false);loaded=false;source='';note=state?.note||'当前未连接 Wallpaper Engine，使用默认背景。';status();return;
      }
      // Reject unexpected URLs even if a future bridge returns malformed data.
      const url=new URL(state.url,location.href);
      if(url.origin!=='https://wallpaper.boyi.local'||!url.pathname.startsWith('/__wallpaper/'))throw Error('Invalid wallpaper URL');
      note=[state.title,state.note].filter(Boolean).join(' · ');status();
      if(source===url.href){setActive(loaded);playback();return;}
      source=url.href;kind=state.kind;loaded=false;
      video.pause();video.hidden=kind!=='video';poster.hidden=kind!=='image';
      if(kind==='video'){poster.removeAttribute('src');video.src=source;video.load();}
      else{video.removeAttribute('src');video.load();poster.src=source;}
    }catch{if(!video.paused)video.pause();note='暂时无法连接壁纸，显示默认背景。';setActive(false);status();}
    finally{busy=false;}
  }
  function installSetting(){
    const quality=document.querySelector('#glassQuality');
    if(!quality||document.querySelector('#wallpaperMode'))return;
    const row=document.createElement('div');row.className='setting-row';
    const label=document.createElement('label');label.htmlFor='wallpaperMode';label.textContent='界面背景';
    const select=document.createElement('select');select.id='wallpaperMode';
    for(const [value,title] of [['follow','跟随 Wallpaper Engine'],['plain','练习室默认背景']]){const option=document.createElement('option');option.value=value;option.textContent=title;select.append(option);}
    const message=document.createElement('p');message.id='wallpaperStatus';message.className='modal-small wallpaper-status';message.setAttribute('role','status');
    row.append(label,select);quality.closest('.setting-row').after(row);row.after(message);
    select.addEventListener('change',()=>{
      enabled=select.value==='follow';try{localStorage.setItem('boyi-wallpaper',enabled?'on':'off');}catch{}
      status();if(enabled)refresh();else{video.pause();setActive(false);}
    });status();
  }
  const modal=document.querySelector('#modalBody');if(modal)new MutationObserver(installSetting).observe(modal,{childList:true,subtree:true});
  document.addEventListener('visibilitychange',()=>{playback();if(!document.hidden)refresh();});
  window.addEventListener('boyi-window-visibility',e=>{nativeVisible=!!e.detail;playback();if(nativeVisible)refresh();});
  reduced.addEventListener('change',playback);
  window.addEventListener('beforeunload',()=>{clearInterval(timer);video.pause();});
  const timer=setInterval(refresh,8000);refresh();
}
