import { useEffect, useState } from 'react';
import { get, getOrderImageUrl, post } from './api';
import { dateTime, duration, errorText, formatCard, money, parseUtc } from './format';
import { orderView, type OrderAction } from './orderFlow';
import { openChannelInTelegram } from './telegram';
import type { DirectPayment, Order } from './types';
import { ConfirmButton, CopyRow } from './ui';

const HOUR = 60 * 60 * 1000;

function toLocalInput(time: number): string {
  const d = new Date(time);
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset());
  return d.toISOString().slice(0, 16);
}

export function OrderCard({ order, onChanged }: { order: Order; onChanged: (message: string) => void }) {
  const view = orderView(order);
  const [open, setOpen] = useState(view.needsYou);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [form, setForm] = useState<'dispute' | 'reschedule' | null>(null);
  const [reason, setReason] = useState('');
  const [newTime, setNewTime] = useState('');
  const [payment, setPayment] = useState<DirectPayment | null>(null);
  const canPay = view.actions.includes('pay');
  const [imageUrl, setImageUrl] = useState('');

  useEffect(() => {
    if (!open || !order.hasImage) return;
    let active = true;
    let url = '';
    getOrderImageUrl(order.id)
      .then(value => { url = value; if (active) setImageUrl(value); else URL.revokeObjectURL(value); })
      .catch(() => {});
    return () => { active = false; if (url) URL.revokeObjectURL(url); };
  }, [open, order.hasImage, order.id]);

  useEffect(() => {
    if (!canPay) return;
    let active = true;
    get<DirectPayment>('/api/orders/' + order.id + '/direct-payment')
      .then(value => { if (active) setPayment(value); })
      .catch(e => { if (active) setError(errorText(e, 'Не удалось загрузить реквизиты')); });
    return () => { active = false; };
  }, [canPay, order.id]);

  async function run(path: string, body: unknown, done: string) {
    setBusy(true);
    setError('');
    try {
      await post('/api/orders/' + order.id + path, body);
      setForm(null);
      onChanged(done);
    } catch (e) {
      setError(errorText(e, 'Не удалось выполнить действие'));
    } finally {
      setBusy(false);
    }
  }

  function submitReschedule() {
    const at = new Date(newTime).getTime();
    if (!newTime || Number.isNaN(at) || at <= Date.now() + HOUR) {
      setError('Выберите время минимум через час.');
      return;
    }
    void run('/reschedule', { scheduledAt: new Date(at).toISOString() }, 'Новое время отправлено владельцу на согласование.');
  }

  const has = (action: OrderAction) => view.actions.includes(action);
  const payable = payment && (payment.cardNumber || payment.phone);

  return <article className={'card orderCard tone-' + view.tone}>
    <button type="button" className="orderHead" aria-expanded={open} onClick={() => setOpen(!open)}>
      <div className="grow">
        <strong>{order.channel}</strong>
        <small>Заказ #{order.id} · {order.isOwner ? 'продажа' : 'покупка'}</small>
      </div>
      <div className="orderHeadSide">
        <div className="price">{money(order.price)}</div>
        <span className={'statusBadge tone-' + view.tone}>{view.label}</span>
      </div>
    </button>

    <div className="orderState">
      <strong>{view.title}</strong>
      <p>{view.text}</p>
    </div>

    {order.publicationIssue && !['PublishFailed', 'EarlyDeletionPending'].includes(order.status) && <p className="hint">{order.publicationIssue}</p>}
    {order.disputeReason && ['Disputed', 'DisputeResolved'].includes(order.status) && <p className="hint">Причина спора: {order.disputeReason}</p>}
    {error && <div className="notice" role="alert">{error}</div>}

    {has('pay') && <div className="payBox">
      {!payment && !error && <p className="hint">Загружаем реквизиты…</p>}
      {payment?.channelBlocked && <p>Канал заблокирован администрацией Reklio. Не переводите деньги по этому заказу.</p>}
      {payment && !payment.channelBlocked && !payable && <p>Владелец ещё не указал реквизиты. Напишите ему или подождите: мы не можем показать их раньше.</p>}
      {payable && <>
        <CopyRow label="Сумма перевода" value={money(payment.amount)} copyValue={String(payment.amount)}/>
        {payment.cardNumber && <CopyRow label="Номер карты" value={formatCard(payment.cardNumber)} copyValue={payment.cardNumber}/>}
        {payment.phone && <CopyRow label="Телефон для СБП" value={payment.phone}/>}
        {payment.bank && <div className="copyRow"><div><small>Банк</small><strong>{payment.bank}</strong></div></div>}
        <ConfirmButton className="primary" disabled={busy} question="Вы уже перевели деньги? Владелец получит уведомление и проверит поступление."
          onConfirm={() => void run('/direct-payment/claim', {}, 'Владелец получил сообщение о переводе. Мы сообщим, когда он подтвердит оплату.')}>
          Я перевёл деньги
        </ConfirmButton>
      </>}
    </div>}

    <div className="orderActions">
      {has('approve') && <button type="button" className="primary small" disabled={busy} onClick={() => void run('/approve', {}, 'Заявка одобрена. Покупатель видит реквизиты и может оплатить.')}>Одобрить</button>}
      {has('reject') && <ConfirmButton className="secondary small" disabled={busy} question="Отклонить заявку? Покупатель получит уведомление."
        onConfirm={() => void run('/reject', {}, 'Заявка отклонена.')}>Отклонить</ConfirmButton>}
      {has('confirmPayment') && <ConfirmButton className="primary small" disabled={busy} question="Деньги действительно пришли на ваш счёт? После подтверждения реклама уйдёт в публикацию."
        onConfirm={() => void run('/direct-payment/confirm', {}, 'Оплата подтверждена.')}>Деньги получены</ConfirmButton>}
      {has('denyPayment') && <ConfirmButton className="secondary small" disabled={busy} question="Вы проверили банк и денег нет? Покупатель сможет открыть спор, если перевод был."
        onConfirm={() => void run('/direct-payment/reject', {}, 'Покупатель получил сообщение, что оплата не найдена.')}>Денег нет</ConfirmButton>}
      {has('republish') && <button type="button" className="primary small" disabled={busy} onClick={() => void run('/republish', {}, 'Реклама скоро выйдет снова. Срок размещения начнётся заново.')}>Опубликовать снова</button>}
      {has('reschedule') && <button type="button" className="secondary small" disabled={busy} onClick={() => { setForm(form === 'reschedule' ? null : 'reschedule'); setNewTime(toLocalInput(Date.now() + 2 * HOUR)); }}>Выбрать новое время</button>}
      {has('dispute') && <button type="button" className="secondary small" disabled={busy} onClick={() => setForm(form === 'dispute' ? null : 'dispute')}>Открыть спор</button>}
      {has('cancel') && <ConfirmButton className="secondary small" disabled={busy}
        question={order.paymentStatus === 'DirectPaid' ? 'Отменить оплаченный заказ? Вам нужно будет вернуть деньги покупателю.' : 'Отменить заявку? Это нельзя будет вернуть.'}
        onConfirm={() => void run('/cancel', {}, 'Заказ отменён.')}>{order.isOwner && order.paymentStatus === 'DirectPaid' ? 'Отменить и вернуть деньги' : 'Отменить'}</ConfirmButton>}
    </div>

    {form === 'reschedule' && <div className="rescheduleBox">
      <label className="field">Новая дата и время
        <input type="datetime-local" value={newTime} min={toLocalInput(Date.now() + HOUR)} onChange={e => setNewTime(e.target.value)}/>
      </label>
      <button type="button" className="primary small" disabled={busy} onClick={submitReschedule}>Отправить на согласование</button>
    </div>}

    {form === 'dispute' && <div className="rescheduleBox">
      <label className="field">Что произошло?
        <textarea rows={3} maxLength={512} value={reason} onChange={e => setReason(e.target.value)} placeholder="Например: перевёл деньги такого-то числа, владелец не отвечает"/>
      </label>
      <p className="hint">Решение примет администрация Reklio. Она может заблокировать канал, но вернуть деньги не может: они переводились напрямую владельцу. Сохраните чек перевода.</p>
      <button type="button" className="primary small" disabled={busy || reason.trim().length < 10}
        onClick={() => void run('/dispute', { reason: reason.trim() }, 'Спор открыт. Администрация рассмотрит его и сообщит о решении.')}>Отправить</button>
      {reason.trim().length < 10 && <p className="hint">Опишите проблему подробнее, минимум 10 символов.</p>}
    </div>}

    {open && <div className="orderDetails">
      {imageUrl && <img className="adImagePreview" src={imageUrl} alt="Картинка рекламы"/>}
      <strong>Текст рекламы</strong><p>{order.text || 'Текст не указан'}</p>
      <strong>Публикация</strong><p>{order.scheduledAt ? dateTime(order.scheduledAt) : 'Не назначена'}</p>
      <strong>Срок размещения</strong><p>{duration(order.durationHours)}</p>
      {order.publishedAt && <><strong>Опубликовано</strong><p>{dateTime(order.publishedAt)}</p></>}
      {order.publishedAt && order.durationHours !== null && order.durationHours > 0 && <>
        <strong>Будет удалено</strong><p>{dateTime(new Date(parseUtc(order.publishedAt).getTime() + order.durationHours * HOUR).toISOString())}</p>
      </>}
      {order.counterpart && <>
        <strong>{order.isOwner ? 'Покупатель' : 'Владелец канала'}</strong>
        <p><button type="button" className="inlineLink" onClick={() => openChannelInTelegram(order.counterpart!)}>Написать @{order.counterpart}</button></p>
      </>}
    </div>}
    <button type="button" className="orderDetailsButton" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Скрыть подробности' : 'Подробнее'}</button>
  </article>;
}
