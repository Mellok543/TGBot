import {useEffect,useState} from 'react';
type Profile={telegramId:number;name:string|null;username:string|null;plan:'FREE'|'PRO';proUntil:string|null;channelLimit:number;commissionPercent:number;channels:number;purchases:number;sales:number;staffRole:string|null};
const api=import.meta.env.VITE_API_URL||'https://api.reklio.site';
export default function WebAccount(){
 const [profile,setProfile]=useState<Profile|null>(null);
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
   <nav className="lp-account-navigation" aria-label="Разделы личного кабинета"><a className="active" href="/account" aria-current="page">◉ Профиль</a><a href="https://t.me/ReklioBot">▣ Мои каналы ↗</a><a href="https://t.me/ReklioBot">▤ Заказы ↗</a><a href="https://t.me/ReklioBot">◇ Финансы ↗</a>{profile.staffRole&&<a href="https://t.me/ReklioBot">⚙ Администрирование ↗</a>}</nav>
   <div className="lp-account-content">
    <div className="lp-account-panel lp-account-identity"><span className="lp-account-avatar">{(profile.name||profile.username||'T').slice(0,1).toUpperCase()}</span><div><h2>{profile.name||'Пользователь Telegram'}</h2><p>{profile.username?'@'+profile.username:'Аккаунт Telegram'} · ID {profile.telegramId}</p></div><span className="lp-account-plan">{profile.plan}</span></div>
    <div className="lp-account-stats"><div><strong>{profile.channels}</strong><span>Мои каналы</span></div><div><strong>{profile.purchases}</strong><span>Покупки</span></div><div><strong>{profile.sales}</strong><span>Продажи</span></div></div>
    <div className="lp-account-panel"><h2>Тариф {profile.plan}</h2><p>Подключено каналов: {profile.channels} из {profile.channelLimit}. Комиссия платформы: {profile.commissionPercent}%.</p>{profile.proUntil&&<p>PRO до {new Date(profile.proUntil).toLocaleDateString('ru-RU')}</p>}<a className="lp-account-textlink" href="/pricing">Посмотреть тарифы →</a></div>
    <div className="lp-account-panel"><h2>Управление рекламой</h2><p>Пока управление каналами и заказами доступно в Mini App. Следующим этапом перенесём эти разделы на сайт с сохранением тех же данных.</p><a className="lp-account-primary" href="https://t.me/ReklioBot">Открыть Mini App ↗</a></div>
   </div>
  </div>}
 </section>;
}
