import { useEffect, useState } from 'react';
import { get, post } from './api';
import { CHECKOUT_OPENED, openCheckout } from './checkout';
import { blockText, duration, errorText, money } from './format';
import { copyText } from './telegram';
import type { ChannelAnalytics, MyChannel, PaymentDetails, SellerRate } from './types';
import { LineChart, OfferNote } from './ui';

const CATEGORIES = ['Технологии', 'Игры', 'Новости', 'Бизнес', 'Финансы', 'Образование', 'Развлечения', 'Юмор', 'Спорт', 'Путешествия', 'Авто', 'Красота и стиль', 'Здоровье', 'Кулинария', 'Музыка', 'Кино', 'Другое'];
const DURATIONS = [{ hours: 24, label: '24 часа' }, { hours: 48, label: '48 часов' }, { hours: 72, label: '3 дня' }, { hours: 168, label: '7 дней' }, { hours: 0, label: 'Навсегда' }];
const EMPTY_DETAILS: PaymentDetails = { cardNumber: '', phone: '', bank: '' };

function Analytics({ channelId, isPro }: { channelId: number; isPro: boolean }) {
  const periods = isPro ? [7, 30, 90] : [7];
  const [days, setDays] = useState(periods[periods.length - 1]);
  const [data, setData] = useState<ChannelAnalytics | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    setData(null);
    setError('');
    get<ChannelAnalytics>(`/api/my-channels/${channelId}/analytics?days=${days}`)
      .then(value => { if (active) setData(value); })
      .catch(e => { if (active) setError(errorText(e, 'Не удалось загрузить аналитику')); });
    return () => { active = false; };
  }, [channelId, days]);

  const graphs = [
    { field: 'subscribers', label: 'Подписчики', color: '#80b5ff' },
    { field: 'avgViews', label: 'Просмотры публикаций', color: '#63dec9' }
  ] as const;

  return <details className="analyticsPanel">
    <summary>Аналитика канала</summary>
    {!isPro && <p className="hint">На FREE доступна статистика за 7 дней, в PRO — за 30 и 90.</p>}
    <div className="analyticsPeriods">{periods.map(d => <button key={d} className={days === d ? 'active' : ''} onClick={() => setDays(d)}>{d} дней</button>)}</div>
    {!data && !error && <p className="description">Загружаем…</p>}
    {error && <p className="notice">{error}</p>}
    {data && <>
      <div className="profileStats">
        <div><span>Заказов</span><strong>{data.orders}</strong></div>
        <div><span>Размещений</span><strong>{data.published}</strong></div>
        <div><span>Ожидают решения</span><strong>{data.pending}</strong></div>
      </div>
      {graphs.map(graph => {
        const points = data.points.filter(p => p[graph.field] !== null);
        const values = points.map(p => p[graph.field] as number);
        return <div className="analyticsGraph" key={graph.field}>
          <h4>{graph.label}</h4>
          {points.length < 2 ? <p className="description">Недостаточно данных за этот период.</p> : <LineChart label={graph.label} color={graph.color}
            values={values} from={points[0].day} to={points[points.length - 1].day} caption={`Последнее: ${values[values.length - 1].toLocaleString('ru-RU')}`}/>}
        </div>;
      })}
    </>}
  </details>;
}

