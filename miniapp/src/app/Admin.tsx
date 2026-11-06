import { useEffect, useState } from 'react';
import { get, post, put } from './api';
import { blockText, dateTime, errorText, money } from './format';
import type { AdminDispute, AdminOrder, AdminOverview, AdminUser, AuditItem, ModerationChannel, MtprotoStatus, PublicationIssue, StaffMember } from './types';

type AdminTab = 'overview' | 'disputes' | 'channels' | 'orders' | 'users' | 'team' | 'audit';
type Sanction = { sanction: 'none' | 'temporary' | 'permanent'; days: string; blockOwner: boolean };

const SERVICE_PRICES = [{ key: 'pro30', label: 'PRO · 30 дней' }, { key: 'promotion7', label: 'Продвижение · 7 дней' }];
const NO_SANCTION: Sanction = { sanction: 'none', days: '7', blockOwner: false };

export function Admin({ role }: { role: string | null }) {
  const canManageTeam = role === 'owner' || role === 'admin';
  const [tab, setTab] = useState<AdminTab>('disputes');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [overview, setOverview] = useState<AdminOverview | null>(null);
  const [disputes, setDisputes] = useState<AdminDispute[]>([]);
  const [sanctions, setSanctions] = useState<Record<number, Sanction>>({});
  const [channels, setChannels] = useState<ModerationChannel[]>([]);
  const [issues, setIssues] = useState<PublicationIssue[]>([]);
  const [mtproto, setMtproto] = useState<MtprotoStatus | null>(null);
  const [reviewId, setReviewId] = useState<number | null>(null);
  const [reviewNote, setReviewNote] = useState('');
  const [search, setSearch] = useState('');
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [orders, setOrders] = useState<AdminOrder[]>([]);
  const [staff, setStaff] = useState<StaffMember[]>([]);
  const [staffId, setStaffId] = useState('');
  const [staffRole, setStaffRole] = useState('moderator');
  const [audit, setAudit] = useState<AuditItem[]>([]);
  const [prices, setPrices] = useState<Record<string, number>>({});
  const [priceDraft, setPriceDraft] = useState<Record<string, string>>({});
  const [priceNotice, setPriceNotice] = useState('');

  async function act(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try { await action(); }
    catch (e) { setError(errorText(e, 'Не удалось выполнить действие')); }
    finally { setBusy(false); }
  }

  const findPeople = async (query: string) => {
    const [foundUsers, foundOrders] = await Promise.all([
      get<AdminUser[]>('/api/admin/users?q=' + encodeURIComponent(query)),
      get<AdminOrder[]>('/api/admin/orders?q=' + encodeURIComponent(query))
    ]);
    setUsers(foundUsers);
    setOrders(foundOrders);
  };

  const load = async () => {
    const [stats, openDisputes, moderation, publication, servicePrices] = await Promise.all([
      get<AdminOverview>('/api/admin/overview'),
      get<AdminDispute[]>('/api/admin/disputes'),
      get<ModerationChannel[]>('/api/admin/channels'),
      get<PublicationIssue[]>('/api/admin/publication-issues'),
      get<Record<string, number>>('/api/service-prices')
    ]);
    setOverview(stats);
    setDisputes(openDisputes);
    setChannels(moderation);
    setIssues(publication);
    setPrices(servicePrices);
    setMtproto(await get<MtprotoStatus>('/api/admin/mtproto-status').catch(() => null));
    await findPeople('');
    if (canManageTeam) {
      setStaff(await get<StaffMember[]>('/api/admin/staff'));
      setAudit(await get<AuditItem[]>('/api/admin/audit'));
    }
  };

  useEffect(() => { void act(load); }, []);

  const resolveDispute = (id: number, decision: 'BuyerRight' | 'OwnerRight') => act(async () => {
    const choice = sanctions[id] ?? NO_SANCTION;
    await post(`/api/admin/disputes/${id}/resolve`, { decision, sanction: choice.sanction, days: choice.sanction === 'temporary' ? Number(choice.days) : null, blockOwner: choice.blockOwner });
    await load();
  });

  const toggleChannel = (channel: ModerationChannel) => act(async () => {
    await post(`/api/admin/channels/${channel.id}/listing`, { listed: !channel.listed });
    setChannels(current => current.map(c => c.id === channel.id ? { ...c, listed: !channel.listed } : c));
  });

  const unblock = (id: number) => act(async () => {
    await post(`/api/admin/channels/${id}/unblock`);
    await load();
  });

  const unblockOwner = (id: number) => act(async () => {
    await post(`/api/admin/users/${id}/unblock`);
    await load();
  });

  const reviewIssue = (id: number, decision: 'ConfirmedProblem' | 'NeedsInvestigation') => act(async () => {
    if (reviewNote.trim().length < 10) throw new Error('Укажите причину, минимум 10 символов');
    await post(`/api/admin/publication-issues/${id}/review`, { decision, note: reviewNote.trim() });
    setIssues(await get<PublicationIssue[]>('/api/admin/publication-issues'));
    setReviewId(null);
    setReviewNote('');
  });

  const saveStaff = () => act(async () => {
    if (!/^\d+$/.test(staffId)) throw new Error('Укажите числовой Telegram ID');
    await post('/api/admin/staff', { telegramId: Number(staffId), role: staffRole });
    setStaffId('');
    setStaff(await get<StaffMember[]>('/api/admin/staff'));
  });

  const revokeStaff = (id: number) => act(async () => {
    await post(`/api/admin/staff/${id}/revoke`);
    setStaff(await get<StaffMember[]>('/api/admin/staff'));
  });

  const savePrice = (key: string) => act(async () => {
    setPriceNotice('');
    const price = Number(priceDraft[key] ?? prices[key]);
    if (!Number.isInteger(price) || price < 1 || price > 100000) throw new Error('Укажите цену от 1 до 100 000 ₽');
    await put(`/api/admin/service-prices/${key}`, { priceRub: price });
    setPrices(await get<Record<string, number>>('/api/service-prices'));
    setPriceNotice('Цена сохранена.');
  });

  const tabs: { id: AdminTab; label: string }[] = [
    { id: 'disputes', label: `Споры (${disputes.filter(d => d.status === 'Disputed').length})` },
    { id: 'overview', label: 'Обзор' },
    { id: 'channels', label: 'Каналы' },
    { id: 'orders', label: 'Заказы' },
    { id: 'users', label: 'Пользователи' },
    ...(canManageTeam ? [{ id: 'team' as const, label: 'Команда' }, { id: 'audit' as const, label: 'Журнал' }] : [])
  ];

  return <div className="sellerDashboard">
    <h3>Администрирование Reklio</h3>
    {error && <div className="notice" role="alert">{error}</div>}
    <div className="adminTabs">{tabs.map(t => <button key={t.id} className={tab === t.id ? 'active' : ''} onClick={() => setTab(t.id)}>{t.label}</button>)}</div>

    {tab === 'overview' && <>
      {overview && <div className="dashboardGrid">
        <div><small>Всего каналов</small><strong>{overview.channels}</strong></div>
        <div><small>В каталоге</small><strong>{overview.listed}</strong></div>
        <div><small>Всего заказов</small><strong>{overview.orders}</strong></div>
        <div><small>Открытые споры</small><strong>{overview.openDisputes}</strong></div>
        <div><small>Ошибки размещений</small><strong>{overview.failedPublications}</strong></div>
      </div>}
      {role === 'owner' && <section className="card adminPrices"><h3>Цены услуг Reklio</h3>
        {SERVICE_PRICES.map(({ key, label }) => <div className="sellerRate" key={key}>
          <label className="field">{label}<input type="number" min="1" max="100000" inputMode="numeric" value={priceDraft[key] ?? String(prices[key] ?? '')} onChange={e => setPriceDraft(d => ({ ...d, [key]: e.target.value }))}/></label>
          <button className="secondary small" type="button" disabled={busy} onClick={() => void savePrice(key)}>Сохранить</button>
        </div>)}
        {priceNotice && <p className="hint">{priceNotice}</p>}
      </section>}
    </>}

    {tab === 'disputes' && <>
      {disputes.length === 0 && <p>Обращений нет.</p>}
      {disputes.map(d => {
        const choice = sanctions[d.id] ?? NO_SANCTION;
        return <article className="card" key={d.id}>
          <strong>Заказ #{d.id} · {d.channel} · {money(d.price)}</strong>
          <p className="description">Причина: {d.reason}</p>
          <p className="description">Реклама: {d.text}</p>
          <p className="hint">{d.paymentStatus === 'DirectPaid' ? 'Оплата подтверждена владельцем' : 'Оплата владельцем не подтверждена'} · {d.status === 'Disputed' ? 'ожидает решения' : 'решение принято'}{d.openedAt ? ' · открыт ' + dateTime(d.openedAt) : ''}</p>
          {d.status === 'Disputed' && <div className="rescheduleBox">
            <label className="field">Если покупатель прав
              <select value={choice.sanction} onChange={e => { const sanction = e.target.value as Sanction['sanction']; setSanctions(s => ({ ...s, [d.id]: { ...choice, sanction } })); }}>
                <option value="none">Без блокировки канала</option><option value="temporary">Заблокировать на время</option><option value="permanent">Заблокировать навсегда</option>
              </select>
            </label>
            {choice.sanction === 'temporary' && <label className="field">Срок блокировки, дней
              <input type="number" min="1" max="365" inputMode="numeric" value={choice.days} onChange={e => { const days = e.target.value; setSanctions(s => ({ ...s, [d.id]: { ...choice, days } })); }}/>
            </label>}
            {choice.sanction !== 'none' && <label className="blockOwnerToggle">
              <input type="checkbox" checked={choice.blockOwner} onChange={e => { const blockOwner = e.target.checked; setSanctions(s => ({ ...s, [d.id]: { ...choice, blockOwner } })); }}/>
              Заблокировать и самого владельца: все его каналы и подключение новых
            </label>}
            <div className="orderActions">
              <button className="primary small" disabled={busy} onClick={() => void resolveDispute(d.id, 'BuyerRight')}>Покупатель прав</button>
              <button className="secondary small" disabled={busy} onClick={() => void resolveDispute(d.id, 'OwnerRight')}>Нарушения нет</button>
            </div>
          </div>}
        </article>;
      })}
    </>}

    {tab === 'channels' && <>
      <p className="description">Последние 100 каналов. Скрытие убирает канал из каталога, но не отменяет действующие заказы.</p>
      {channels.map(c => <div className="sellerRate" key={c.id}>
        <div><strong>{c.title}</strong><small>{c.category} · {c.blocked ? blockText(c.blockedUntil) : c.listed ? 'В каталоге' : 'Скрыт'} · {money(c.price)}</small></div>
        {c.blocked
          ? <button className="secondary small" disabled={busy} onClick={() => void unblock(c.id)}>Снять блокировку</button>
          : <button className="secondary small" disabled={busy} onClick={() => void toggleChannel(c)}>{c.listed ? 'Скрыть' : 'Показать'}</button>}
      </div>)}
    </>}

    {(tab === 'users' || tab === 'orders') && <div className="adminSearch">
      <input placeholder="Канал, заказ, Telegram ID или имя" value={search} maxLength={80} onChange={e => setSearch(e.target.value)}/>
      <button className="secondary small" disabled={busy} onClick={() => void act(() => findPeople(search))}>Найти</button>
    </div>}

    {tab === 'users' && users.map(u => <div className="sellerRate" key={u.id}>
      <div><strong>{u.name || 'Пользователь'} · {u.id}</strong><small>{u.username ? '@' + u.username + ' · ' : ''}Каналов: {u.channels} · Покупок: {u.purchases}{u.blocked ? ' · ' + blockText(u.blockedUntil) : ''}</small></div>
      {u.blocked && <button className="secondary small" disabled={busy} onClick={() => void unblockOwner(u.id)}>Снять блокировку</button>}
    </div>)}

    {tab === 'orders' && <>
      {orders.map(o => <div className="sellerRate" key={o.id}>
        <div><strong>#{o.id} · {o.channel}</strong><small>{o.status} · {money(o.price)} · Покупатель: {o.buyerId}</small></div>
      </div>)}
      <h3 className="detailSubtitle">Проблемы публикаций</h3>
      {mtproto && <div className="notice">
        <strong>Мониторинг постов: {mtproto.configured ? (mtproto.healthy ? 'работает' : 'нет недавних проверок') : 'не настроен'}</strong>
        <p>Проверено: {mtproto.checkedOrders} · На месте: {mtproto.present} · Не найдено: {mtproto.missing}{mtproto.lastCheck ? ' · Последняя проверка: ' + dateTime(mtproto.lastCheck) : ''}</p>
      </div>}
      {issues.length === 0 && <p className="description">Ошибок публикаций нет.</p>}
      {issues.map(issue => <article className="card" key={issue.id}>
        <strong>Заказ #{issue.id} · {issue.channel}</strong>
        <p className="description">{issue.status}: {issue.error || 'подробностей нет'}</p>
        <button className="secondary small" onClick={() => { setReviewId(reviewId === issue.id ? null : issue.id); setReviewNote(''); }}>Рассмотреть</button>
        {reviewId === issue.id && <div className="rescheduleBox">
          <label className="field">Что удалось проверить?<textarea rows={3} value={reviewNote} maxLength={500} onChange={e => setReviewNote(e.target.value)}/></label>
          <div className="orderActions">
            <button className="secondary small" disabled={busy} onClick={() => void reviewIssue(issue.id, 'NeedsInvestigation')}>Нужна проверка</button>
            <button className="primary small" disabled={busy} onClick={() => void reviewIssue(issue.id, 'ConfirmedProblem')}>Подтвердить нарушение</button>
          </div>
        </div>}
      </article>)}
    </>}

    {tab === 'team' && canManageTeam && <>
      <label className="field">Telegram ID сотрудника<input inputMode="numeric" value={staffId} onChange={e => setStaffId(e.target.value)} placeholder="123456789"/></label>
      <label className="field">Роль<select value={staffRole} onChange={e => setStaffRole(e.target.value)}>
        <option value="moderator">Модератор</option>{role === 'owner' && <option value="admin">Администратор</option>}
      </select></label>
      <button className="primary small" disabled={busy} onClick={() => void saveStaff()}>Добавить или изменить роль</button>
      {staff.map(person => <div className="sellerRate" key={person.id}>
        <div><strong>{person.id}</strong><small>{person.role === 'admin' ? 'Администратор' : 'Модератор'}</small></div>
        <button className="secondary small" disabled={busy} onClick={() => void revokeStaff(person.id)}>Отозвать</button>
      </div>)}
    </>}

    {tab === 'audit' && canManageTeam && audit.map(a => <div className="auditEntry" key={a.id}>
      <strong>{a.action}</strong>
      <small>{a.actorId} → {a.targetId ?? '—'} · {a.details ?? ''}</small>
      <small>{dateTime(a.createdAt)}</small>
    </div>)}
  </div>;
}
