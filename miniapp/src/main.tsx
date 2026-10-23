import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Search, ClipboardList, Megaphone, UserRound, ArrowUpRight, RefreshCw } from 'lucide-react';
import './style.css';

declare global { interface Window { Telegram?: { WebApp?: { initData: string; ready(): void; expand(): void; colorScheme: string; openTelegramLink(url: string): void } } } }

type Channel = { id: number; title: string; category: string; price: number; description: string; username: string | null };
type Order = { id: number; channel: string; price: number; status: string; createdAt: string };
type Mine = { id: number; title: string; listed: boolean; price: number };
type Tab = 'catalog' | 'orders' | 'channels' | 'profile';
const API = import.meta.env.VITE_API_URL || '';
const tg = window.Telegram?.WebApp;
async function get<T>(path: string): Promise<T> {
  const response = await fetch(API + path, { headers: { 'X-Telegram-Init-Data': tg?.initData || '' } });
  if (!response.ok) throw new Error(response.status === 401 ? 'Откройте приложение из Telegram' : 'Не удалось загрузить данные');
  return response.json() as Promise<T>;
}
function App() {
  const [tab, setTab] = useState<Tab>('catalog');
  const [search, setSearch] = useState('');
  const [channels, setChannels] = useState<Channel[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [mine, setMine] = useState<Mine[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  useEffect(() => { tg?.ready(); tg?.expand(); }, []);
  useEffect(() => {
    let active = true;
    setLoading(true); setError('');
    const path = tab === 'catalog' ? '/api/channels?search=' + encodeURIComponent(search) : tab === 'orders' ? '/api/orders' : tab === 'channels' ? '/api/my-channels' : '/api/me';
    const timer = setTimeout(() => {
      get<Channel[] | Order[] | Mine[] | {id:number}>(path)
        .then(data => { if (!active) return; if (tab === 'catalog') setChannels(data as Channel[]); if (tab === 'orders') setOrders(data as Order[]); if (tab === 'channels') setMine(data as Mine[]); })
        .catch(e => { if (active) setError(e instanceof Error ? e.message : 'Ошибка'); })
        .finally(() => { if (active) setLoading(false); });
    }, tab === 'catalog' ? 250 : 0);
    return () => { active = false; clearTimeout(timer); };
  }, [tab, search]);
  const money = (n: number) => new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 }).format(n);
  const tabs: {id:Tab;label:string;icon:typeof Search}[] = [ {id:'catalog',label:'Каталог',icon:Search}, {id:'orders',label:'Заказы',icon:ClipboardList}, {id:'channels',label:'Каналы',icon:Megaphone}, {id:'profile',label:'Профиль',icon:UserRound} ];
  return <div className="app">
    <header><div className="eyebrow">TELEGRAM MANAGER</div><h1>{tab === 'catalog' ? 'Найти рекламу' : tab === 'orders' ? 'Мои заказы' : tab === 'channels' ? 'Мои каналы' : 'Профиль'}</h1><p>{tab === 'catalog' ? 'Подберите канал для вашего объявления' : tab === 'orders' ? 'Контролируйте размещения' : tab === 'channels' ? 'Управляйте рекламными площадками' : 'Настройки аккаунта'}</p></header>
    <main>
      {tab === 'catalog' && <label className="search"><Search size={19}/><input placeholder="Название канала" value={search} maxLength={80} onChange={e=>setSearch(e.target.value)}/></label>}
      {error && <div className="notice">{error}<button onClick={()=>setSearch(s=>s+' ')}>Повторить</button></div>}
      {loading && <div className="muted loading"><RefreshCw size={16}/> Загрузка...</div>}
      {tab === 'catalog' && <section>{!loading && channels.length === 0 && !error && <div className="empty">Каналы не найдены. Попробуйте изменить запрос.</div>}{channels.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1).toUpperCase()}</div><div className="grow"><strong>{c.title}</strong><small>{c.category}</small></div><div className="price">{money(c.price)}</div></div>{c.description && <p className="description">{c.description}</p>}{c.username && <button className="link" onClick={()=>tg?.openTelegramLink('https://t.me/'+c.username!.replace(/^@/,''))}>Открыть канал <ArrowUpRight size={15}/></button>}</article>)}</section>}
      {tab === 'orders' && <section>{!loading && !orders.length && !error && <div className="empty">Пока нет заказов</div>}{orders.map(o=><article className="card" key={o.id}><div className="cardTop"><div className="grow"><strong>Заказ #{o.id}</strong><small>{o.channel}</small></div><div className="price">{money(o.price)}</div></div><span className="status">{o.status}</span></article>)}</section>}
      {tab === 'channels' && <section>{!loading && !mine.length && !error && <div className="empty">Каналы пока не подключены. Добавить первый канал можно через бота.</div>}{mine.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1)}</div><div className="grow"><strong>{c.title}</strong><small>{c.listed?'В каталоге':'Скрыт из каталога'}</small></div><div className="price">{money(c.price)}</div></div></article>)}</section>}
      {tab === 'profile' && <div className="card"><strong>Ваш кабинет</strong><p className="description">Вы вошли через Telegram. Настройки тарифов, создание заказов и платежи будут перенесены сюда на следующих этапах.</p></div>}
    </main>
    <nav>{tabs.map(({id,label,icon:Icon})=><button key={id} className={tab===id?'active':''} onClick={()=>setTab(id)}><Icon size={21}/><span>{label}</span></button>)}</nav>
  </div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
