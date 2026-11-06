import { useEffect, useState } from 'react';
import { get } from './api';
import { CHECKOUT_OPENED, openCheckout } from './checkout';
import { date, errorText, money, parseUtc } from './format';
import { webApp } from './telegram';
import type { Me, Order, PaymentRecord, Subscription } from './types';
import { Admin } from './Admin';
import { OfferNote } from './ui';
import { HowItWorks } from './Welcome';

const PAYMENT_LABELS: Record<string, string> = { paid: 'Оплачено', review: 'На проверке, напишите в поддержку', pending: 'Ожидает оплаты' };

export function Profile({ me, orders, subscription, onSubscriptionChange }: {
  me: Me | null;
  orders: Order[];
  subscription: Subscription | null;
  onSubscriptionChange: (value: Subscription) => void;
}) {
  const [payments, setPayments] = useState<PaymentRecord[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const [admin, setAdmin] = useState(false);
  const user = webApp()?.initDataUnsafe?.user;

  const loadPayments = () => get<PaymentRecord[]>('/api/payments/my').then(setPayments).catch(() => {});
  useEffect(() => { void loadPayments(); }, []);

  // The payment page opens outside the app, so keep checking while a recent checkout may complete.
  useEffect(() => {
    const recent = payments.some(p => p.status === 'pending' && Date.now() - parseUtc(p.createdAt).getTime() < 30 * 60 * 1000);
    if (!recent) return;
    const timer = window.setTimeout(() => {
      void loadPayments();
      get<Subscription>('/api/subscription').then(onSubscriptionChange).catch(() => {});
    }, 5000);
    return () => window.clearTimeout(timer);
  }, [payments]);

  async function buyPro() {
    setBusy(true);
    setNotice('');
    try {
      await openCheckout('/api/payments/checkout/pro');
      setNotice(CHECKOUT_OPENED);
      await loadPayments();
    } catch (e) {
      setNotice(errorText(e, 'Не удалось открыть оплату'));
    } finally {
      setBusy(false);
    }
  }

  const purchases = orders.filter(o => !o.isOwner);
  const sales = orders.filter(o => o.isOwner);
  const completed = (list: Order[]) => list.filter(o => o.status === 'Deleted' || o.status === 'Published').length;

  return <div className="profileOverview">
    <div className="profileIdentity">
      <div className="profileAvatar">{(user?.first_name || 'R').slice(0, 1).toUpperCase()}</div>
      <div>
        <h2>{user?.first_name || 'Пользователь Reklio'}</h2>
        <p>{user?.username ? '@' + user.username : 'Аккаунт Telegram'} · {subscription?.plan ?? 'FREE'}</p>
      </div>
    </div>

    <section className="card planCard">
      <h3>Тариф {subscription?.plan ?? 'FREE'}</h3>
      {subscription?.isPro && subscription.expiresAt
        ? <p>Действует до {date(subscription.expiresAt)}. Новая оплата продлит его на 30 дней.</p>
        : <p>FREE: один канал и статистика за 7 дней. Бесплатно и без ограничения по времени.</p>}
      <p>PRO: до 10 каналов и статистика за 30 и 90 дней. Автопродления нет.</p>
      <button className="primary" type="button" disabled={!subscription?.checkoutAvailable || busy} onClick={() => void buyPro()}>
        {subscription?.isPro ? 'Продлить PRO' : 'Подключить PRO'} · {money(subscription?.proPriceRub ?? 399)} / 30 дней
      </button>
      <OfferNote/>
      {notice && <p className="hint" role="status">{notice}</p>}
      {subscription && !subscription.checkoutAvailable && <p className="hint">{subscription.checkoutMessage}</p>}
      {payments.length > 0 && <details className="channelMore">
        <summary>Мои платежи</summary>
        {payments.slice(0, 8).map(p => <div className="profileQuickLine" key={p.orderUuid}>
          <span>{p.product === 'pro30' ? 'PRO' : 'Продвижение'} · {money(p.amountKopecks / 100)}</span>
          <small>{PAYMENT_LABELS[p.status] ?? p.status}</small>
        </div>)}
      </details>}
    </section>

    <div className="profileStats">
      <div><span>Покупок</span><strong>{purchases.length}</strong></div>
      <div><span>Покупок выполнено</span><strong>{completed(purchases)}</strong></div>
      <div><span>Продаж</span><strong>{sales.length}</strong></div>
      <div><span>Продаж выполнено</span><strong>{completed(sales)}</strong></div>
    </div>

    <details className="card aboutPanel"><summary>Как работает Reklio</summary><HowItWorks/></details>

    <section className="card">
      <h3>Помощь</h3>
      <p className="description">Вопросы и проблемы: support@reklio.site. В письме укажите номер заказа или ваш Telegram ID{me ? `: ${me.id}` : ''}.</p>
      <p className="hint">
        <a href="https://reklio.site/terms" target="_blank" rel="noopener noreferrer">Оферта</a> · <a href="https://reklio.site/advertising-rules" target="_blank" rel="noopener noreferrer">Правила размещения</a> · <a href="https://reklio.site/privacy" target="_blank" rel="noopener noreferrer">Конфиденциальность</a>
      </p>
    </section>

    {me?.isDisputeAdmin && <button className="secondary sellerToggle" onClick={() => setAdmin(!admin)}>{admin ? 'Скрыть панель модерации' : 'Панель модерации'}</button>}
    {admin && me && <Admin role={me.adminRole}/>}
  </div>;
}
