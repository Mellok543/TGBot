import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Search, ClipboardList, Megaphone, UserRound, ArrowUpRight, RefreshCw } from 'lucide-react';
import './style.css';

declare global { interface Window { Telegram?: { WebApp?: { initData: string; ready(): void; expand(): void; colorScheme: string; openTelegramLink(url: string): void } } } }

type Rate = { id: number; hours: number; price: number };
type Channel = { id: number; title: string; category: string; price: number; description: string; username: string | null };
type Order = { id: number; channel: string; price: number; status: string; createdAt: string; isOwner: boolean };
type Mine = { id: number; title: string; listed: boolean; price: number };
type Tab = 'catalog' | 'orders' | 'channels' | 'profile';
const API = import.meta.env.VITE_API_URL || '';
const tg = window.Telegram?.WebApp;
async function post<T>(path: string, data: unknown): Promise<T> {
  const response = await fetch(API + path, { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Telegram-Init-Data': tg?.initData || '' },
    body: JSON.stringify(data) });
  if (!response.ok) {
    const detail = await response.json().catch(() => ({})) as {error?:string};
    throw new Error(detail.error || `Ошибка сервера (${response.status}) при оформлении заказа`);
  }
  return response.json() as Promise<T>;
}
async function get<T>(path: string): Promise<T> {
  const response = await fetch(API + path, { headers: { 'X-Telegram-Init-Data': tg?.initData || '' } });
  if (!response.ok) throw new Error(response.status === 401 ? 'Откройте приложение из Telegram' : 'Не удалось загрузить данные');
  return response.json() as Promise<T>;
}
function App() {
  const [tab, setTab] = useState<Tab>('catalog');
  const [selected, setSelected] = useState<Channel | null>(null);
  const [rates, setRates] = useState<Rate[]>([]);
  const [rateId, setRateId] = useState('');
  const [adText, setAdText] = useState('');
  const [day, setDay] = useState('');
  const [hour, setHour] = useState('12');
  const [minute, setMinute] = useState('00');
  const days = Array.from({length:90}, (_, i) => { const d = new Date(); d.setHours(12,0,0,0); d.setDate(d.getDate()+i); return {value:`${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`, label:d.toLocaleDateString('ru-RU',{weekday:'short',day:'numeric',month:'long',year:'numeric'})}; });
  const [busy, setBusy] = useState(false);
  const [bookingMessage, setBookingMessage] = useState('');
  async function openChannel(c: Channel) {
    setSelected(c); setBookingMessage(''); setRates([]); setRateId('');
    try { setRates(await get<Rate[]>('/api/channels/' + c.id + '/rates')); }
    catch(e) { setBookingMessage(e instanceof Error ? e.message : 'Ошибка'); }
  }
  async function submitOrder() {
    if (!selected || !rateId || !adText.trim() || !day) {
      setBookingMessage('Укажите тариф, текст объявления и дату публикации'); return;
    }
    const timestamp = new Date(`${day}T${hour}:${minute}:00`);
    if (Number.isNaN(timestamp.getTime()) || timestamp.getTime() <= Date.now()+120000) {
      setBookingMessage('Выберите время минимум через 2 минуты от текущего и не позднее 90 дней'); return;
    }
    setBusy(true); setBookingMessage('');
    try {
      const draft = await post<{id:number}>('/api/orders', { channelId:selected.id, text:adText.trim() });
      await post('/api/orders/'+draft.id+'/schedule', { rateId:Number(rateId), scheduledAt:timestamp.toISOString() });
      setSelected(null); setTab('orders'); setBookingMessage('Заявка #'+draft.id+' отправлена владельцу на согласование');
    } catch(e) { setBookingMessage(e instanceof Error ? e.message : 'Ошибка оформления'); }
    finally { setBusy(false); }
  }
  const [search, setSearch] = useState('');
  const [channels, setChannels] = useState<Channel[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [processingOrder, setProcessingOrder] = useState<number | null>(null);
  const [orderMessage, setOrderMessage] = useState('');
  async function payOrder(order: Order) {
    if (processingOrder !== null) return;
    setProcessingOrder(order.id); setOrderMessage('');
    try {
      const payment = await post<{confirmationUrl:string}>('/api/orders/' + order.id + '/payment', {});
      const url = new URL(payment.confirmationUrl);
      if (url.protocol !== 'https:') throw new Error('Некорректная ссылка оплаты');
      window.location.assign(url.href);
    } catch (e) {
      setOrderMessage(e instanceof Error ? e.message : 'Ошибка оплаты');
    } finally { setProcessingOrder(null); }
  }
  async function checkPayment(order: Order) {
    if (processingOrder !== null) return;
    setProcessingOrder(order.id); setOrderMessage('');
    try {
      const payment = await post<{status:string}>('/api/orders/' + order.id + '/payment/check', {});
      if (payment.status === 'Succeeded') {
        setOrders(previous => previous.map(item => item.id === order.id ? {...item, status:'Scheduled'} : item));
        setOrderMessage('Тестовая оплата подтверждена');
      } else setOrderMessage('Статус ЮKassa: ' + payment.status);
    } catch(e) { setOrderMessage(e instanceof Error ? e.message : 'Ошибка проверки'); }
    finally { setProcessingOrder(null); }
  }
  async function decideOrder(order: Order, decision: 'approve' | 'reject') {
    if (processingOrder !== null) return;
    setProcessingOrder(order.id); setOrderMessage('');
    try {
      const result = await post<{status:string}>('/api/orders/' + order.id + '/' + decision, {});
      setOrders(previous => previous.map(item => item.id === order.id ? {...item, status: result.status} : item));
      setOrderMessage(decision === 'approve' ? 'Заявка #' + order.id + ' одобрена' : 'Заявка #' + order.id + ' отклонена');
    } catch (e) {
      setOrderMessage(e instanceof Error ? e.message : 'Не удалось обработать заявку');
      try { setOrders(await get<Order[]>('/api/orders')); } catch { /* Preserve the last known list. */ }
    } finally { setProcessingOrder(null); }
  }
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
      {bookingMessage && <div className="notice">{bookingMessage}</div>}
      {selected && tab === 'catalog' && <div className="bookingOverlay"><div className="bookingPanel">
        <button className="back" onClick={()=>setSelected(null)}>← Назад в каталог</button>
        <h2>{selected.title}</h2><p className="description">{selected.description || 'Рекламное размещение в Telegram-канале'}</p>
        <label className="field">Тариф<select value={rateId} onChange={e=>setRateId(e.target.value)}>
          <option value="">Выберите тариф</option>{rates.map(rate=><option key={rate.id} value={rate.id}>{rate.hours} ч — {money(rate.price)}</option>)}
        </select></label>
        {!rates.length && <p className="description">Владелец пока не добавил тарифы.</p>}
        <label className="field">Текст рекламного поста<textarea value={adText} maxLength={4096} rows={5} onChange={e=>setAdText(e.target.value)} placeholder="Введите рекламный текст"/></label>
        <div className="field"><span>Дата публикации (ваш часовой пояс)</span><select value={day} onChange={e=>setDay(e.target.value)}><option value="">Выберите день</option>{days.map(d=><option key={d.value} value={d.value}>{d.label}</option>)}</select></div>
        <div className="field"><span>Время публикации</span><div className="timeGrid"><select aria-label="Часы" value={hour} onChange={e=>setHour(e.target.value)}>{Array.from({length:24},(_,i)=>String(i).padStart(2,'0')).map(h=><option key={h} value={h}>{h} ч</option>)}</select><select aria-label="Минуты" value={minute} onChange={e=>setMinute(e.target.value)}>{Array.from({length:12},(_,i)=>String(i*5).padStart(2,'0')).map(m=><option key={m} value={m}>{m} мин</option>)}</select></div><small className="hint">Доступные даты — ближайшие 90 дней. Время по часам вашего телефона.</small></div>
        <p className="description">После отправки владелец должен согласовать заявку. Оплата станет доступна после согласования.</p>
        {bookingMessage && <div className="notice">{bookingMessage}</div>}
        <button className="primary" disabled={busy || !rates.length} onClick={submitOrder}>{busy?'Отправка...':'Отправить заявку'}</button>
      </div></div>}
      {error && <div className="notice">{error}<button onClick={()=>setSearch(s=>s+' ')}>Повторить</button></div>}
      {loading && <div className="muted loading"><RefreshCw size={16}/> Загрузка...</div>}
      {tab === 'catalog' && <section>{!loading && channels.length === 0 && !error && <div className="empty">Каналы не найдены. Попробуйте изменить запрос.</div>}{channels.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1).toUpperCase()}</div><div className="grow"><strong>{c.title}</strong><small>{c.category}</small></div><div className="price">{money(c.price)}</div></div>{c.description && <p className="description">{c.description}</p>}<button className="primary small" onClick={()=>openChannel(c)}>Заказать рекламу</button>{c.username && <button className="link" onClick={()=>tg?.openTelegramLink('https://t.me/'+c.username!.replace(/^@/,''))}>Открыть канал <ArrowUpRight size={15}/></button>}</article>)}</section>}
      {tab === 'orders' && <section>{orderMessage && <div className="notice">{orderMessage}</div>}{!loading && !orders.length && !error && <div className="empty">Пока нет заказов</div>}{orders.map(o=><article className="card" key={o.id}><div className="cardTop"><div className="grow"><strong>Заказ #{o.id}</strong><small>{o.channel} · {o.isOwner ? 'Входящая заявка' : 'Моя заявка'}</small></div><div className="price">{money(o.price)}</div></div><span className="status">{({Draft:'Черновик',Pending:'Ожидает согласования',AwaitingPayment:'Ожидает оплаты',Rejected:'Отклонена',Scheduled:'Запланирована',Publishing:'Публикуется',Published:'Опубликована'} as Record<string,string>)[o.status] || o.status}</span>{!o.isOwner && o.status === 'AwaitingPayment' && <div className="orderActions"><button className="primary small" disabled={processingOrder !== null} onClick={()=>payOrder(o)}>Оплатить (тест)</button><button className="secondary small" disabled={processingOrder !== null} onClick={()=>checkPayment(o)}>Проверить оплату</button></div>}{o.isOwner && o.status === 'Pending' && <div className="orderActions"><button className="primary small" disabled={processingOrder !== null} onClick={()=>decideOrder(o,'approve')}>{processingOrder === o.id ? 'Обработка...' : 'Одобрить'}</button><button className="secondary small" disabled={processingOrder !== null} onClick={()=>decideOrder(o,'reject')}>Отклонить</button></div>}</article>)}</section>}
      {tab === 'channels' && <section>{!loading && !mine.length && !error && <div className="empty">Каналы пока не подключены. Добавить первый канал можно через бота.</div>}{mine.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1)}</div><div className="grow"><strong>{c.title}</strong><small>{c.listed?'В каталоге':'Скрыт из каталога'}</small></div><div className="price">{money(c.price)}</div></div></article>)}</section>}
      {tab === 'profile' && <div className="card"><strong>Ваш кабинет</strong><p className="description">Вы вошли через Telegram. Настройки тарифов, создание заказов и платежи будут перенесены сюда на следующих этапах.</p></div>}
    </main>
    <nav>{tabs.map(({id,label,icon:Icon})=><button key={id} className={tab===id?'active':''} onClick={()=>setTab(id)}><Icon size={21}/><span>{label}</span></button>)}</nav>
  </div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
