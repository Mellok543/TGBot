import { useEffect, useState } from 'react';
import { get } from './api';
import { CHECKOUT_OPENED, openCheckout } from './checkout';
import { date, errorText, money, parseUtc } from './format';
import type { PaymentRecord, Subscription } from './types';
import { OfferNote } from './ui';

const PAYMENT_LABELS: Record<string, string> = { paid: 'Оплачено', review: 'На проверке, напишите в поддержку', pending: 'Ожидает оплаты' };

export function PlanCard({ subscription, onChange }: { subscription: Subscription | null; onChange: (value: Subscription) => void }) {
  const [payments, setPayments] = useState<PaymentRecord[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');

  const loadPayments = () => get<PaymentRecord[]>('/api/payments/my').then(setPayments).catch(() => {});
  useEffect(() => { void loadPayments(); }, []);

  // The payment page opens outside the app, so keep checking while a recent checkout may complete.
  useEffect(() => {
    const recent = payments.some(p => p.status === 'pending' && Date.now() - parseUtc(p.createdAt).getTime() < 30 * 60 * 1000);
    if (!recent) return;
    const timer = window.setTimeout(() => {
      void loadPayments();
      get<Subscription>('/api/subscription').then(onChange).catch(() => {});
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

  return <section className="card planCard">
    <h3>Тариф {subscription?.plan ?? 'FREE'}</h3>
    {subscription?.isPro && subscription.expiresAt
      ? <p>Действует до {date(subscription.expiresAt)}. Новая оплата продлит его на 30 дней.</p>
      : <p>FREE: один канал и статистика за 7 дней. Бесплатно и без ограничения по времени.</p>}
    <p>PRO: до 15 каналов и статистика за 30 и 90 дней. Автопродления нет.</p>
    <button className="primary" type="button" disabled={!subscription?.checkoutAvailable || busy} onClick={() => void buyPro()}>
      {subscription?.isPro ? 'Продлить PRO' : 'Подключить PRO'} · {money(subscription?.proPriceRub ?? 399)} / 30 дней
    </button>
    <OfferNote/>
    {notice && <p className="hint" role="status">{notice}</p>}
    {subscription && !subscription.checkoutAvailable && <p className="hint">{subscription.checkoutMessage}</p>}
    {payments.length > 0 && <details className="channelMore" open={payments.some(p => p.status === 'review')}>
      <summary>Мои платежи</summary>
      {payments.slice(0, 8).map(p => <div className="profileQuickLine" key={p.orderUuid}>
        <span>{p.product === 'pro30' ? 'PRO' : 'Продвижение'} · {money(p.amountKopecks / 100)}</span>
        <small>{PAYMENT_LABELS[p.status] ?? p.status}</small>
      </div>)}
    </details>}
  </section>;
}
