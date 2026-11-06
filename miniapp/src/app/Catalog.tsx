import { useEffect, useMemo, useState } from 'react';
import { ArrowUpRight, Search } from 'lucide-react';
import { get, getPublicChannel, post, remove } from './api';
import { count, cpm, errorText, money } from './format';
import { openChannelInTelegram, startChannelId } from './telegram';
import type { Channel, MonthlyTop } from './types';
import { ChannelPage } from './ChannelPage';
import { ChannelAvatar, Empty, ErrorBox, Loading } from './ui';

type SortKey = 'default' | 'priceAsc' | 'priceDesc' | 'subscribers' | 'posts' | 'title' | 'cpm';

const costPerView = (c: Channel) => c.avgViews && c.avgViews > 0 ? c.price / c.avgViews : Number.POSITIVE_INFINITY;

const SORTERS: Record<SortKey, (a: Channel, b: Channel) => number> = {
  default: (a, b) => (b.likes ?? 0) - (a.likes ?? 0),
  priceAsc: (a, b) => a.price - b.price,
  priceDesc: (a, b) => b.price - a.price,
  subscribers: (a, b) => (b.subscribers ?? -1) - (a.subscribers ?? -1),
  posts: (a, b) => b.posts30d - a.posts30d,
  title: (a, b) => a.title.localeCompare(b.title, 'ru'),
  cpm: (a, b) => costPerView(a) - costPerView(b)
};

