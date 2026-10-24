import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Search, ClipboardList, Megaphone, UserRound, ArrowUpRight, RefreshCw } from 'lucide-react';
import './style.css';

declare global { interface Window { Telegram?: { WebApp?: { initData: string; ready(): void; expand(): void; colorScheme: string; openTelegramLink(url: string): void } } } }

type Rate = { id: number; hours: number; price: number };
type HistoryPoint = { day: string; subscribers: number | null; posts: number };
type Channel = { id: number; title: string; category: string; price: number; description: string; username: string | null; subscribers: number | null; posts30d: number };
type StaffMember={id:number;role:string;grantedBy:number;createdAt:string};
type AdminUser={id:number;name:string;username:string|null;channels:number;purchases:number};
type AdminOrder={id:number;channel:string;status:string;price:number;buyerId:number;ownerId:number;createdAt:string};
type AuditItem={id:number;actorId:number;action:string;targetId:number|null;details:string|null;createdAt:string};
type ModerationChannel={id:number;title:string;category:string;listed:boolean;ownerId:number;price:number};
type MtprotoStatus={configured:boolean;checkedOrders:number;present:number;missing:number;lastCheck:string|null;healthy:boolean};
type PublicationIssue={id:number;channel:string;status:string;error:string;scheduledAt:string|null;publishedAt:string|null};
type AdminOverview={channels:number;listed:number;orders:number;openDisputes:number;failedPublications:number};
type AdminDispute = {id:number;channel:string;price:number;text:string;reason:string;openedAt:string|null;status:string;resolution:string|null;paymentStatus:string;isTest:boolean;payoutStatus:string};
type DealInfo = {orderId:number;sellerPrice:number;serviceFee:number;total:number;status:string;paymentStatus:string;payoutStatus:string;isTest:boolean;isSeller:boolean;providerDealActive:boolean;message:string};
type FinanceOperation = {id:number;channel:string;amount:number;commission:number;net:number;status:string;payoutStatus:string;publishedAt:string|null;eligibleAt:string|null};
type FinanceData = {pending:number;readyForReview:number;readyNet:number;paid:number;refunded:number;commissionPercent:number;blocked:number;operations:FinanceOperation[];note:string};
type OrderSummary = { incomingPending:number; completedSales:number; completedPurchases:number; salesVolume:number; purchaseVolume:number; upcomingSales:number };
type Order = { id: number; channel: string; price: number; status: string; createdAt: string; isOwner: boolean; text: string; scheduledAt: string | null; durationHours: number | null; paymentStatus:string; payoutStatus:string; isTest:boolean;disputeReason:string|null;publicationIssue:string|null };
type Mine = { id: number; title: string; listed: boolean; price: number };
type SellerRate = Rate & { active: boolean };
type Tab = 'catalog' | 'orders' | 'channels' | 'profile';
const API = import.meta.env.VITE_API_URL || '';
const tg = window.Telegram?.WebApp;
const REQUEST_TIMEOUT_MS = 12000;
async function post<T>(path: string, data: unknown): Promise<T> {
  const response = await fetch(API + path, { signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS), method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Telegram-Init-Data': tg?.initData || '' },
    body: JSON.stringify(data) });
  if (!response.ok) {
    const detail = await response.json().catch(() => ({})) as {error?:string};
    throw new Error(detail.error || `Ошибка сервера (${response.status}) при оформлении заказа`);
  }
  return response.json() as Promise<T>;
}
async function get<T>(path: string): Promise<T> {
  const response = await fetch(API + path, { signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS), headers: { 'X-Telegram-Init-Data': tg?.initData || '' } });
  if (!response.ok) throw new Error(response.status === 401 ? 'Откройте приложение из Telegram' : 'Не удалось загрузить данные');
  return response.json() as Promise<T>;
}
function App() {
  const [tab, setTab] = useState<Tab>('catalog');
  const [showAbout, setShowAbout] = useState(false);
  const [adminDisputes, setAdminDisputes] = useState<AdminDispute[] | null>(null);
  const [adminBusy, setAdminBusy] = useState<number | null>(null);
  const [adminError, setAdminError] = useState('');
  const [adminOpen, setAdminOpen] = useState(false);
  const [adminTab,setAdminTab]=useState<'overview'|'channels'|'orders'|'users'|'disputes'|'team'|'audit'>('overview');
  const [adminRole,setAdminRole]=useState<string|null>(null);
  const [staff,setStaff]=useState<StaffMember[]>([]);
  const [staffId,setStaffId]=useState('');
  const [staffRole,setStaffRole]=useState('moderator');
  const [overview,setOverview]=useState<AdminOverview|null>(null);
  const [moderationChannels,setModerationChannels]=useState<ModerationChannel[]>([]);
  const [publicationIssues,setPublicationIssues]=useState<PublicationIssue[]>([]);
  const [mtprotoStatus,setMtprotoStatus]=useState<MtprotoStatus|null>(null);
  const [reviewOrder,setReviewOrder]=useState<number|null>(null);
  const [reviewNote,setReviewNote]=useState('');
  const [moderationBusy,setModerationBusy]=useState<number|null>(null);
  const [adminSearch,setAdminSearch]=useState('');
  const [adminUsers,setAdminUsers]=useState<AdminUser[]>([]);
  const [adminOrders,setAdminOrders]=useState<AdminOrder[]>([]);
  const [adminAudit,setAdminAudit]=useState<AuditItem[]>([]);
  const [adminSearchBusy,setAdminSearchBusy]=useState(false);
  const [isDisputeAdmin, setIsDisputeAdmin] = useState(false);
  const [selected, setSelected] = useState<Channel | null>(null);
  const [rates, setRates] = useState<Rate[]>([]);
  const [reservedSlots, setReservedSlots] = useState<string[]>([]);
  const [availabilityKnown, setAvailabilityKnown] = useState(false);
  const [history, setHistory] = useState<HistoryPoint[]>([]);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [rateId, setRateId] = useState('');
  const [adText, setAdText] = useState('');
  const [day, setDay] = useState('');
  const [hour, setHour] = useState('12');
  const [minute, setMinute] = useState('00');
  const days = Array.from({length:90}, (_, i) => { const d = new Date(); d.setHours(12,0,0,0); d.setDate(d.getDate()+i); return {value:`${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`, label:d.toLocaleDateString('ru-RU',{weekday:'short',day:'numeric',month:'long',year:'numeric'})}; });
  const [busy, setBusy] = useState(false);
  const [bookingMessage, setBookingMessage] = useState('');
  async function openChannel(c: Channel) {
    setSelected(c); setBookingMessage(''); setRates([]); setHistory([]); setRateId(''); setReservedSlots([]); setAvailabilityKnown(false); setDetailsLoading(true);
    const [ratesResult, historyResult, slotsResult] = await Promise.allSettled([
      get<Rate[]>('/api/channels/' + c.id + '/rates'),
      get<HistoryPoint[]>('/api/channels/' + c.id + '/history'),
      get<string[]>('/api/channels/' + c.id + '/availability')
    ]);
    if (ratesResult.status === 'fulfilled') setRates(ratesResult.value);
    else setBookingMessage(ratesResult.reason instanceof Error ? ratesResult.reason.message : 'Не удалось загрузить тарифы');
    if (historyResult.status === 'fulfilled') setHistory(historyResult.value);
    if (slotsResult.status === 'fulfilled') { setReservedSlots(slotsResult.value); setAvailabilityKnown(true); }
    setDetailsLoading(false);
  }
  const chosenRate = rates.find(r => String(r.id) === rateId);
  const selectedDateTime = day ? new Date(`${day}T${hour}:${minute}:00`) : null;
  const conflicts = (dateTime: Date | null) => !!dateTime && reservedSlots.some(value => {
    const booked = new Date(value.endsWith('Z') ? value : value + 'Z');
    return !Number.isNaN(booked.getTime()) && Math.abs(booked.getTime() - dateTime.getTime()) < 60000;
  });
  async function submitOrder() {
    if (!selected || !rateId || !adText.trim() || !day) {
      setBookingMessage('Укажите тариф, текст объявления и дату публикации'); return;
    }
    const timestamp = new Date(`${day}T${hour}:${minute}:00`);
    if (Number.isNaN(timestamp.getTime()) || timestamp.getTime() <= Date.now()+120000) {
      setBookingMessage('Выберите время минимум через 2 минуты от текущего и не позднее 90 дней'); return;
    }
    if (conflicts(timestamp)) { setBookingMessage('Это время уже занято. Выберите другое.'); return; }
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
  const [categoryFilter, setCategoryFilter] = useState('');
  const [sortBy, setSortBy] = useState<'default' | 'priceAsc' | 'priceDesc' | 'subscribers' | 'posts' | 'title'>('default');
  const categories = [...new Set(channels.map(c => c.category).filter(Boolean))].sort((a,b) => a.localeCompare(b, 'ru'));
  const normalizedSearch = search.trim().toLocaleLowerCase('ru');
  const visibleChannels = channels.filter(c =>
    (!categoryFilter || c.category === categoryFilter) &&
    (!normalizedSearch || [c.title, c.category, c.description, c.username || ''].some(value => value.toLocaleLowerCase('ru').includes(normalizedSearch)))
  ).sort((a,b) => sortBy === 'priceAsc' ? a.price - b.price
    : sortBy === 'priceDesc' ? b.price - a.price
    : sortBy === 'subscribers' ? (b.subscribers ?? -1) - (a.subscribers ?? -1)
    : sortBy === 'posts' ? b.posts30d - a.posts30d
    : sortBy === 'title' ? a.title.localeCompare(b.title, 'ru') : 0);
  const [orders, setOrders] = useState<Order[]>([]);
  const [finance, setFinance] = useState<FinanceData | null>(null);
  const [financeError, setFinanceError] = useState('');
  const [orderSummary, setOrderSummary] = useState<OrderSummary | null>(null);
  const [orderFilter, setOrderFilter] = useState<'incoming' | 'outgoing' | 'all'>('incoming');
  const [expandedOrder, setExpandedOrder] = useState<number | null>(null);
  const [disputeOrder, setDisputeOrder] = useState<number | null>(null);
  const [disputeReason, setDisputeReason] = useState('');
  const [rescheduleOrder, setRescheduleOrder] = useState<number | null>(null);
  const [rescheduleTime, setRescheduleTime] = useState('');
  const [dealInfo, setDealInfo] = useState<Record<number,DealInfo>>({});
  const filteredOrders = orders.filter(o=>orderFilter==='all'||(orderFilter==='incoming'?o.isOwner:!o.isOwner));
  async function reschedule(id:number) {
    const date=new Date(rescheduleTime);
    if(!rescheduleTime || Number.isNaN(date.getTime()) || date.getTime()<=Date.now()+600000){
      setOrderMessage('Выберите новое время минимум через 10 минут');return;
    }
    setProcessingOrder(id);setOrderMessage('');
    try {
      await post('/api/orders/'+id+'/reschedule',{scheduledAt:date.toISOString()});
      setOrders(await get<Order[]>('/api/orders'));
      setRescheduleOrder(null);
      setOrderMessage('Новая дата отправлена владельцу для повторного согласования');
    } catch(e){setOrderMessage(e instanceof Error?e.message:'Не удалось перенести заказ');}
    finally{setProcessingOrder(null);}
  }
  async function openDispute(id:number) {
    if(disputeReason.trim().length<10){setOrderMessage('Опишите проблему подробнее, минимум 10 символов');return;}
    setProcessingOrder(id);setOrderMessage('');
    try {
      await post('/api/orders/'+id+'/dispute',{reason:disputeReason.trim()});
      setOrders(await get<Order[]>('/api/orders'));
      setDisputeOrder(null);setDisputeReason('');
      setOrderMessage('Обращение создано. Выплата по заказу приостановлена.');
    }catch(e){setOrderMessage(e instanceof Error?e.message:'Не удалось открыть спор');}
    finally{setProcessingOrder(null);}
  }
  async function showOrderDetails(id:number) {
    if(expandedOrder===id){setExpandedOrder(null);return;}
    setExpandedOrder(id);
    try{
      const data=await get<DealInfo>('/api/orders/'+id+'/deal');
      setDealInfo(previous=>({...previous,[id]:data}));
    }catch{ /* Existing order details remain available without deal metadata. */ }
  }
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
  const [editingChannel, setEditingChannel] = useState<Mine | null>(null);
  const [sellerRates, setSellerRates] = useState<SellerRate[]>([]);
  const [sellerHours, setSellerHours] = useState('24');
  const [sellerPrice, setSellerPrice] = useState('');
  const [sellerBusy, setSellerBusy] = useState(false);
  const [sellerMessage, setSellerMessage] = useState('');
  async function loadSellerRates(channel: Mine) {
    setEditingChannel(channel); setSellerMessage(''); setSellerRates([]);
    try { setSellerRates(await get<SellerRate[]>('/api/my-channels/'+channel.id+'/rates')); }
    catch(e) { setSellerMessage(e instanceof Error ? e.message : 'Ошибка загрузки тарифов'); }
  }
  async function saveSellerRate() {
    if (!editingChannel || sellerBusy) return;
    const hours=Number(sellerHours), price=Number(sellerPrice);
    if (!Number.isInteger(hours) || hours<1 || hours>720 || !Number.isFinite(price) || price<1 || price>1000000) {
      setSellerMessage('Укажите длительность 1–720 часов и цену от 1 до 1 000 000 ₽'); return;
    }
    setSellerBusy(true); setSellerMessage('');
    try {
      await post('/api/my-channels/'+editingChannel.id+'/rates',{hours,price});
      setSellerRates(await get<SellerRate[]>('/api/my-channels/'+editingChannel.id+'/rates'));
      setSellerPrice(''); setSellerMessage('Тариф сохранён');
    } catch(e) { setSellerMessage(e instanceof Error ? e.message : 'Ошибка сохранения'); }
    finally { setSellerBusy(false); }
  }
  async function toggleSellerRate(rate: SellerRate) {
    if (!editingChannel || sellerBusy) return;
    setSellerBusy(true); setSellerMessage('');
    try {
      await post('/api/my-channels/'+editingChannel.id+'/rates/'+rate.id+'/toggle',{});
      setSellerRates(await get<SellerRate[]>('/api/my-channels/'+editingChannel.id+'/rates'));
    } catch(e) { setSellerMessage(e instanceof Error ? e.message : 'Ошибка'); }
    finally { setSellerBusy(false); }
  }
  async function toggleListing(channel: Mine) {
    if (sellerBusy) return;
    setSellerBusy(true); setSellerMessage('');
    try {
      await post('/api/my-channels/'+channel.id+'/listing',{listed:!channel.listed});
      setMine(prev=>prev.map(c=>c.id===channel.id?{...c,listed:!c.listed}:c));
      setEditingChannel({...channel,listed:!channel.listed});
    } catch(e) { setSellerMessage(e instanceof Error ? e.message : 'Ошибка'); }
    finally { setSellerBusy(false); }
  }
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (tab !== 'orders') return;
    let active=true;
    get<OrderSummary>('/api/orders/summary').then(value=>{if(active)setOrderSummary(value);}).catch(()=>{if(active)setOrderSummary(null);});
    return ()=>{active=false;};
  }, [tab,retry,orders]);
  useEffect(() => {
    if(tab!=='profile') return;
    let active=true;
    setFinanceError('');
    get<FinanceData>('/api/finance').then(data=>{if(active)setFinance(data);})
      .catch(e=>{if(active)setFinanceError(e instanceof Error?e.message:'Ошибка загрузки финансов');});
    return ()=>{active=false;};
  },[tab,retry]);
  useEffect(() => {
    if (tab !== 'orders') return;
    let active=true;
    const poll=() => get<Order[]>('/api/orders').then(data=>{if(active)setOrders(data);}).catch(()=>{});
    const interval=window.setInterval(poll,15000);
    return ()=>{active=false;window.clearInterval(interval);};
  },[tab]);
  async function loadAdminDisputes(){
    setAdminError('');
    try{const [disputes,stats,channels,issues]=await Promise.all([get<AdminDispute[]>('/api/admin/disputes'),get<AdminOverview>('/api/admin/overview'),get<ModerationChannel[]>('/api/admin/channels'),get<PublicationIssue[]>('/api/admin/publication-issues')]);setAdminDisputes(disputes);setOverview(stats);setModerationChannels(channels);setPublicationIssues(issues);setMtprotoStatus(await get<MtprotoStatus>('/api/admin/mtproto-status').catch(()=>null));if(adminRole==='owner'||adminRole==='admin')setStaff(await get<StaffMember[]>('/api/admin/staff'));setAdminOpen(true);await searchAdmin('');if(adminRole==='owner'||adminRole==='admin')setAdminAudit(await get<AuditItem[]>('/api/admin/audit'));}
    catch(e){setAdminError(e instanceof Error?e.message:'Доступ к спорам недоступен');}
  }
  async function saveStaff() {
    if(!/^\d+$/.test(staffId)){setAdminError('Укажи числовой Telegram ID');return;}
    setAdminError('');
    try{await post('/api/admin/staff',{telegramId:Number(staffId),role:staffRole});setStaffId('');setStaff(await get<StaffMember[]>('/api/admin/staff'));}
    catch(e){setAdminError(e instanceof Error?e.message:'Не удалось изменить роль');}
  }
  async function revokeStaff(id:number) {
    setAdminError('');
    try{await post('/api/admin/staff/'+id+'/revoke',{});setStaff(await get<StaffMember[]>('/api/admin/staff'));}
    catch(e){setAdminError(e instanceof Error?e.message:'Не удалось отозвать роль');}
  }
  async function searchAdmin(query=adminSearch){
    setAdminSearchBusy(true);setAdminError('');
    try{
      const [users,orders]=await Promise.all([
        get<AdminUser[]>('/api/admin/users?q='+encodeURIComponent(query)),
        get<AdminOrder[]>('/api/admin/orders?q='+encodeURIComponent(query))
      ]);
      setAdminUsers(users);setAdminOrders(orders);
    }catch(e){setAdminError(e instanceof Error?e.message:'Ошибка поиска');}
    finally{setAdminSearchBusy(false);}
  }
  async function reviewPublication(id:number,decision:'ConfirmedProblem'|'NeedsInvestigation'){
    if(reviewNote.trim().length<10){setAdminError('Укажите причину (минимум 10 символов)');return;}
    setModerationBusy(id);setAdminError('');
    try{
      await post('/api/admin/publication-issues/'+id+'/review',{decision,note:reviewNote.trim()});
      setPublicationIssues(await get<PublicationIssue[]>('/api/admin/publication-issues'));
      setReviewOrder(null);setReviewNote('');
      if(adminRole==='owner'||adminRole==='admin')setAdminAudit(await get<AuditItem[]>('/api/admin/audit'));
    }catch(e){setAdminError(e instanceof Error?e.message:'Не удалось сохранить проверку');}
    finally{setModerationBusy(null);}
  }
  async function moderateChannel(channel:ModerationChannel){
    if(moderationBusy!==null)return;
    setModerationBusy(channel.id);setAdminError('');
    try{
      await post('/api/admin/channels/'+channel.id+'/listing',{listed:!channel.listed});
      setModerationChannels(items=>items.map(x=>x.id===channel.id?{...x,listed:!channel.listed}:x));
    }catch(e){setAdminError(e instanceof Error?e.message:'Не удалось изменить видимость канала');}
    finally{setModerationBusy(null);}
  }
  async function resolveAdminDispute(id:number,decision:'RefundRecommended'|'PayoutRecommended'){
    if(adminBusy!==null)return;
    setAdminBusy(id);setAdminError('');
    try{await post('/api/admin/disputes/'+id+'/resolve',{decision});await loadAdminDisputes();}
    catch(e){setAdminError(e instanceof Error?e.message:'Не удалось сохранить решение');}
    finally{setAdminBusy(null);}
  }
  useEffect(() => { tg?.ready(); tg?.expand(); get<{id:number;isDisputeAdmin:boolean;adminRole:string|null}>('/api/me').then(me=>{setIsDisputeAdmin(me.isDisputeAdmin);setAdminRole(me.adminRole);}).catch(()=>setIsDisputeAdmin(false)); }, []);
  useEffect(() => {
    let active = true;
    setLoading(true); setError('');
    const path = tab === 'catalog' ? '/api/channels' : tab === 'orders' ? '/api/orders' : tab === 'channels' ? '/api/my-channels' : '/api/me';
    const timer = setTimeout(() => {
      get<Channel[] | Order[] | Mine[] | {id:number}>(path)
        .then(data => { if (!active) return; if (tab === 'catalog') setChannels(data as Channel[]); if (tab === 'orders') setOrders(data as Order[]); if (tab === 'channels') setMine(data as Mine[]); })
        .catch(e => { if (active) setError(e instanceof Error && e.name === 'TimeoutError' ? 'Сервер долго не отвечает. Попробуйте снова.' : e instanceof Error ? e.message : 'Ошибка'); })
        .finally(() => { if (active) setLoading(false); });
    }, tab === 'catalog' ? 250 : 0);
    return () => { active = false; clearTimeout(timer); };
  }, [tab, retry]);
  const money = (n: number) => new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 }).format(n);
  const tabs: {id:Tab;label:string;icon:typeof Search}[] = [ {id:'catalog',label:'Каталог',icon:Search}, {id:'orders',label:'Заказы',icon:ClipboardList}, {id:'channels',label:'Каналы',icon:Megaphone}, {id:'profile',label:'Профиль',icon:UserRound} ];
  return <div className="app">
    <header><div className="brandLine"><img src="/reklio-mark.svg" alt="Логотип Reklio" className="brandMark"/><div><div className="brandName">Rekli<span>o</span></div><div className="eyebrow">БИРЖА РЕКЛАМЫ</div></div></div><h1>{tab === 'catalog' ? 'Найти рекламу' : tab === 'orders' ? 'Мои заказы' : tab === 'channels' ? 'Мои каналы' : 'Профиль'}</h1><p>{tab === 'catalog' ? 'Подберите канал для вашего объявления' : tab === 'orders' ? 'Контролируйте размещения' : tab === 'channels' ? 'Управляйте рекламными площадками' : 'Настройки аккаунта'}</p><button className="aboutTrigger" onClick={()=>setShowAbout(true)}>ⓘ Как работает Reklio <span>→</span></button></header>
    <main>
      {showAbout && <div className="bookingOverlay" role="dialog" aria-modal="true" aria-label="Как работает Reklio"><div className="bookingPanel aboutPanel"><button className="back" onClick={()=>setShowAbout(false)}>← Вернуться</button><div className="aboutHero"><div className="eyebrow">REKLIO</div><h2>Реклама в Telegram — в одном месте</h2><p>Reklio помогает рекламодателям находить каналы для размещения, а авторам каналов — получать рекламные заказы и управлять публикациями.</p></div><h3>Рекламодателям</h3><ol><li>Найдите канал в каталоге и изучите его статистику и тарифы.</li><li>Выберите дату, подготовьте рекламный текст и отправьте заявку.</li><li>После согласования оплатите заказ и следите за публикацией в разделе «Заказы».</li></ol><h3>Владельцам каналов</h3><ol><li>Подключите свой канал через Telegram-бота Reklio.</li><li>Укажите стоимость размещения и доступные тарифы.</li><li>Проверяйте заявки, подтверждайте подходящие объявления и контролируйте публикации.</li></ol><h3>Оплата и стоимость</h3><p>Владелец канала сам устанавливает цену рекламы и получает всю указанную стоимость. Покупатель дополнительно оплачивает сервисный сбор Reklio. Полная сумма и состав платежа показываются до отправки заявки и перед оплатой.</p><h3>Как защищается сделка?</h3><p>Система отслеживает статусы оплаты, публикации и завершения размещения. Безопасные сделки с выплатой после выполнения условий готовятся на базе YooKassa. Пока доступна тестовая оплата: реальные безопасные выплаты ещё не запущены.</p><div className="aboutNote">Reklio — платформа для поиска рекламных площадок и управления размещениями. Статистика каналов зависит от доступных данных Telegram и не гарантирует результат рекламной кампании.</div><button className="primary" onClick={()=>setShowAbout(false)}>Понятно</button></div></div>}
      {tab === 'catalog' && <label className="search"><Search size={19}/><input placeholder="Название канала" value={search} maxLength={80} onChange={e=>setSearch(e.target.value)}/></label>}
      {tab === 'catalog' && <div className="catalogControls">
        <label>Категория<select aria-label="Категория" value={categoryFilter} onChange={e=>setCategoryFilter(e.target.value)}>
          <option value="">Все категории</option>{categories.map(category=><option key={category} value={category}>{category}</option>)}
        </select></label>
        <label>Сортировка<select aria-label="Сортировка" value={sortBy} onChange={e=>setSortBy(e.target.value as typeof sortBy)}>
          <option value="default">По умолчанию</option><option value="priceAsc">Сначала дешёвые</option>
          <option value="priceDesc">Сначала дорогие</option><option value="subscribers">Больше подписчиков</option><option value="posts">Активность за 30 дней</option><option value="title">По названию</option>
        </select></label>
        <div className="catalogSummary"><span>Найдено каналов: {visibleChannels.length}</span>
          {(search || categoryFilter || sortBy !== 'default') && <button onClick={()=>{setSearch('');setCategoryFilter('');setSortBy('default');}}>Сбросить</button>}
        </div>
      </div>}
      {bookingMessage && <div className="notice">{bookingMessage}</div>}
      {selected && tab === 'catalog' && <div className="bookingOverlay"><div className="bookingPanel">
        <button className="back" onClick={()=>setSelected(null)}>← Назад в каталог</button>
        <div className="detailHeading"><div className="avatar">{selected.title.slice(0,1).toUpperCase()}</div><div><h2>{selected.title}</h2><span className="detailCategory">{selected.category}</span></div></div>
        {selected.username && <button className="link" onClick={()=>tg?.openTelegramLink('https://t.me/'+selected.username!.replace(/^@/,''))}>Перейти в канал <ArrowUpRight size={15}/></button>}
        <p className="description">{selected.description || 'Рекламное размещение в Telegram-канале'}</p>
        <h3 className="detailSubtitle">Статистика канала</h3>
        <div className="detailStats">
          <div><small>Подписчики</small><strong>{selected.subscribers === null ? 'Нет данных' : selected.subscribers.toLocaleString('ru-RU')}</strong></div>
          <div><small>Постов за 30 дней</small><strong>{selected.posts30d}</strong></div>
        </div>
        <h3 className="detailSubtitle">Динамика подписчиков · 30 дней</h3>
        {detailsLoading ? <p className="description">Загрузка статистики...</p> : (() => {
          const points = history.filter((p): p is HistoryPoint & {subscribers:number} => p.subscribers !== null);
          if (points.length < 2) return <p className="description">Пока недостаточно измерений для графика.</p>;
          const values = points.map(p=>p.subscribers);
          const min = Math.min(...values), max = Math.max(...values);
          const coordinates = points.map((p,i)=>`${(i/(points.length-1)*100).toFixed(2)},${(95-(p.subscribers-min)/Math.max(1,max-min)*85).toFixed(2)}`).join(' ');
          return <div className="historyCard"><svg viewBox="0 0 100 100" preserveAspectRatio="none" role="img" aria-label="График числа подписчиков за последние 30 дней"><polyline points={coordinates} fill="none" stroke="#80b5ff" strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke"/></svg><div className="historyLabels"><span>{points[0].day}</span><span>{points[points.length-1].day}</span></div><small>От {values[0].toLocaleString('ru-RU')} до {values[values.length-1].toLocaleString('ru-RU')} подписчиков</small></div>;
        })()}
        <h3 className="detailSubtitle">Выберите тариф размещения</h3>
        <div className="rateCards">{rates.map(rate=><button type="button" key={rate.id} aria-pressed={rateId===String(rate.id)} className={rateId===String(rate.id)?'rateCard picked':'rateCard'} onClick={()=>setRateId(String(rate.id))}><span>{rate.hours} ч размещения</span><strong>{money(rate.price)}</strong><small>{rateId===String(rate.id)?'✓ Выбрано':'Выбрать тариф'}</small></button>)}</div>
        {!rates.length && <p className="description">Владелец пока не добавил тарифы.</p>}
        <label className="field">Текст рекламного поста<textarea value={adText} maxLength={4096} rows={5} onChange={e=>setAdText(e.target.value)} placeholder="Введите рекламный текст"/></label>
        <div className="field"><span>Дата публикации (ваш часовой пояс)</span><div className="dayScroller">{days.map(d=><button type="button" key={d.value} className={day===d.value?'dayTile chosen':'dayTile'} onClick={()=>setDay(d.value)}>{new Date(d.value+'T12:00:00').toLocaleDateString('ru-RU',{weekday:'short'})}<strong>{new Date(d.value+'T12:00:00').getDate()}</strong><small>{new Date(d.value+'T12:00:00').toLocaleDateString('ru-RU',{month:'short'})}</small></button>)}</div></div>
        <div className="field"><span>Время публикации</span><div className="timeGrid"><select aria-label="Часы" value={hour} onChange={e=>setHour(e.target.value)}>{Array.from({length:24},(_,i)=>String(i).padStart(2,'0')).map(h=><option key={h} value={h}>{h} ч</option>)}</select><select aria-label="Минуты" value={minute} onChange={e=>setMinute(e.target.value)}>{Array.from({length:12},(_,i)=>String(i*5).padStart(2,'0')).map(m=><option key={m} value={m}>{m} мин</option>)}</select></div><small className="hint">Ближайшие 90 дней, время по часам вашего телефона. {availabilityKnown ? 'Занятые слоты проверяются.' : 'Доступность будет проверена при оформлении.'}</small>{availabilityKnown && conflicts(selectedDateTime) && <span className="slotWarning">Выбранное время уже занято.</span>}</div>
        <div className="orderPreview"><h3>Ваш заказ</h3><div><span>Канал</span><b>{selected.title}</b></div><div><span>Тариф</span><b>{chosenRate ? chosenRate.hours+' ч' : 'Не выбран'}</b></div><div><span>Размещение</span><b>{day ? new Date(day+'T12:00:00').toLocaleDateString('ru-RU')+' · '+hour+':'+minute : 'Дата не выбрана'}</b></div><div className="previewTotal"><span>Цена размещения</span><strong>{chosenRate ? money(chosenRate.price) : '—'}</strong></div><div><span>Сервисный сбор Reklio</span><b>{chosenRate ? money(Math.round(chosenRate.price*5)/100) : '—'}</b></div><div className="previewTotal"><span>Итого к оплате</span><strong>{chosenRate ? money(chosenRate.price+Math.round(chosenRate.price*5)/100) : '—'}</strong></div></div>
        <p className="description">После отправки владелец должен согласовать заявку. Оплата станет доступна после согласования.</p>
        {bookingMessage && <div className="notice">{bookingMessage}</div>}
        <button className="primary" disabled={busy || !chosenRate || conflicts(selectedDateTime)} onClick={submitOrder}>{busy?'Отправка...':'Отправить заявку'}</button>
      </div></div>}
      {error && <div className="notice">{error}<button onClick={()=>setRetry(n=>n+1)}>Повторить</button></div>}
      {loading && <div className="muted loading"><RefreshCw size={16}/> Загрузка...</div>}
      {tab === 'catalog' && <section>{!loading && visibleChannels.length === 0 && !error && <div className="empty">Каналы не найдены. Попробуйте изменить запрос.</div>}{visibleChannels.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1).toUpperCase()}</div><div className="grow"><strong>{c.title}</strong><small>{c.category}</small></div><div className="price">{money(c.price)}</div></div><div className="channelStats"><span>Подписчики: <b>{c.subscribers === null ? "Нет данных" : c.subscribers.toLocaleString("ru-RU")}</b></span><span>Постов за 30 дней: <b>{c.posts30d}</b></span></div>{c.description && <p className="description">{c.description}</p>}<button className="primary small" onClick={()=>openChannel(c)}>Подробнее и заказать</button>{c.username && <button className="link" onClick={()=>tg?.openTelegramLink('https://t.me/'+c.username!.replace(/^@/,''))}>Открыть канал <ArrowUpRight size={15}/></button>}</article>)}</section>}
      {tab === 'orders' && <section>{orderSummary && <div className="sellerDashboard"><h3>Статистика размещений</h3><div className="dashboardGrid"><div><small>Оплаченные продажи</small><strong>{money(orderSummary.salesVolume)}</strong></div><div><small>Оплаченные покупки</small><strong>{money(orderSummary.purchaseVolume)}</strong></div><div><small>Ожидают решения</small><strong>{orderSummary.incomingPending}</strong></div><div><small>Запланированы</small><strong>{orderSummary.upcomingSales}</strong></div><div><small>Продано размещений</small><strong>{orderSummary.completedSales}</strong></div><div><small>Куплено размещений</small><strong>{orderSummary.completedPurchases}</strong></div></div><p>Суммы включают только подтверждённые нетестовые платежи. Это оборот, не доступный к выводу баланс.</p></div>}{orderMessage && <div className="notice">{orderMessage}</div>}<div className="orderFilters"><button className={orderFilter==='incoming'?'active':''} onClick={()=>setOrderFilter('incoming')}>Входящие ({orders.filter(o=>o.isOwner).length})</button><button className={orderFilter==='outgoing'?'active':''} onClick={()=>setOrderFilter('outgoing')}>Мои покупки ({orders.filter(o=>!o.isOwner).length})</button><button className={orderFilter==='all'?'active':''} onClick={()=>setOrderFilter('all')}>Все</button></div>{!loading && !filteredOrders.length && !error && <div className="empty">Заявок пока нет</div>}{filteredOrders.map(o=><article className="card" key={o.id}><div className="cardTop"><div className="grow"><strong>Заказ #{o.id}</strong><small>{o.channel} · {o.isOwner ? 'Входящая заявка' : 'Моя заявка'}</small></div><div className="price">{money(o.price)}</div></div><span className="status">{({Draft:'Черновик',Pending:'Ожидает согласования',AwaitingPayment:'Ожидает оплаты',Rejected:'Отклонена',Scheduled:'Запланирована',Publishing:'Публикуется',Published:'Опубликована',PaidReschedule:'Оплачено — нужен перенос',PendingPaid:'Ожидает повторного согласования'} as Record<string,string>)[o.status] || o.status}</span>{!o.isOwner && (o.status==='PaidReschedule'||(o.status==='Pending'&&o.scheduledAt&&new Date(o.scheduledAt).getTime()<=Date.now()+600000)) && <div className="rescheduleBox"><button className="secondary small" onClick={()=>{setRescheduleOrder(rescheduleOrder===o.id?null:o.id);setRescheduleTime('');}}>Выбрать новое время</button>{rescheduleOrder===o.id&&<><label className="field">Новая дата и время<input type="datetime-local" value={rescheduleTime} onChange={e=>setRescheduleTime(e.target.value)}/></label><button className="primary small" disabled={processingOrder!==null} onClick={()=>reschedule(o.id)}>Отправить на согласование</button></>}</div>}<button className="orderDetailsButton" onClick={()=>showOrderDetails(o.id)}>{expandedOrder===o.id?'Скрыть подробности':'Подробнее о заявке'}</button>{expandedOrder===o.id && <div className="orderDetails"><strong>Рекламный текст</strong><p>{o.text||'Текст не указан'}</p><strong>Дата публикации</strong><p>{o.scheduledAt ? new Date(o.scheduledAt.endsWith('Z')?o.scheduledAt:o.scheduledAt+'Z').toLocaleString('ru-RU') : 'Не назначена'}</p><strong>Длительность</strong><p>{o.durationHours===null?'Не выбрана':o.durationHours+' ч'}</p><div className="dealTimeline"><strong>Этапы сделки {o.isTest?'(тестовая)':''}</strong>{[
              {name:'Согласование заявки',done:!['Draft','Pending','Rejected'].includes(o.status)},
              {name:'Оплата подтверждена',done:['Paid','TestPaid'].includes(o.paymentStatus)},
              {name:'Реклама опубликована',done:['Published','Deleted','Disputed','DisputeResolved'].includes(o.status)},
              {name:'Размещение завершено',done:['Deleted','Disputed','DisputeResolved'].includes(o.status)},
              {name:'Проверка выполнения',done:['ReadyForManualReview','Paid'].includes(o.payoutStatus)},
              {name:'Выплата продавцу',done:o.payoutStatus==='Paid'}
            ].map((step,i)=><div className={step.done?'dealStep done':'dealStep'} key={i}><span>{step.done?'✓':i+1}</span><small>{step.name}</small></div>)}
            <p>{o.isOwner?'Стоимость размещения':'К оплате'}: {money(o.isOwner?o.price:(dealInfo[o.id]?.total??o.price+Math.round(o.price*5)/100))} · Сервисный сбор: {money(dealInfo[o.id]?.serviceFee??Math.round(o.price*5)/100)} · Владельцу канала: {money(o.price)}</p>
            <small>{dealInfo[o.id]?.message??'Реальные выплаты через YooKassa ещё не подключены. Статусы носят информационный характер.'}</small></div></div>}{o.publicationIssue&&<div className="notice"><strong>Проблема размещения</strong><p>{o.publicationIssue}</p></div>}{o.status==='Disputed' && <div className="notice">По заказу открыт спор. Выплата приостановлена.{o.disputeReason&&<p>{o.disputeReason}</p>}</div>}{!o.isOwner&&['Published','Deleted','DeleteFailed'].includes(o.status)&&['Paid','TestPaid'].includes(o.paymentStatus)&&!['Paid','Refunded'].includes(o.payoutStatus)&&<div className="rescheduleBox"><button className="secondary small" onClick={()=>{setDisputeOrder(disputeOrder===o.id?null:o.id);setDisputeReason('');}}>Сообщить о проблеме</button>{disputeOrder===o.id&&<><label className="field">Что произошло?<textarea rows={3} maxLength={512} value={disputeReason} onChange={e=>setDisputeReason(e.target.value)} placeholder="Например, пост удалён раньше оплаченного срока"/></label><button className="primary small" disabled={processingOrder!==null||disputeReason.trim().length<10} onClick={()=>openDispute(o.id)}>Отправить обращение</button></>}</div>}{!o.isOwner && o.status === 'AwaitingPayment' && <div className="orderActions"><button className="primary small" disabled={processingOrder !== null} onClick={()=>payOrder(o)}>Оплатить (тест)</button><button className="secondary small" disabled={processingOrder !== null} onClick={()=>checkPayment(o)}>Проверить оплату</button></div>}{o.isOwner && (o.status === 'Pending' || o.status === 'PendingPaid') && <div className="orderActions"><button className="primary small" disabled={processingOrder !== null} onClick={()=>decideOrder(o,'approve')}>{processingOrder === o.id ? 'Обработка...' : 'Одобрить'}</button><button className="secondary small" disabled={processingOrder !== null} onClick={()=>decideOrder(o,'reject')}>Отклонить</button></div>}</article>)}</section>}
      {tab === 'channels' && <section>{!loading && !mine.length && !error && <div className="empty">Каналы пока не подключены. Добавить первый канал можно через бота.</div>}{mine.map(c=><article className="card" key={c.id}><div className="cardTop"><div className="avatar">{c.title.slice(0,1)}</div><div className="grow"><strong>{c.title}</strong><small>{c.listed?'В каталоге':'Скрыт из каталога'}</small></div><div className="price">{money(c.price)}</div></div><button className="primary small" onClick={()=>loadSellerRates(c)}>Управлять каналом</button></article>)}</section>}
      {editingChannel && tab === 'channels' && <div className="bookingOverlay"><div className="bookingPanel">
        <button className="back" onClick={()=>setEditingChannel(null)}>← К моим каналам</button>
        <h2>{editingChannel.title}</h2>
        <p className="description">{editingChannel.listed?'Канал виден покупателям':'Канал скрыт из каталога'}</p>
        <button className="secondary sellerToggle" disabled={sellerBusy} onClick={()=>toggleListing(editingChannel)}>{editingChannel.listed?'Скрыть из каталога':'Опубликовать в каталоге'}</button>
        <h3 className="detailSubtitle">Рекламные тарифы</h3>
        {sellerRates.length===0 && <p className="description">Нет тарифов. Добавьте первый ниже.</p>}
        {sellerRates.map(rate=><div className="sellerRate" key={rate.id}><div><strong>{rate.hours} ч — {money(rate.price)}</strong><small>{rate.active?'Активен':'Отключён'}</small></div><button disabled={sellerBusy} className="secondary small" onClick={()=>toggleSellerRate(rate)}>{rate.active?'Отключить':'Включить'}</button></div>)}
        <h3 className="detailSubtitle">Добавить или изменить тариф</h3>
        <p className="description">Если тариф с указанной длительностью уже существует, его цена обновится.</p>
        <label className="field">Длительность (часы)<input type="number" inputMode="numeric" min="1" max="720" value={sellerHours} onChange={e=>setSellerHours(e.target.value)}/></label>
        <label className="field">Цена (₽)<input type="number" inputMode="decimal" min="1" max="1000000" value={sellerPrice} onChange={e=>setSellerPrice(e.target.value)}/></label>
        {sellerMessage && <div className="notice">{sellerMessage}</div>}
        <button className="primary" disabled={sellerBusy} onClick={saveSellerRate}>{sellerBusy?'Сохранение...':'Сохранить тариф'}</button>
      </div></div>}
      {tab === 'profile' && <section>{isDisputeAdmin&&<button className="secondary sellerToggle" onClick={loadAdminDisputes}>Панель модерации споров</button>}{adminError&&<div className="notice">{adminError}</div>}{adminOpen&&adminDisputes&&<div className="sellerDashboard"><h3>Администрирование Reklio</h3><div className="adminTabs">{([{id:'overview',label:'Обзор'},{id:'channels',label:'Каналы'},{id:'orders',label:'Заказы'},{id:'users',label:'Пользователи'},{id:'disputes',label:'Споры'},{id:'team',label:'Команда'},{id:'audit',label:'Журнал'}] as const).filter(t=>(t.id!=='team'&&t.id!=='audit')||adminRole==='owner'||adminRole==='admin').map(t=><button key={t.id} className={adminTab===t.id?'active':''} onClick={()=>setAdminTab(t.id)}>{t.label}</button>)}</div>{adminTab==='overview'&&overview&&<div className="dashboardGrid"><div><small>Всего каналов</small><strong>{overview.channels}</strong></div><div><small>В каталоге</small><strong>{overview.listed}</strong></div><div><small>Всего заказов</small><strong>{overview.orders}</strong></div><div><small>Открытые споры</small><strong>{overview.openDisputes}</strong></div><div><small>Ошибки размещений</small><strong>{overview.failedPublications}</strong></div></div>}{adminTab==='team'&&(adminRole==='owner'||adminRole==='admin')&&<><h3 className="detailSubtitle">Команда Reklio</h3><label className="field">Telegram ID сотрудника<input type="text" inputMode="numeric" value={staffId} onChange={e=>setStaffId(e.target.value)} placeholder="123456789"/></label><label className="field">Роль<select value={staffRole} onChange={e=>setStaffRole(e.target.value)}><option value="moderator">Модератор</option>{adminRole==='owner'&&<option value="admin">Администратор</option>}</select></label><button className="primary small" onClick={saveStaff}>Добавить или изменить роль</button>{staff.map(person=><div className="sellerRate" key={person.id}><div><strong>{person.id}</strong><small>{person.role==='admin'?'Администратор':'Модератор'}</small></div><button className="secondary small" onClick={()=>revokeStaff(person.id)}>Отозвать</button></div>)}</>}{(adminTab==='users'||adminTab==='orders')&&<><h3 className="detailSubtitle">Поиск по бирже</h3><div className="adminSearch"><input placeholder="Канал, заказ, Telegram ID или имя" value={adminSearch} onChange={e=>setAdminSearch(e.target.value)} maxLength={80}/><button className="secondary small" disabled={adminSearchBusy} onClick={()=>searchAdmin()}>Найти</button></div></>} {adminTab==='users'&&<><h3 className="detailSubtitle">Пользователи</h3>{adminUsers.map(u=><div className="sellerRate" key={u.id}><div><strong>{u.name||'Пользователь'} · {u.id}</strong><small>{u.username?'@'+u.username+' · ':''}Каналов: {u.channels} · Покупок: {u.purchases}</small></div></div>)}</>} {adminTab==='orders'&&<><h3 className="detailSubtitle">Заказы</h3>{adminOrders.map(o=><div className="sellerRate" key={o.id}><div><strong>#{o.id} · {o.channel}</strong><small>{o.status} · {money(o.price)} · Покупатель: {o.buyerId}</small></div></div>)}</>} {adminTab==='audit'&&(adminRole==='owner'||adminRole==='admin')&&<><h3 className="detailSubtitle">Журнал действий</h3>{adminAudit.map(a=><div className="auditEntry" key={a.id}><strong>{a.action}</strong><small>{a.actorId} → {a.targetId??'—'} · {a.details??''}</small><small>{new Date(a.createdAt).toLocaleString('ru-RU')}</small></div>)}</>}{adminTab==='channels'&&<><h3 className="detailSubtitle">Модерация каналов</h3><p className="description">Последние 100 каналов. Скрытие убирает канал из каталога, но не отменяет действующие заказы.</p>{moderationChannels.length===0&&<p className="description">Нет каналов</p>}{moderationChannels.map(c=><div className="sellerRate" key={c.id}><div><strong>{c.title}</strong><small>{c.category} · {c.listed?'В каталоге':'Скрыт'} · {money(c.price)}</small></div><button className="secondary small" disabled={moderationBusy!==null} onClick={()=>moderateChannel(c)}>{c.listed?'Скрыть':'Показать'}</button></div>)}</>} {adminTab==='orders'&&<><h3 className="detailSubtitle">Проблемы публикаций</h3>{mtprotoStatus&&<div className="notice"><strong>MTProto-мониторинг: {mtprotoStatus.configured?(mtprotoStatus.healthy?'Проверки выполняются':'Нет недавних проверок'):'Не настроен'}</strong><p>Проверено размещений: {mtprotoStatus.checkedOrders} · Последний результат PRESENT: {mtprotoStatus.present} · MISSING: {mtprotoStatus.missing}</p><p>{mtprotoStatus.lastCheck?'Последняя проверка: '+new Date(mtprotoStatus.lastCheck.endsWith('Z')?mtprotoStatus.lastCheck:mtprotoStatus.lastCheck+'Z').toLocaleString('ru-RU'):'Проверок пока нет'}</p></div>}{publicationIssues.length===0&&<p className="description">Ошибок публикаций нет</p>}{publicationIssues.map(issue=><article className="card" key={issue.id}><strong>Заказ #{issue.id} · {issue.channel}</strong><p className="description">Статус: {issue.status}</p><p className="description">{issue.error||'Подробности ошибки отсутствуют'}</p><button className="secondary small" onClick={()=>{setReviewOrder(reviewOrder===issue.id?null:issue.id);setReviewNote('');}}>Рассмотреть</button>{reviewOrder===issue.id&&<div className="rescheduleBox"><textarea rows={3} value={reviewNote} onChange={e=>setReviewNote(e.target.value)} maxLength={500} placeholder="Что удалось проверить?"/><div className="orderActions"><button className="secondary small" disabled={moderationBusy!==null} onClick={()=>reviewPublication(issue.id,'NeedsInvestigation')}>Нужна проверка</button><button className="primary small" disabled={moderationBusy!==null} onClick={()=>reviewPublication(issue.id,'ConfirmedProblem')}>Подтвердить нарушение</button></div></div>}</article>)}</>} {adminTab==='disputes'&&<><h3 className="detailSubtitle">Споры на рассмотрении</h3>{adminDisputes.length===0&&<p>Обращений нет</p>}{adminDisputes.map(d=><article className="card" key={d.id}><strong>Заказ #{d.id} · {d.channel}</strong><p className="description">Реклама: {d.text}</p><p className="description">Причина: {d.reason}</p><p className="description">{d.isTest?'Тестовый заказ':'Заказ'} · {d.status==='Disputed'?'Ожидает решения':'Решение принято'}</p>{d.status==='Disputed'&&<div className="orderActions"><button className="secondary small" disabled={adminBusy!==null} onClick={()=>resolveAdminDispute(d.id,'RefundRecommended')}>Рекомендовать возврат</button><button className="secondary small" disabled={adminBusy!==null} onClick={()=>resolveAdminDispute(d.id,'PayoutRecommended')}>Рекомендовать выплату</button></div>}</article>)}</>}</div>}<div className="sellerDashboard"><h3>Финансы продавца</h3>{financeError && <div className="notice">{financeError}<button onClick={()=>setRetry(n=>n+1)}>Повторить</button></div>}{finance ? <><div className="dashboardGrid"><div><small>В обработке</small><strong>{money(finance.pending)}</strong></div><div><small>На проверке выплаты</small><strong>{money(finance.readyForReview)}</strong></div><div><small>К выплате продавцу*</small><strong>{money(finance.readyNet)}</strong></div><div><small>Выплачено (статус)</small><strong>{money(finance.paid)}</strong></div><div><small>Заблокировано</small><strong>{money(finance.blocked)}</strong></div><div><small>Возвраты</small><strong>{money(finance.refunded)}</strong></div></div><p>Сервисный сбор Reklio оплачивает покупатель сверх цены размещения. *Предварительный расчёт, не подтверждение доступного баланса.</p><p>{finance.note}</p></> : !financeError && <p>Загрузка данных...</p>}</div><h3 className="detailSubtitle">История начислений</h3>{finance && finance.operations.length===0 && <div className="empty">Подтверждённых нетестовых платежей пока нет</div>}{finance?.operations.map(op=><article className="card" key={op.id}><div className="cardTop"><div className="grow"><strong>Заказ #{op.id}</strong><small>{op.channel}</small></div><div className="price">{money(op.net)}</div></div><p className="description">Цена размещения {money(op.amount)} · сбор покупателя {money(op.commission)} · продавцу {money(op.net)}</p><span className="status">{({OnHold:'Удерживается',AwaitingVerification:'Проверка размещения',ReadyForManualReview:'Ожидает ручной проверки',Blocked:'Заблокировано',Paid:'Выплачено',Refunded:'Возвращено'} as Record<string,string>)[op.payoutStatus]||op.payoutStatus}</span>{op.publishedAt && <p className="description">Опубликовано: {new Date(op.publishedAt.endsWith('Z')?op.publishedAt:op.publishedAt+'Z').toLocaleString('ru-RU')}</p>}</article>)}</section>}
    </main>
    <nav>{tabs.map(({id,label,icon:Icon})=><button key={id} className={tab===id?'active':''} onClick={()=>setTab(id)}><Icon size={21}/><span>{label}</span></button>)}</nav>
  </div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
