import {useEffect,useState} from 'react';
type MyChannel={id:number;title:string;listed:boolean;price:number;category:string;alias:string|null};
type Profile={telegramId:number;name:string|null;username:string|null;plan:'FREE'|'PRO';proUntil:string|null;channelLimit:number;commissionPercent:number;channels:number;purchases:number;sales:number;staffRole:string|null};
const api=import.meta.env.VITE_API_URL||'https://api.reklio.site';
export default function WebAccount(){
 const [profile,setProfile]=useState<Profile|null>(null);
 const [channels,setChannels]=useState<MyChannel[]>([]);
 const [channelsStatus,setChannelsStatus]=useState<'idle'|'loading'|'ready'|'error'>('idle');
 const [pendingChannel,setPendingChannel]=useState<number|null>(null);
 const [channelMessage,setChannelMessage]=useState('');
 const section=window.location.pathname==='/account/channels'?'channels':'profile';
 useEffect(()=>{if(section!=='channels')return;const controller=new AbortController();setChannelsStatus('loading');fetch(api+'/api/web/channels',{credentials:'include',signal:controller.signal}).then(async r=>{if(!r.ok)throw new Error();setChannels(await r.json());setChannelsStatus('ready')}).catch(()=>{if(!controller.signal.aborted)setChannelsStatus('error')});return()=>controller.abort()},[section]);
 const toggleListing=async (channel:MyChannel)=>{setPendingChannel(channel.id);setChannelMessage('');try{const r=await fetch(api+'/api/web/channels/'+channel.id+'/listing',{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({listed:!channel.listed})});if(!r.ok)throw new Error();setChannels(prev=>prev.map(c=>c.id===channel.id?{...c,listed:!c.listed}:c));}catch{setChannelMessage('Не удалось обновить видимость канала. Повторите попытку.')}finally{setPendingChannel(null)}};
 const [status,setStatus]=useState<'loading'|'ready'|'guest'|'error'>('loading');
 useEffect(()=>{const c=new AbortController();fetch(api+'/api/web/profile',{credentials:'include',signal:c.signal})
 .then(async r=>{if(r.status===401){setStatus('guest');return;}if(!r.ok)throw new Error('Не удалось загрузить профиль');setProfile(await r.json());setStatus('ready');})
 .catch(()=>{if(!c.signal.aborted)setStatus('error');});return()=>c.abort()},[]);
 return <section className="lp-account">
  <div className="lp-account-heading"><span className="lp-kicker">ЛИЧНЫЙ КАБИНЕТ</span><h1>Мой Reklio</h1><p>Один аккаунт для сайта и Telegram Mini App.</p></div>
  {status==='loading'&&<div className="lp-account-panel">Загружаем данные аккаунта…</div>}
  {status==='error'&&<div className="lp-account-panel">Не удалось загрузить профиль. Обновите страницу и повторите попытку.</div>}
  {status==='guest'&&<div className="lp-account-panel"><h2>Войдите через Telegram</h2><p>Для доступа к личному кабинету нужна авторизация.</p><a className="lp-account-primary" href={api+'/api/auth/login'}>Войти через Telegram ↗</a></div>}
  {profile&&status==='ready'&&<div className="lp-account-layout">
   <nav className="lp-account-navigation" aria-label="Разделы личного кабинета"><a className={section==='profile'?'active':''} href="/account" aria-current={section==='profile'?'page':undefined}>◉ Профиль</a><a className={section==='channels'?'active':''} href="/account/channels" aria-current={section==='channels'?'page':undefined}>▣ Мои каналы</a><a href="https://t.me/ReklioBot">▤ Заказы ↗</a><a href="https://t.me/ReklioBot">◇ Финансы ↗</a>{profile.staffRole&&<a href="https://t.me/ReklioBot">⚙ Администрирование ↗</a>}</nav>
   <div className="lp-account-content">
    {section==='channels' ? <><div className="lp-account-panel"><h2>Мои каналы</h2><p>Управляйте видимостью ваших каналов в каталоге. Тарифы и рекламные предложения пока настраиваются в Mini App.</p><a className="lp-account-textlink" href="https://t.me/ReklioBot">Подключить канал через Telegram ↗</a></div>{channelsStatus==='loading'&&<div className="lp-account-panel">Загружаем каналы…</div>}{channelsStatus==='error'&&<div className="lp-account-panel">Не удалось загрузить каналы. Обновите страницу.</div>}{channelsStatus==='ready'&&channels.length===0&&<div className="lp-account-panel">У вас пока нет подключённых каналов.</div>}{channelMessage&&<div className="lp-account-panel" role="alert">{channelMessage}</div>}{channels.map(channel=><article className="lp-account-panel lp-account-channel" key={channel.id}><div><h2>{channel.title}</h2><p>{channel.category||'Категория не указана'} · {channel.price.toLocaleString('ru-RU')} ₽</p><span className={channel.listed?'lp-account-status listed':'lp-account-status'}>{channel.listed?'В каталоге':'Скрыт из каталога'}</span></div><div className="lp-account-channel-actions"><button type="button" disabled={pendingChannel===channel.id} onClick={()=>toggleListing(channel)}>{pendingChannel===channel.id?'Сохранение…':channel.listed?'Скрыть':'Опубликовать'}</button>{channel.alias&&<a href={'/ad/'+channel.alias}>Публичная ссылка ↗</a>}<a href="https://t.me/ReklioBot">Настроить в Telegram ↗</a></div></article>)}</> : <>
    <div className="lp-account-panel lp-account-identity"><span className="lp-account-avatar">{(profile.name||profile.username||'T').slice(0,1).toUpperCase()}</span><div><h2>{profile.name||'Пользователь Telegram'}</h2><p>{profile.username?'@'+profile.username:'Аккаунт Telegram'} · ID {profile.telegramId}</p></div><span className="lp-account-plan">{profile.plan}</span></div>
    <div className="lp-account-stats"><div><strong>{profile.channels}</strong><span>Мои каналы</span></div><div><strong>{profile.purchases}</strong><span>Покупки</span></div><div><strong>{profile.sales}</strong><span>Продажи</span></div></div>
    <div className="lp-account-panel"><h2>Тариф {profile.plan}</h2><p>Подключено каналов: {profile.channels} из {profile.channelLimit}. Комиссия платформы: {profile.commissionPercent}%.</p>{profile.proUntil&&<p>PRO до {new Date(profile.proUntil).toLocaleDateString('ru-RU')}</p>}<a className="lp-account-textlink" href="/pricing">Посмотреть тарифы →</a></div>
    <div className="lp-account-panel"><h2>Управление рекламой</h2><p>Пока управление каналами и заказами доступно в Mini App. Следующим этапом перенесём эти разделы на сайт с сохранением тех же данных.</p><a className="lp-account-primary" href="https://t.me/ReklioBot">Открыть Mini App ↗</a></div>
    </>}
   </div>
  </div>}
 </section>;
}
