import { useEffect, useState } from 'react';
import { ClipboardList, Megaphone, Search, UserRound } from 'lucide-react';
import { get } from './api';
import { errorText } from './format';
import { needsAttention } from './orderFlow';
import { startChannelId, webApp } from './telegram';
import type { Me, Order, Subscription, Tab } from './types';
import { Catalog } from './Catalog';
import { Channels } from './Channels';
import { Orders } from './Orders';
import { Profile } from './Profile';
import { Welcome, welcomeSeen } from './Welcome';

const TABS: { id: Tab; label: string; title: string; subtitle: string; icon: typeof Search }[] = [
  { id: 'catalog', label: 'Каталог', title: 'Найти рекламу', subtitle: 'Выберите канал для вашего объявления', icon: Search },
  { id: 'orders', label: 'Заказы', title: 'Мои заказы', subtitle: 'Покупки и продажи рекламы', icon: ClipboardList },
  { id: 'channels', label: 'Каналы', title: 'Мои каналы', subtitle: 'Продавайте рекламу в своих каналах', icon: Megaphone },
  { id: 'profile', label: 'Профиль', title: 'Профиль', subtitle: 'Тариф, платежи и помощь', icon: UserRound }
];

const ORDERS_POLL_MS = 15000;

export function App() {
  const [tab, setTab] = useState<Tab>('catalog');
  // A buyer arriving by an owner's link should see that channel, not a greeting.
  const [welcome, setWelcome] = useState(() => !welcomeSeen() && startChannelId() === null);
  const [me, setMe] = useState<Me | null>(null);
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  const [orders, setOrders] = useState<Order[]>([]);
  const [ordersLoading, setOrdersLoading] = useState(true);
  const [ordersError, setOrdersError] = useState('');
  const [orderMessage, setOrderMessage] = useState('');

  async function reloadOrders(message?: string) {
    if (message !== undefined) setOrderMessage(message);
    try {
      setOrders(await get<Order[]>('/api/orders'));
      setOrdersError('');
    } catch (e) {
      setOrdersError(errorText(e, 'Не удалось загрузить заказы'));
    } finally {
      setOrdersLoading(false);
    }
  }

  useEffect(() => {
    const app = webApp();
    app?.ready();
    app?.expand();
    get<Me>('/api/me').then(setMe).catch(() => {});
    get<Subscription>('/api/subscription').then(setSubscription).catch(() => {});
  }, []);

  // Keeps the attention badge honest on every tab; the orders tab needs fresher data.
  useEffect(() => {
    void reloadOrders();
    const timer = window.setInterval(() => void reloadOrders(), tab === 'orders' ? ORDERS_POLL_MS : ORDERS_POLL_MS * 4);
    return () => window.clearInterval(timer);
  }, [tab]);

  const current = TABS.find(item => item.id === tab)!;
  const attention = needsAttention(orders);

  function navigate(next: Tab) {
    setTab(next);
    setOrderMessage('');
    window.scrollTo(0, 0);
  }

  return <div className="app">
    {welcome && <Welcome onClose={next => { setWelcome(false); if (next) navigate(next); }}/>}
    <header>
      <div className="brandLine">
        <img src="/reklio-mark.svg" alt="" className="brandMark"/>
        <div className="brandName">Rekli<span>o</span></div>
      </div>
      <h1>{current.title}</h1>
      <p>{current.subtitle}</p>
    </header>
    <main>
      {tab === 'catalog' && <Catalog onOrderCreated={message => { navigate('orders'); void reloadOrders(message); }}/>}
      {tab === 'orders' && <Orders orders={orders} loading={ordersLoading} error={ordersError} message={orderMessage} onReload={message => void reloadOrders(message)} onNavigate={navigate}/>}
      {tab === 'channels' && <Channels subscription={subscription}/>}
      {tab === 'profile' && <Profile me={me} orders={orders} subscription={subscription} onSubscriptionChange={setSubscription}/>}
    </main>
    <nav>{TABS.map(({ id, label, icon: Icon }) => <button key={id} className={tab === id ? 'active' : ''} onClick={() => navigate(id)}>
      <span className="navIcon"><Icon size={21}/>{id === 'orders' && attention > 0 && <b className="navBadge">{attention}</b>}</span>
      <span>{label}</span>
    </button>)}</nav>
  </div>;
}
