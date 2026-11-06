import { useEffect, useState } from 'react';
import { get, post } from './api';
import { blockText, errorText, money } from './format';
import type { MyChannel, Subscription } from './types';
import { ChannelEditor } from './ChannelEditor';
import { ChannelAvatar, Empty, ErrorBox, Loading } from './ui';

function ConnectChannel({ onBack, onConnected }: { onBack: () => void; onConnected: (id: number) => void }) {
  const [username, setUsername] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  async function connect() {
    setBusy(true);
    setError('');
    try {
      const connected = await post<{ id: number }>('/api/my-channels/connect', { username });
      onConnected(connected.id);
    } catch (e) {
      setError(errorText(e, 'Не удалось подключить канал'));
    } finally {
      setBusy(false);
    }
  }

  return <div className="bookingOverlay" role="dialog" aria-modal="true" aria-label="Подключить канал"><div className="bookingPanel">
    <button className="back" onClick={onBack}>← К моим каналам</button>
    <h2>Подключение канала</h2>
    <ol className="nextSteps">
      <li>Откройте канал в Telegram → <b>Администраторы</b> → <b>Добавить администратора</b>.</li>
      <li>Найдите <b>@ReklioBot</b> и разрешите ему публиковать и удалять сообщения.</li>
      <li>Вернитесь сюда и введите адрес канала.</li>
    </ol>
    <label className="field">Адрес канала
      <input value={username} onChange={e => setUsername(e.target.value)} placeholder="@my_channel" autoComplete="off" autoCapitalize="off"/>
      <small className="hint">Пока поддерживаются только публичные каналы с @адресом. Вы должны быть администратором канала.</small>
    </label>
    {error && <ErrorBox message={error}/>}
    <button className="primary" type="button" disabled={busy || !username.trim()} onClick={() => void connect()}>{busy ? 'Проверяем…' : 'Проверить и подключить'}</button>
  </div></div>;
}

export function Channels({ subscription, initialEditId = null }: { subscription: Subscription | null; initialEditId?: number | null }) {
  const [channels, setChannels] = useState<MyChannel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [connecting, setConnecting] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(initialEditId);
  const [justConnected, setJustConnected] = useState(false);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError('');
    get<MyChannel[]>('/api/my-channels')
      .then(value => { if (active) setChannels(value); })
      .catch(e => { if (active) setError(errorText(e, 'Не удалось загрузить каналы')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [retry]);

  const editing = channels.find(c => c.id === editingId);
  const atLimit = subscription !== null && channels.length >= subscription.channelLimit;

  if (connecting) return <ConnectChannel onBack={() => setConnecting(false)} onConnected={id => {
    setConnecting(false);
    setEditingId(id);
    setJustConnected(true);
    setRetry(n => n + 1);
  }}/>;

  if (editing) return <ChannelEditor
    channel={editing}
    isPro={subscription?.isPro ?? false}
    justConnected={justConnected}
    onChange={updated => setChannels(current => current.map(c => c.id === updated.id ? updated : c))}
    onBack={() => { setEditingId(null); setJustConnected(false); }}/>;

  return <section>
    <div className="channelAddPanel">
      <div>
        <strong>Зарабатывайте на рекламе</strong>
        <p>Подключите канал, укажите цены и реквизиты. Деньги приходят сразу вам, Reklio не берёт комиссию.</p>
      </div>
      <button type="button" className="primary channelAddButton" disabled={atLimit} onClick={() => setConnecting(true)}>＋ Добавить канал</button>
    </div>
    {atLimit && <p className="hint">На вашем тарифе можно подключить до {subscription?.channelLimit} каналов. С PRO можно подключить больше.</p>}
    {error && <ErrorBox message={error} onRetry={() => setRetry(n => n + 1)}/>}
    {loading && channels.length === 0 && <Loading/>}
    {!loading && !error && channels.length === 0 && <Empty title="Каналов пока нет" text="Подключение занимает пару минут: нужно только добавить бота в администраторы канала."/>}
    {channels.map(c => <article className="card" key={c.id}>
      <div className="cardTop">
        <ChannelAvatar id={c.id} title={c.title}/>
        <div className="grow"><strong>{c.title}</strong><small>{c.blocked ? blockText(c.blockedUntil) : c.listed ? 'В каталоге' : 'Скрыт из каталога'}</small></div>
        <div className="price">от {money(c.price)}</div>
      </div>
      <button className="primary small" onClick={() => setEditingId(c.id)}>Настроить</button>
    </article>)}
  </section>;
}