export function ChannelEditor({ channel, isPro, justConnected, onChange, onBack }: {
  channel: MyChannel;
  isPro: boolean;
  justConnected: boolean;
  onChange: (channel: MyChannel) => void;
  onBack: () => void;
}) {
  const [rates, setRates] = useState<SellerRate[]>([]);
  const [details, setDetails] = useState<PaymentDetails>(EMPTY_DETAILS);
  const [savedDetails, setSavedDetails] = useState(false);
  const [hours, setHours] = useState('24');
  const [price, setPrice] = useState('');
  const [alias, setAlias] = useState(channel.alias || '');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState(justConnected ? 'Канал подключён! Осталось выполнить шаги ниже, и он появится в каталоге.' : '');
  const [promotionPrice, setPromotionPrice] = useState<number | null>(null);
  const [promotionNotice, setPromotionNotice] = useState('');

  useEffect(() => {
    get<SellerRate[]>(`/api/my-channels/${channel.id}/rates`).then(setRates).catch(e => setMessage(errorText(e, 'Не удалось загрузить тарифы')));
    get<{ cardNumber: string | null; phone: string | null; bank: string | null }>(`/api/my-channels/${channel.id}/direct-payment`)
      .then(value => {
        setDetails({ cardNumber: value.cardNumber || '', phone: value.phone || '', bank: value.bank || '' });
        setSavedDetails(Boolean(value.bank));
      })
      .catch(e => setMessage(errorText(e, 'Не удалось загрузить реквизиты')));
    get<Record<string, number>>('/api/service-prices').then(value => setPromotionPrice(value.promotion7 ?? null)).catch(() => {});
  }, [channel.id]);

  async function act(action: () => Promise<void>) {
    setBusy(true);
    setMessage('');
    try { await action(); }
    catch (e) { setMessage(errorText(e, 'Не удалось сохранить')); }
    finally { setBusy(false); }
  }

  const reloadRates = async () => setRates(await get<SellerRate[]>(`/api/my-channels/${channel.id}/rates`));

  const saveDetails = () => act(async () => {
    const saved = await post<{ cardNumber: string | null; phone: string | null; bank: string | null }>(`/api/my-channels/${channel.id}/direct-payment`, details);
    setDetails({ cardNumber: saved.cardNumber || '', phone: saved.phone || '', bank: saved.bank || '' });
    setSavedDetails(Boolean(saved.bank));
    setMessage(saved.bank ? 'Реквизиты сохранены.' : 'Реквизиты удалены.');
  });

  const saveRate = () => act(async () => {
    const value = Number(price);
    if (!Number.isFinite(value) || value < 1 || value > 1000000) throw new Error('Укажите цену от 1 до 1 000 000 ₽');
    await post(`/api/my-channels/${channel.id}/rates`, { hours: Number(hours), price: value });
    await reloadRates();
    setPrice('');
    setMessage('Тариф сохранён.');
  });

  const toggleRate = (rate: SellerRate) => act(async () => {
    await post(`/api/my-channels/${channel.id}/rates/${rate.id}/toggle`);
    await reloadRates();
  });

  const saveCategory = (category: string) => act(async () => {
    await post(`/api/my-channels/${channel.id}/category`, { category });
    onChange({ ...channel, category });
  });

  const toggleListing = () => act(async () => {
    await post(`/api/my-channels/${channel.id}/listing`, { listed: !channel.listed });
    onChange({ ...channel, listed: !channel.listed });
    setMessage(channel.listed ? 'Канал скрыт из каталога.' : 'Канал опубликован! Теперь его видят покупатели.');
  });

  const saveAlias = () => act(async () => {
    const saved = await post<{ alias: string }>(`/api/my-channels/${channel.id}/alias`, { alias });
    onChange({ ...channel, alias: saved.alias });
    setMessage('Адрес ссылки сохранён.');
  });

  const buyPromotion = () => act(async () => {
    setPromotionNotice('');
    await openCheckout('/api/payments/checkout/promotion/' + channel.id);
    setPromotionNotice(CHECKOUT_OPENED);
  });

  const hasCategory = CATEGORIES.includes(channel.category);
  const hasRate = rates.some(r => r.active);
  const ready = hasCategory && hasRate && savedDetails;
  const buyerLink = channel.alias ? 'https://reklio.site/ad/' + channel.alias : 'https://t.me/ReklioBot?startapp=ad_' + channel.id;

  const steps = [
    { done: hasCategory, label: 'Выберите категорию' },
    { done: hasRate, label: 'Добавьте тариф' },
    { done: savedDetails, label: 'Укажите реквизиты для оплаты' },
    { done: channel.listed, label: 'Опубликуйте канал' }
  ];

  return <div className="bookingOverlay"><div className="bookingPanel">
    <button className="back" onClick={onBack}>← К моим каналам</button>
    <h2>{channel.title}</h2>
    {channel.blocked && <div className="notice" role="alert">{blockText(channel.blockedUntil)} администрацией Reklio. Публикация в каталоге и новые заказы недоступны.</div>}
    {message && <div className="notice" role="status">{message}</div>}

    {!channel.listed && !channel.blocked && <div className="ownerChecklist">
      <strong>Чтобы начать продавать рекламу</strong>
      {steps.map(step => <div key={step.label} className={step.done ? 'done' : ''}><span>{step.done ? '✓' : '○'}</span> {step.label}</div>)}
    </div>}

    <section className="card">
      <h3>Категория</h3>
      <label className="field">Тема канала
        <select value={hasCategory ? channel.category : ''} disabled={busy} onChange={e => void saveCategory(e.target.value)}>
          <option value="" disabled>Выберите категорию</option>
          {CATEGORIES.map(item => <option key={item} value={item}>{item}</option>)}
        </select>
      </label>
    </section>

    <section className="card">
      <h3>Тарифы</h3>
      <p className="description">Сколько стоит реклама и как долго пост остаётся в канале. По истечении срока бот удалит пост сам.</p>
      {rates.map(rate => <div className="sellerRate" key={rate.id}>
        <div><strong>{duration(rate.hours)} — {money(rate.price)}</strong><small>{rate.active ? 'Показывается покупателям' : 'Отключён'}</small></div>
        <button disabled={busy} className="secondary small" onClick={() => void toggleRate(rate)}>{rate.active ? 'Отключить' : 'Включить'}</button>
      </div>)}
      <div className="rateForm">
        <label className="field">Срок
          <select value={hours} onChange={e => setHours(e.target.value)}>{DURATIONS.map(item => <option key={item.hours} value={item.hours}>{item.label}</option>)}</select>
        </label>
        <label className="field">Цена, ₽
          <input type="number" inputMode="numeric" min="1" max="1000000" value={price} onChange={e => setPrice(e.target.value)} placeholder="1000"/>
        </label>
      </div>
      <button className="primary small" disabled={busy || !price} onClick={() => void saveRate()}>Сохранить тариф</button>
      <p className="hint">Если тариф с таким сроком уже есть, его цена обновится.</p>
    </section>

    <section className="card">
      <h3>Реквизиты для оплаты</h3>
      <p className="description">Покупатель переведёт деньги напрямую вам. Он увидит реквизиты только после того, как вы одобрите его заявку.</p>
      <label className="field">Номер карты
        <input inputMode="numeric" autoComplete="off" placeholder="0000 0000 0000 0000" value={details.cardNumber} maxLength={23} onChange={e => setDetails({ ...details, cardNumber: e.target.value })}/>
      </label>
      <label className="field">Телефон для СБП
        <input type="tel" autoComplete="off" placeholder="+7 900 123-45-67" value={details.phone} maxLength={20} onChange={e => setDetails({ ...details, phone: e.target.value })}/>
      </label>
      <label className="field">Банк
        <input autoComplete="off" placeholder="Например, Т-Банк" value={details.bank} maxLength={64} onChange={e => setDetails({ ...details, bank: e.target.value })}/>
      </label>
      <button className="primary small" type="button" disabled={busy} onClick={() => void saveDetails()}>Сохранить реквизиты</button>
      <p className="hint">Достаточно карты или телефона, банк указать обязательно. Никогда не указывайте срок действия карты, CVV и коды из SMS. Чтобы удалить реквизиты, очистите поля и сохраните.</p>
    </section>

    {!channel.blocked && <>
      <button className="primary sellerToggle" disabled={busy || (!channel.listed && !ready)} onClick={() => void toggleListing()}>{channel.listed ? 'Скрыть из каталога' : 'Опубликовать в каталоге'}</button>
      {!channel.listed && !ready && <p className="hint">Кнопка станет доступна, когда будут выполнены шаги выше.</p>}
    </>}

    {channel.listed && hasRate && <section className="channelDirectLinks">
      <h3>🔗 Ссылка для покупателей</h3>
      <p>Поделитесь ей: покупатель сразу попадёт на оформление рекламы в вашем канале.</p>
      <div className="channelAliasPreview"><span>{buyerLink.replace('https://', '')}</span></div>
      <button className="secondary" type="button" onClick={() => void copyText(buyerLink).then(ok => setMessage(ok ? 'Ссылка скопирована.' : 'Не удалось скопировать, выделите ссылку вручную.'))}>Скопировать ссылку</button>
      <div className="channelAliasEditor">
        <span className="channelAliasPrefix">reklio.site/ad/</span>
        <input aria-label="Короткий адрес" autoComplete="off" maxLength={32} value={alias} placeholder="мой_канал" onChange={e => setAlias(e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, ''))}/>
      </div>
      <button className="secondary" type="button" disabled={busy || alias.length < 4 || alias === channel.alias} onClick={() => void saveAlias()}>Сохранить короткий адрес</button>
      <small>От 4 до 32 латинских букв, цифр или _. При смене адреса старая короткая ссылка перестанет работать.</small>
    </section>}

    {channel.listed && !channel.blocked && <section className="channelPromotionPanel">
      <h3>🚀 Продвижение</h3>
      <p>Карточка канала выделяется и показывается выше остальных в каталоге на 7 дней.</p>
      <button className="primary" type="button" disabled={busy} onClick={() => void buyPromotion()}>Оплатить{promotionPrice ? ' · ' + money(promotionPrice) : ''}</button>
      <OfferNote/>
      {promotionNotice && <small className="hint">{promotionNotice}</small>}
    </section>}

    <Analytics channelId={channel.id} isPro={isPro}/>
  </div></div>;
}
