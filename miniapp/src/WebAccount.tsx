import {useEffect,useState} from 'react';
import {Feedback} from './app/Feedback';
import {SiteAdmin,SiteBooking,SiteCatalog,SiteChannels,SiteOrders,SitePlan} from './app/SiteSections';
const orderStatusNames:Record<string,string>={Draft:'Черновик',Pending:'Ожидает подтверждения',PendingPaid:'Ожидает повторного подтверждения',AwaitingPayment:'Ожидает оплаты',Scheduled:'Запланировано',Publishing:'Публикуется',Published:'Опубликовано',Deleted:'Размещение завершено',DeleteFailed:'Ошибка удаления',PaidReschedule:'Нужно выбрать новое время',Rejected:'Отклонено',Disputed:'Открыт спор',DisputeResolved:'Спор рассмотрен',Cancelled:'Отменено',Expired:'Истекло',EarlyDeletionPending:'Пост удалён раньше срока',PublishFailed:'Не опубликовано',Failed:'Ошибка'};
const statusName=(status:string)=>orderStatusNames[status]||status;
type WebOrder={id:number;channel:string;price:number;status:string;createdAt:string;isOwner:boolean};
type Profile={telegramId:number;name:string|null;username:string|null;plan:'FREE'|'PRO';proUntil:string|null;channelLimit:number;channels:number;purchases:number;sales:number;staffRole:string|null};
const api=import.meta.env.VITE_API_URL||'https://api.reklio.site';
const rub=(v:number)=>v.toLocaleString('ru-RU',{minimumFractionDigits:0,maximumFractionDigits:2})+' ₽';
export default function WebAccount(){
 const [profile,setProfile]=useState<Profile|null>(null);
 const [dashboardOrders,setDashboardOrders]=useState<WebOrder[]>([]);
 const [dashboardStatus,setDashboardStatus]=useState<'loading'|'ready'|'partial'>('loading');
 const [status,setStatus]=useState<'loading'|'ready'|'guest'|'error'>('loading');
 const path=window.location.pathname;
 const bookingChannel=path.match(/^\/account\/order\/(-?\d+)$/)?.[1];
 const editChannel=path.match(/^\/account\/channels\/(-?\d+)$/)?.[1];
 const section=bookingChannel?'order':editChannel||path==='/account/channels'?'channels':path==='/account/catalog'?'catalog':path==='/account/orders'?'orders':path==='/account/plan'||path==='/account/finance'?'plan':path==='/account/admin'?'admin':'profile';
 useEffect(()=>{if(section!=='profile')return;const controller=new AbortController();setDashboardStatus('loading');fetch(api+'/api/web/orders',{credentials:'include',signal:controller.signal}).then(r=>{if(!r.ok)throw new Error();return r.json() as Promise<WebOrder[]>}).then(value=>{setDashboardOrders(value);setDashboardStatus('ready')}).catch(()=>{if(!controller.signal.aborted)setDashboardStatus('partial')});return()=>controller.abort()},[section]);
 useEffect(()=>{const c=new AbortController();fetch(api+'/api/web/profile',{credentials:'include',signal:c.signal})
 .then(async r=>{if(r.status===401){setStatus('guest');return;}if(!r.ok)throw new Error('Не удалось загрузить профиль');setProfile(await r.json());setStatus('ready');})
 .catch(()=>{if(!c.signal.aborted)setStatus('error');});return()=>c.abort()},[]);
 const navLink=(name:string,href:string,label:string)=><a className={section===name?'active':''} href={href} aria-current={section===name?'page':undefined}>{label}</a>;
 return <section className="lp-account">
  <div className="lp-account-heading"><span className="lp-kicker">ЛИЧНЫЙ КАБИНЕТ</span><h1>Мой Reklio</h1><p>Один аккаунт для сайта и Telegram Mini App.</p></div>
  {status==='loading'&&<div className="lp-account-panel">Загружаем данные аккаунта…</div>}
  {status==='error'&&<div className="lp-account-panel">Не удалось загрузить профиль. Обновите страницу и повторите попытку.</div>}
  {status==='guest'&&<div className="lp-account-panel"><h2>Войдите через Telegram</h2><p>Для доступа к личному кабинету нужна авторизация.</p><a className="lp-account-primary" href={api+'/api/auth/login'}>Войти через Telegram ↗</a></div>}
  {profile&&status==='ready'&&<div className="lp-account-layout">
   <nav className="lp-account-navigation" aria-label="Разделы личного кабинета">{navLink('profile','/account','◉ Профиль')}{navLink('catalog','/account/catalog','⌕ Каталог')}{navLink('channels','/account/channels','▣ Мои каналы')}{navLink('orders','/account/orders','▤ Заказы')}{navLink('plan','/account/plan','◇ Тариф и платежи')}{profile.staffRole&&navLink('admin','/account/admin','⚙ Администрирование')}</nav>
   <div className="lp-account-content">
    {section==='plan' ? <SitePlan/> : section==='orders' ? <SiteOrders/> : section==='order' ? <SiteBooking channelId={Number(bookingChannel)}/> : section==='channels' ? <SiteChannels editId={editChannel?Number(editChannel):null}/> : section==='catalog' ? <SiteCatalog/> : section==='admin'&&profile.staffRole ? <SiteAdmin role={profile.staffRole}/> : <>
    <div className="lp-account-panel lp-account-identity"><span className="lp-account-avatar">{(profile.name||profile.username||'T').slice(0,1).toUpperCase()}</span><div><h2>{profile.name||'Пользователь Telegram'}</h2><p>{profile.username?'@'+profile.username:'Аккаунт Telegram'} · ID {profile.telegramId}</p></div><span className="lp-account-plan">{profile.plan}</span></div>
    <div className="lp-account-stats"><div><strong>{profile.channels}</strong><span>Мои каналы</span></div><div><strong>{profile.purchases}</strong><span>Покупки</span></div><div><strong>{profile.sales}</strong><span>Продажи</span></div></div>
    <div className="lp-account-panel"><h2>Тариф {profile.plan}</h2><p>Подключено каналов: {profile.channels} из {profile.channelLimit}.</p>{profile.proUntil&&<p>PRO до {new Date(profile.proUntil).toLocaleDateString('ru-RU')}</p>}<a className="lp-account-textlink" href="/account/plan">Подключить или продлить PRO →</a></div>
    <div className="lp-dashboard-actions"><a href="/account/channels"><strong>Управление каналами</strong><span>Настройки и рекламные тарифы →</span></a><a href="/account/orders"><strong>Все заказы</strong><span>Покупки, продажи и подтверждения →</span></a><a href="/account/plan"><strong>Тариф и платежи</strong><span>PRO и история оплат →</span></a><a href="/account/catalog"><strong>Найти рекламу</strong><span>Каталог, избранное и голосование →</span></a><a href="#feedback"><strong>Связаться с нами</strong><span>Ошибка, идея или вопрос →</span></a></div>
    <div className="lp-account-panel"><div className="lp-dashboard-section-title"><h2>Последние заказы</h2><a href="/account/orders">Все заказы →</a></div>{dashboardStatus==='loading'?<p>Загружаем последние заказы…</p>:dashboardOrders.length? <div className="lp-dashboard-orders">{dashboardOrders.slice(0,5).map(order=><a href="/account/orders" key={order.id}><div><strong>#{order.id} · {order.channel}</strong><span>{order.isOwner?'Продажа':'Покупка'} · {new Date(order.createdAt).toLocaleDateString('ru-RU')}</span></div><div><strong>{rub(order.price)}</strong><span>{statusName(order.status)}</span></div></a>)}</div>:dashboardStatus==='partial'?<p>Не удалось загрузить последние заказы.</p>:<p>Заказов пока нет. Можно начать с <a href="/account/catalog">каталога Telegram-каналов</a>.</p>}</div>
    <div className="app embeddedApp"><Feedback/></div>
    </>}
   </div>
  </div>}
 </section>;
}
