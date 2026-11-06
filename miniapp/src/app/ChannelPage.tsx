import { useEffect, useMemo, useState } from 'react';
import { ArrowUpRight } from 'lucide-react';
import { get, post, uploadOrderImage } from './api';
import { count, cpm, duration, errorText, money, parseUtc } from './format';
import { prepareImage } from './image';
import { openChannelInTelegram } from './telegram';
import type { Channel, ChannelReview, HistoryPoint, Rate } from './types';
import { ChannelAvatar, ErrorBox, LineChart } from './ui';

const HOUR = 60 * 60 * 1000;
const DAY = 24 * HOUR;
const TEXT_LIMIT = 4096;
const CAPTION_LIMIT = 1024;
const pad = (n: number) => String(n).padStart(2, '0');
const HOURS = Array.from({ length: 24 }, (_, i) => pad(i));
const MINUTES = Array.from({ length: 12 }, (_, i) => pad(i * 5));
const dayValue = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

function upcomingDays(): string[] {
  return Array.from({ length: 90 }, (_, i) => {
    const d = new Date();
    d.setHours(12, 0, 0, 0);
    d.setDate(d.getDate() + i);
    return dayValue(d);
  });
}

export function ChannelPage({ channel, voted = false, onVote, onBack, onOrderCreated }: {
  channel: Channel;
  voted?: boolean;
  onVote?: () => Promise<void>;
  onBack: () => void;
  onOrderCreated: (message: string) => void;
}) {
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [rates, setRates] = useState<Rate[] | null>(null);
  const [history, setHistory] = useState<HistoryPoint[]>([]);
  const [reviews, setReviews] = useState<ChannelReview[]>([]);
  const [reserved, setReserved] = useState<number[]>([]);
  const [rateId, setRateId] = useState<number | null>(null);
  const [text, setText] = useState('');
  const [day, setDay] = useState('');
  const [hour, setHour] = useState('12');
  const [minute, setMinute] = useState('00');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [voteBusy, setVoteBusy] = useState(false);
  const [image, setImage] = useState<Blob | null>(null);
  const [imageUrl, setImageUrl] = useState('');
  const [imageBusy, setImageBusy] = useState(false);
  const textLimit = image ? CAPTION_LIMIT : TEXT_LIMIT;

  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl); }, [imageUrl]);

  async function chooseImage(file: File | undefined) {
    if (!file) return;
    setImageBusy(true);
    setMessage('');
    try {
      const prepared = await prepareImage(file);
      setImage(prepared);
      setImageUrl(URL.createObjectURL(prepared));
    } catch (e) {
      setMessage(errorText(e, 'Не удалось обработать картинку'));
    } finally {
      setImageBusy(false);
    }
  }

  useEffect(() => {
    let active = true;
    get<Rate[]>('/api/channels/' + channel.id + '/rates')
      .then(value => { if (active) setRates(value); })
      .catch(e => { if (active) { setRates([]); setMessage(errorText(e, 'Не удалось загрузить тарифы')); } });
    get<HistoryPoint[]>('/api/channels/' + channel.id + '/history').then(value => { if (active) setHistory(value); }).catch(() => {});
    get<ChannelReview[]>('/api/channels/' + channel.id + '/reviews').then(value => { if (active) setReviews(value); }).catch(() => {});
    get<string[]>('/api/channels/' + channel.id + '/availability')
      .then(value => { if (active) setReserved(value.map(item => parseUtc(item).getTime())); })
      .catch(() => {});
    return () => { active = false; };
  }, [channel.id]);

  const rate = rates?.find(r => r.id === rateId);

  const isFree = (d: string, h: string, m: string) => {
    const at = new Date(`${d}T${h}:${m}:00`).getTime();
    return at >= Date.now() + HOUR && at <= Date.now() + 90 * DAY && reserved.every(slot => Math.abs(slot - at) >= 60000);
  };
  const days = useMemo(() => upcomingDays().filter(d => HOURS.some(h => MINUTES.some(m => isFree(d, h, m)))), [reserved]);
  const chosenDay = days.includes(day) ? day : days[0] ?? '';
  const freeHours = chosenDay ? HOURS.filter(h => MINUTES.some(m => isFree(chosenDay, h, m))) : [];
  const chosenHour = freeHours.includes(hour) ? hour : freeHours[0] ?? '';
  const freeMinutes = chosenHour ? MINUTES.filter(m => isFree(chosenDay, chosenHour, m)) : [];
  const chosenMinute = freeMinutes.includes(minute) ? minute : freeMinutes[0] ?? '';
  const when = chosenDay && chosenHour && chosenMinute ? new Date(`${chosenDay}T${chosenHour}:${chosenMinute}:00`) : null;

  async function submit() {
    if (!rate || !when) return;
    if (when.getTime() < Date.now() + HOUR) {
      setMessage('Это время уже слишком близко. Выберите другое.');
      setStep(2);
      return;
    }
    setBusy(true);
    setMessage('');
    try {
      const draft = await post<{ id: number }>('/api/orders', { channelId: channel.id, text: text.trim() });
      if (image) await uploadOrderImage(draft.id, image);
      await post('/api/orders/' + draft.id + '/schedule', { rateId: rate.id, scheduledAt: when.toISOString() });
      onOrderCreated(`Заявка #${draft.id} отправлена. Платить пока не нужно: сначала владелец канала её рассмотрит, и мы сразу сообщим о решении.`);
    } catch (e) {
      setMessage(errorText(e, 'Не удалось отправить заявку'));
    } finally {
      setBusy(false);
    }
  }

  async function vote() {
    setVoteBusy(true);
    try { await onVote?.(); }
    catch (e) { setMessage(errorText(e, 'Не удалось учесть голос')); }
    finally { setVoteBusy(false); }
  }

  const subscribers = history.filter((p): p is HistoryPoint & { subscribers: number } => p.subscribers !== null);
  const views = history.filter((p): p is HistoryPoint & { avgViews: number } => p.avgViews !== null);
  const averageStars = reviews.length ? reviews.reduce((sum, r) => sum + r.stars, 0) / reviews.length : 0;

  return <div className="bookingOverlay"><div className="bookingPanel">
    <button className="back" onClick={onBack}>← Назад в каталог</button>
    <div className="detailHeading">
      <ChannelAvatar id={channel.id} title={channel.title}/>
      <div><h2>{channel.title}</h2><span className="detailCategory">{channel.category}</span></div>
    </div>
    {channel.username && <button className="link" onClick={() => openChannelInTelegram(channel.username!)}>Открыть канал <ArrowUpRight size={15}/></button>}
    {channel.description && <p className="description">{channel.description}</p>}
    {onVote && <div className="channelLikeRow">
      <button type="button" className={'channelLikeButton' + (voted ? ' active' : '')} disabled={voted || voteBusy} onClick={() => void vote()}>{voted ? '✓ Голос учтён' : '♡ Голосовать'}</button>
      <span>{channel.likes ?? 0} голосов за месяц</span>
    </div>}

    <div className="detailStats">
      <div><small>Подписчики</small><strong>{count(channel.subscribers)}</strong></div>
      <div><small>Постов за 30 дней</small><strong>{channel.posts30d}</strong></div>
      <div><small>Средние просмотры</small><strong>{count(channel.avgViews)}</strong></div>
      <div><small>Цена 1 000 просмотров</small><strong>{cpm(channel.price, channel.avgViews)}</strong></div>
    </div>
    <details className="channelMore">
      <summary>Статистика и отзывы</summary>
      <p className="description">Просмотры усреднены по {channel.viewsSamples} постам за 30 дней и измерены минимум через сутки после публикации.</p>
      <h3 className="detailSubtitle">Подписчики за 30 дней</h3>
      {subscribers.length < 2 ? <p className="description">Пока недостаточно измерений.</p> : <LineChart label="Подписчики за 30 дней" color="#80b5ff"
        values={subscribers.map(p => p.subscribers)} from={subscribers[0].day} to={subscribers[subscribers.length - 1].day}
        caption={`От ${subscribers[0].subscribers.toLocaleString('ru-RU')} до ${subscribers[subscribers.length - 1].subscribers.toLocaleString('ru-RU')}`}/>}
      <h3 className="detailSubtitle">Просмотры за 30 дней</h3>
      {views.length < 2 ? <p className="description">Пока недостаточно измерений.</p> : <LineChart label="Просмотры за 30 дней" color="#63dec9"
        values={views.map(p => p.avgViews)} from={views[0].day} to={views[views.length - 1].day}
        caption={`Последнее измерение: ${views[views.length - 1].avgViews.toLocaleString('ru-RU')}`}/>}
      <div className="reviewsPreview"><h3>Отзывы о размещениях</h3>
        {reviews.length ? <>
          <strong>★ {averageStars.toFixed(1)} <small>({reviews.length})</small></strong>
          {reviews.slice(0, 3).map((review, i) => <div className="reviewItem" key={i}><b>{'★'.repeat(review.stars)}{'☆'.repeat(5 - review.stars)}</b><p>{review.comment || 'Без комментария'}</p></div>)}
        </> : <p>Отзывов пока нет.</p>}
      </div>
    </details>

    <div className="purchaseStepper">
      <span className={step === 1 ? 'active' : ''}>1 · Срок</span>
      <span className={step === 2 ? 'active' : ''}>2 · Текст и время</span>
      <span className={step === 3 ? 'active' : ''}>3 · Проверка</span>
    </div>
    {message && <ErrorBox message={message}/>}

    {step === 1 && <>
      <h3 className="detailSubtitle">На какой срок разместить рекламу?</h3>
      {rates === null && <p className="description">Загружаем тарифы…</p>}
      {rates?.length === 0 && !message && <p className="description">Владелец пока не добавил тарифы. Выберите другой канал.</p>}
      <div className="rateCards">{rates?.map(r => <button type="button" key={r.id} aria-pressed={rateId === r.id} className={rateId === r.id ? 'rateCard picked' : 'rateCard'} onClick={() => setRateId(r.id)}>
        <span>{duration(r.hours)}</span><strong>{money(r.price)}</strong><small>{rateId === r.id ? '✓ Выбрано' : 'Выбрать'}</small>
      </button>)}</div>
      <button type="button" className="primary purchaseNext" disabled={!rate} onClick={() => setStep(2)}>Далее →</button>
    </>}

    {step === 2 && <>
      <h3 className="detailSubtitle">Что и когда публикуем?</h3>
      <label className="field">Текст рекламного поста
        <textarea value={text} maxLength={textLimit} rows={5} onChange={e => setText(e.target.value)} placeholder="Опишите предложение и добавьте ссылку"/>
        <small className={text.length > textLimit ? 'slotWarning' : 'hint'}>Реклама выйдет именно в таком виде. {text.length}/{textLimit}{image ? ' — Telegram ограничивает подпись к картинке' : ''}</small>
      </label>
      <div className="field"><span>Картинка (необязательно)</span>
        {imageUrl && <img className="adImagePreview" src={imageUrl} alt="Выбранная картинка"/>}
        <div className="orderActions">
          <label className="secondary small filePick">{imageBusy ? 'Обрабатываем…' : imageUrl ? 'Заменить' : 'Выбрать картинку'}
            <input type="file" accept="image/*" hidden disabled={imageBusy} onChange={e => { void chooseImage(e.target.files?.[0]); e.target.value = ''; }}/>
          </label>
          {imageUrl && <button type="button" className="secondary small" onClick={() => { setImage(null); setImageUrl(''); }}>Убрать</button>}
        </div>
      </div>
      {days.length === 0 ? <div className="notice">Свободных дат нет. Выберите другой канал.</div> : <>
        <div className="field"><span>Дата публикации</span><div className="dayScroller">{days.map(d => {
          const value = new Date(d + 'T12:00:00');
          return <button type="button" key={d} className={chosenDay === d ? 'dayTile chosen' : 'dayTile'} onClick={() => setDay(d)}>
            {value.toLocaleDateString('ru-RU', { weekday: 'short' })}<strong>{value.getDate()}</strong><small>{value.toLocaleDateString('ru-RU', { month: 'short' })}</small>
          </button>;
        })}</div></div>
        <div className="field"><span>Время публикации</span>
          <div className="timeGrid">
            <select aria-label="Часы" value={chosenHour} onChange={e => setHour(e.target.value)}>{freeHours.map(h => <option key={h} value={h}>{h} ч</option>)}</select>
            <select aria-label="Минуты" value={chosenMinute} onChange={e => setMinute(e.target.value)}>{freeMinutes.map(m => <option key={m} value={m}>{m} мин</option>)}</select>
          </div>
          <small className="hint">По часам вашего телефона. Показано только свободное время, минимум через час.</small>
        </div>
      </>}
      <div className="purchaseNavigation">
        <button type="button" className="secondary" onClick={() => setStep(1)}>← Назад</button>
        <button type="button" className="primary" disabled={!text.trim() || text.length > textLimit || !when || imageBusy} onClick={() => setStep(3)}>Проверить →</button>
      </div>
    </>}

    {step === 3 && rate && when && <>
      <div className="orderPreview">
        <h3>Проверьте заявку</h3>
        <div><span>Канал</span><b>{channel.title}</b></div>
        <div><span>Срок</span><b>{duration(rate.hours)}</b></div>
        <div><span>Публикация</span><b>{when.toLocaleString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })}</b></div>
        <div className="previewTotal"><span>К оплате владельцу</span><strong>{money(rate.price)}</strong></div>
      </div>
      <div className="orderDetails">{imageUrl && <img className="adImagePreview" src={imageUrl} alt="Картинка рекламы"/>}<strong>Текст</strong><p>{text.trim()}</p></div>
      <ol className="nextSteps">
        <li>Владелец канала рассмотрит заявку. <b>Платить пока не нужно.</b></li>
        <li>После одобрения вы увидите его реквизиты и переведёте деньги напрямую ему.</li>
        <li>Владелец подтвердит перевод, и реклама выйдет автоматически.</li>
      </ol>
      <p className="hint">Reklio не получает эти деньги и не может их вернуть. Если владелец нарушит условия, вы сможете открыть спор.</p>
      <div className="purchaseNavigation">
        <button type="button" className="secondary" onClick={() => setStep(2)}>← Изменить</button>
        <button className="primary" disabled={busy} onClick={() => void submit()}>{busy ? 'Отправка…' : 'Отправить заявку'}</button>
      </div>
    </>}
  </div></div>;
}
