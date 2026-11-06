import { useState } from 'react';
import { needsAttention, orderView } from './orderFlow';
import type { Order, Tab } from './types';
import { OrderCard } from './OrderCard';
import { Empty, ErrorBox, Loading } from './ui';

type Filter = 'action' | 'sales' | 'purchases' | 'all';

export function Orders({ orders, loading, error, message, onReload, onNavigate }: {
  orders: Order[];
  loading: boolean;
  error: string;
  message: string;
  onReload: (message?: string) => void;
  onNavigate: (tab: Tab) => void;
}) {
  const attention = needsAttention(orders);
  const [filter, setFilter] = useState<Filter>(attention > 0 ? 'action' : 'all');

  const shown = orders.filter(order =>
    filter === 'all' ? true
    : filter === 'action' ? orderView(order).needsYou
    : filter === 'sales' ? order.isOwner
    : !order.isOwner);

  const filters: { id: Filter; label: string }[] = [
    { id: 'action', label: `Нужно действие (${attention})` },
    { id: 'all', label: `Все (${orders.length})` },
    { id: 'purchases', label: `Покупки (${orders.filter(o => !o.isOwner).length})` },
    { id: 'sales', label: `Продажи (${orders.filter(o => o.isOwner).length})` }
  ];

  return <section>
    {message && <div className="notice success" role="status">{message}</div>}
    {error && <ErrorBox message={error} onRetry={() => onReload()}/>}
    {orders.length > 0 && <div className="orderFilters">{filters.map(item =>
      <button key={item.id} className={filter === item.id ? 'active' : ''} onClick={() => setFilter(item.id)}>{item.label}</button>)}
    </div>}
    {loading && orders.length === 0 && <Loading/>}
    {!loading && !error && orders.length === 0 && <Empty title="Заказов пока нет"
      text="Найдите канал и отправьте заявку. Или подключите свой канал, чтобы получать заказы."
      action={<div className="orderActions centered">
        <button className="primary small" onClick={() => onNavigate('catalog')}>Открыть каталог</button>
        <button className="secondary small" onClick={() => onNavigate('channels')}>Подключить канал</button>
      </div>}/>}
    {orders.length > 0 && shown.length === 0 && <Empty title={filter === 'action' ? 'Всё под контролем' : 'Здесь пока пусто'}
      text={filter === 'action' ? 'Сейчас от вас ничего не требуется. Мы напишем в Telegram, когда что-то изменится.' : undefined}/>}
    {shown.map(order => <OrderCard key={order.id} order={order} onChanged={onReload}/>)}
  </section>;
}