export function Catalog({ onOrderCreated }: { onOrderCreated: (message: string) => void }) {
  const [channels, setChannels] = useState<Channel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [selected, setSelected] = useState<Channel | null>(null);
  const [notice, setNotice] = useState('');
  const [favorites, setFavorites] = useState<number[]>([]);
  const [voted, setVoted] = useState<number[]>([]);
  const [top, setTop] = useState<MonthlyTop | null>(null);
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState('');
  const [maxPrice, setMaxPrice] = useState('');
  const [minSubscribers, setMinSubscribers] = useState('');
  const [onlyFavorites, setOnlyFavorites] = useState(false);
  const [sortBy, setSortBy] = useState<SortKey>('default');

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError('');
    get<Channel[]>('/api/channels')
      .then(data => { if (active) setChannels(data); })
      .catch(e => { if (active) setError(errorText(e, 'Не удалось загрузить каталог')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [retry]);

  useEffect(() => {
    let active = true;
    get<number[]>('/api/favorites').then(ids => { if (active) setFavorites(ids); }).catch(() => {});
    get<number[]>('/api/channel-likes').then(ids => { if (active) setVoted(ids); }).catch(() => {});
    get<MonthlyTop>('/api/rankings/monthly').then(value => { if (active) setTop(value); }).catch(() => {});
    return () => { active = false; };
  }, []);

  // A buyer who followed an owner's link lands straight on that channel.
  useEffect(() => {
    const id = startChannelId();
    if (id === null) return;
    let active = true;
    getPublicChannel(id)
      .then(channel => { if (active) setSelected(channel); })
      .catch(e => { if (active) setNotice(errorText(e, 'Не удалось открыть канал') + ' Можно выбрать другой канал в каталоге.'); });
    return () => { active = false; };
  }, []);

  async function toggleFavorite(id: number) {
    const isFavorite = favorites.includes(id);
    setFavorites(current => isFavorite ? current.filter(x => x !== id) : [id, ...current]);
    try {
      if (isFavorite) await remove('/api/favorites/' + id);
      else await post('/api/favorites/' + id);
    } catch (e) {
      setFavorites(current => isFavorite ? [id, ...current] : current.filter(x => x !== id));
      setNotice(errorText(e, 'Не удалось сохранить избранное'));
    }
  }

  async function vote(id: number) {
    await post('/api/channel-likes/' + id);
    setVoted(current => [...current, id]);
    setChannels(current => current.map(c => c.id === id ? { ...c, likes: (c.likes ?? 0) + 1 } : c));
  }

  const categories = useMemo(() => [...new Set(channels.map(c => c.category).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'ru')), [channels]);

  const visible = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('ru');
    return channels
      .filter(c => (!category || c.category === category)
        && (!onlyFavorites || favorites.includes(c.id))
        && (!maxPrice || c.price <= Number(maxPrice))
        && (!minSubscribers || (c.subscribers !== null && c.subscribers >= Number(minSubscribers)))
        && (!query || [c.title, c.category, c.description, c.username || ''].some(value => value.toLocaleLowerCase('ru').includes(query))))
      .sort((a, b) => Number(Boolean(b.promotedUntil)) - Number(Boolean(a.promotedUntil)) || SORTERS[sortBy](a, b));
  }, [channels, search, category, maxPrice, minSubscribers, onlyFavorites, favorites, sortBy]);

  const filtersActive = Boolean(search || category || maxPrice || minSubscribers || onlyFavorites || sortBy !== 'default');
  const resetFilters = () => { setSearch(''); setCategory(''); setMaxPrice(''); setMinSubscribers(''); setOnlyFavorites(false); setSortBy('default'); };

  if (selected) return <ChannelPage
    channel={selected}
    voted={voted.includes(selected.id)}
    onVote={() => vote(selected.id)}
    onBack={() => setSelected(null)}
    onOrderCreated={onOrderCreated}/>;

  return <section>
    <label className="search"><Search size={19}/><input placeholder="Название или тема канала" value={search} maxLength={80} onChange={e => setSearch(e.target.value)}/></label>
    <div className="favoriteFilter">
      <button className={!onlyFavorites ? 'active' : ''} onClick={() => setOnlyFavorites(false)}>Все каналы</button>
      <button className={onlyFavorites ? 'active' : ''} onClick={() => setOnlyFavorites(true)}>♡ Избранное ({favorites.length})</button>
    </div>
    <details className="catalogFilters">
      <summary>Фильтры и сортировка{filtersActive ? ' · включены' : ''}</summary>
      <div className="catalogControls">
        <label>Категория<select value={category} onChange={e => setCategory(e.target.value)}>
          <option value="">Все категории</option>{categories.map(item => <option key={item} value={item}>{item}</option>)}
        </select></label>
        <label>Сортировка<select value={sortBy} onChange={e => setSortBy(e.target.value as SortKey)}>
          <option value="default">Популярные</option><option value="priceAsc">Сначала дешёвые</option><option value="priceDesc">Сначала дорогие</option>
          <option value="subscribers">Больше подписчиков</option><option value="posts">Активность за 30 дней</option><option value="cpm">Дешевле за 1 000 просмотров</option><option value="title">По названию</option>
        </select></label>
        <label>Бюджет<select value={maxPrice} onChange={e => setMaxPrice(e.target.value)}>
          <option value="">Любая цена</option><option value="1000">До 1 000 ₽</option><option value="3000">До 3 000 ₽</option><option value="5000">До 5 000 ₽</option><option value="10000">До 10 000 ₽</option>
        </select></label>
        <label>Аудитория<select value={minSubscribers} onChange={e => setMinSubscribers(e.target.value)}>
          <option value="">Любая</option><option value="1000">От 1 000</option><option value="5000">От 5 000</option><option value="10000">От 10 000</option><option value="50000">От 50 000</option>
        </select></label>
      </div>
    </details>
    <div className="catalogSummary"><span>Найдено каналов: {visible.length}</span>{filtersActive && <button onClick={resetFilters}>Сбросить</button>}</div>
    {notice && <div className="notice" role="status">{notice}</div>}
    {error && <ErrorBox message={error} onRetry={() => setRetry(n => n + 1)}/>}
    {loading && <Loading/>}
    {!loading && !error && visible.length === 0 && (filtersActive
      ? <Empty title="Ничего не нашлось" text="Попробуйте изменить запрос или фильтры." action={<button className="secondary small" onClick={resetFilters}>Сбросить фильтры</button>}/>
      : <Empty title="В каталоге пока нет каналов" text="Загляните чуть позже: новые площадки появляются регулярно."/>)}
    {visible.map(c => <article className={c.promotedUntil ? 'card catalogCard catalogCardPromoted' : 'card catalogCard'} key={c.id}>
      {c.promotedUntil && <div className="catalogPromotedFlag">✦ Продвигается</div>}
      <div className="cardTop">
        <ChannelAvatar id={c.id} title={c.title} extraClass="catalogAvatar"/>
        <div className="grow"><strong>{c.title}</strong><small>{c.category}</small></div>
        <button className={'favoriteHeart' + (favorites.includes(c.id) ? ' active' : '')} aria-label={favorites.includes(c.id) ? 'Убрать из избранного' : 'Добавить в избранное'} onClick={() => void toggleFavorite(c.id)}>{favorites.includes(c.id) ? '♥' : '♡'}</button>
      </div>
      <div className="channelStats">
        <span>👥 <b>{count(c.subscribers)}</b></span>
        <span>👁 <b>{count(c.avgViews)}</b></span>
        <span>📊 1 000 просмотров: <b>{cpm(c.price, c.avgViews)}</b></span>
      </div>
      {c.description && <p className="description">{c.description}</p>}
      <div className="catalogCardFoot">
        <div><small>Размещение от</small><strong>{money(c.price)}</strong></div>
        <button className="primary small" onClick={() => setSelected(c)}>Выбрать →</button>
      </div>
      {c.username && <button className="link" onClick={() => openChannelInTelegram(c.username!)}>Открыть канал <ArrowUpRight size={15}/></button>}
    </article>)}
    {top && top.channels.length > 0 && <details className="monthlyTopPanel">
      <summary>🏆 Топ месяца · {top.month}</summary>
      {top.channels.slice(0, 5).map(c => <div key={c.id} className="monthlyTopRow"><strong>#{c.place}</strong><span>{c.title}</span><b>♥ {c.votes}</b></div>)}
    </details>}
  </section>;
}
