import {useEffect,useMemo,useState} from 'react';
import './landing.css';
import WebAccount from './WebAccount';
const items=[{n:'01',title:'Выберите канал',desc:'Изучайте тематику, описание площадки и стоимость размещения до подачи заявки.'},{n:'02',title:'Запланируйте рекламу',desc:'Укажите время размещения и передайте готовый рекламный пост владельцу канала.'},{n:'03',title:'Следите за публикацией',desc:'Управляйте заказами через Telegram. Reklio проверяет доступность опубликованных сообщений.'}];
type PublicChannel={id:number;title:string;category:string;price:number;description:string;username:string|null;subscribers:number|null;avgViews:number|null;promotedUntil?:string|null;likes:number};
const publicApi=import.meta.env.VITE_API_URL || 'https://api.reklio.site';
function ChannelAvatar({id,title,className='lp-catalog-avatar'}:{id:number;title:string;className?:string}){
 const [failed,setFailed]=useState(false);
 return <span className={className} aria-hidden="true">{!failed&&<img src={publicApi+'/api/public/channels/'+id+'/avatar'} alt="" loading="lazy" onError={()=>setFailed(true)}/>}<span style={{display:failed?'':'none'}}>{title.slice(0,1).toUpperCase()}</span></span>;
}
function PublicCatalog(){
 const [channels,setChannels]=useState<PublicChannel[]>([]);
 const [monthlyTop,setMonthlyTop]=useState<{month:string;channels:{id:number;title:string;category:string;votes:number;place:number}[]}|null>(null);
 const [search,setSearch]=useState(()=>new URLSearchParams(window.location.search).get('q')||'');
 const [category,setCategory]=useState('');
 const [sort,setSort]=useState('default');
 const [maxPrice,setMaxPrice]=useState('');
 const [loading,setLoading]=useState(true);
 const [error,setError]=useState('');
 useEffect(()=>{
  fetch(publicApi+'/api/rankings/monthly').then(r=>r.ok?r.json():null).then(data=>{if(data)setMonthlyTop(data)}).catch(()=>{});
 },[]);
 useEffect(()=>{
  const controller=new AbortController();
  fetch(publicApi+'/api/public/channels?limit=60',{signal:controller.signal})
   .then(async r=>{if(!r.ok)throw Error('Не удалось загрузить каталог');return r.json() as Promise<PublicChannel[]>;})
   .then(setChannels).catch(e=>{if(!controller.signal.aborted)setError(e instanceof Error?e.message:'Ошибка загрузки');})
   .finally(()=>{if(!controller.signal.aborted)setLoading(false);});
  return ()=>controller.abort();
 },[]);
 const categories=useMemo(()=>[...new Set(channels.map(c=>c.category).filter(Boolean))].sort(),[channels]);
 const visible=useMemo(()=>channels.filter(c=>(!category||c.category===category)&&(!maxPrice||c.price<=Number(maxPrice))&&[c.title,c.description,c.category,c.username||''].join(' ').toLocaleLowerCase('ru').includes(search.trim().toLocaleLowerCase('ru')))
 .sort((a,b)=>(Number(Boolean(b.promotedUntil))-Number(Boolean(a.promotedUntil)))||(sort==='price'?a.price-b.price:sort==='audience'?(b.subscribers||0)-(a.subscribers||0):sort==='cpm'?(a.avgViews&&a.avgViews>0?a.price/a.avgViews:Infinity)-(b.avgViews&&b.avgViews>0?b.price/b.avgViews:Infinity):(b.likes??0)-(a.likes??0))),[channels,category,search,sort,maxPrice]);
 const reset=()=>{setSearch('');setCategory('');setSort('default');setMaxPrice('');window.history.replaceState(null,'','/catalog');};
 const hasFilters=Boolean(search||category||maxPrice||sort!=='default');
 return <section id="catalog" className="lp-section lp-catalog lp-catalog-modern">
  {monthlyTop&&<div className="lp-monthly-ranking"><h3>🏆 ТОП месяца · {monthlyTop.month}</h3><div className="lp-top-entries">{monthlyTop.channels.length?monthlyTop.channels.slice(0,5).map(c=><a key={c.id} href={'/channels/'+c.id}><strong>#{c.place}</strong><span>{c.title}</span><b>♥ {c.votes}</b></a>):<p>Рейтинг формируется. Проголосуйте за канал в Mini App!</p>}</div><small>Голосовать можно раз в 24 часа.</small></div>}<div className="lp-catalog-title"><div><span className="lp-kicker">КАТАЛОГ REKLIO</span><h2>Рекламные площадки</h2><p>Сравнивайте предложения и переходите к оформлению заказа через Telegram.</p></div><a href="https://t.me/ReklioBot">Добавить свой канал ↗</a></div>
  <div className="lp-catalog-toolbar"><label className="lp-filter-search"><span>Поиск</span><input type="search" aria-label="Поиск каналов" placeholder="Название, тематика или @username" value={search} onChange={e=>setSearch(e.target.value)}/></label>
  <label><span>Категория</span><select aria-label="Категория" value={category} onChange={e=>setCategory(e.target.value)}><option value="">Все категории</option>{categories.map(c=><option key={c} value={c}>{c}</option>)}</select></label>
  <label><span>Стоимость до</span><select aria-label="Максимальная цена" value={maxPrice} onChange={e=>setMaxPrice(e.target.value)}><option value="">Любая цена</option><option value="500">500 ₽</option><option value="1000">1 000 ₽</option><option value="3000">3 000 ₽</option><option value="5000">5 000 ₽</option><option value="10000">10 000 ₽</option></select></label>
  <label><span>Сортировка</span><select aria-label="Сортировка" value={sort} onChange={e=>setSort(e.target.value)}><option value="default">Популярные · лайки</option><option value="price">Сначала дешевле</option><option value="audience">Больше подписчиков</option><option value="cpm">Выгодный CPM</option></select></label></div>
  <div className="lp-catalog-status"><span aria-live="polite">{loading?'Загружаем площадки…':error?'Ошибка загрузки': 'Найдено каналов: '+visible.length}</span>{hasFilters&&<button type="button" onClick={reset}>Сбросить фильтры ×</button>}</div>
  {error&&<div className="lp-catalog-message" role="alert"><h3>Каталог временно недоступен</h3><p>{error}. Попробуйте обновить страницу.</p><button type="button" onClick={()=>window.location.reload()}>Обновить</button></div>}
  {!loading&&!error&&visible.length===0&&<div className="lp-catalog-message"><h3>Площадки не найдены</h3><p>Попробуйте изменить категорию, стоимость или поисковый запрос.</p><button type="button" onClick={reset}>Показать все каналы</button></div>}
  <div className="lp-catalog-grid">{visible.map(c=><article key={c.id} className={c.promotedUntil?"lp-catalog-card lp-catalog-sponsored":"lp-catalog-card"}>{c.promotedUntil&&<div className="lp-sponsored-label">✦ Продвигается</div>}<div className="lp-catalog-heading"><ChannelAvatar id={c.id} title={c.title}/><div><strong>{c.title}</strong><small>{c.category||'Telegram-канал'} · ♥ {c.likes??0} голосов</small></div></div><p>{c.description||'Рекламная площадка в Telegram'}</p><div className="lp-catalog-metrics"><span>Подписчики <b>{c.subscribers?.toLocaleString('ru-RU')??'Нет данных'}</b></span><span>Средние просмотры <b>{c.avgViews?.toLocaleString('ru-RU')??'Нет данных'}</b></span><span>CPM <b>{c.avgViews&&c.avgViews>0?rub(c.price*1000/c.avgViews):'Нет данных'}</b></span></div><div className="lp-catalog-bottom"><div><small>Реклама от</small><strong>{rub(c.price)}</strong></div><a className="lp-card-link" href={'/channels/'+c.id}>Открыть канал ↗</a></div></article>)}</div>
 </section>;
}
function GettingStarted(){
 const [role,setRole]=useState<'buyer'|'owner'>('buyer');
 const buyer=role==='buyer';
 return <section className="lp-start" id="start" aria-labelledby="lp-start-title">
  <div className="lp-start-heading"><span className="lp-kicker">С ЧЕГО НАЧАТЬ</span><h2 id="lp-start-title">Что вы хотите сделать?</h2><p>Выберите свою задачу — покажем короткий путь. Регистрация для просмотра каталога не нужна.</p></div>
  <div className="lp-start-tabs" role="group" aria-label="Моя цель"><button type="button" className={buyer?'active':''} aria-pressed={buyer} onClick={()=>setRole('buyer')}>Хочу купить рекламу</button><button type="button" className={!buyer?'active':''} aria-pressed={!buyer} onClick={()=>setRole('owner')}>Хочу продавать рекламу</button></div>
  <div className="lp-start-steps">{(buyer?[
    ['01','Найдите канал','Откройте каталог, выберите тематику и сравните цены.'],
    ['02','Изучите предложение','Посмотрите просмотры, аудиторию, сроки размещения и отзывы.'],
    ['03','Оформите заказ','Откройте Reklio в Telegram, выберите время и отправьте заявку.']
   ]:[
    ['01','Откройте Reklio','Перейдите в Telegram-бот и запустите приложение.'],
    ['02','Добавьте канал','Подключите канал и укажите стоимость и продолжительность рекламы.'],
    ['03','Получайте заявки','Подтверждайте заказы и следите за публикациями в приложении.']
   ]).map(([n,title,description])=><article key={n}><span>{n}</span><h3>{title}</h3><p>{description}</p></article>)}</div>
  <div className="lp-start-actions"><a className="lp-start-primary" href={buyer?'/catalog':'https://t.me/ReklioBot'}>{buyer?'Перейти в каталог':'Подключить мой канал'} ↗</a><a href={buyer?'/help':'/pricing'}>{buyer?'Как работает покупка':'Посмотреть тарифы'} →</a></div>
 </section>;
}
function HomeFeatured(){
 const [channels,setChannels]=useState<PublicChannel[]>([]);
 const [loading,setLoading]=useState(true);
 const [error,setError]=useState(false);
 useEffect(()=>{
  const controller=new AbortController();
  fetch(publicApi+'/api/public/channels?limit=6',{signal:controller.signal})
   .then(r=>{if(!r.ok)throw Error('catalog');return r.json() as Promise<PublicChannel[]>;})
   .then(data=>{if(!controller.signal.aborted)setChannels(data.slice(0,3));})
   .catch(()=>{if(!controller.signal.aborted)setError(true);})
   .finally(()=>{if(!controller.signal.aborted)setLoading(false);});
  return ()=>controller.abort();
 },[]);
 return <section className="lp-home-market" aria-labelledby="lp-home-market-title">
  <div className="lp-home-market-head"><div><span className="lp-kicker">МАРКЕТПЛЕЙС</span><h2 id="lp-home-market-title">Площадки для вашей рекламы</h2><p>Актуальные предложения из каталога Reklio — без регистрации для просмотра.</p></div><a href="/catalog">Весь каталог ↗</a></div>
  {loading&&<p className="lp-catalog-hint" role="status">Загружаем каналы…</p>}
  {error&&<p className="lp-catalog-hint">Не удалось загрузить площадки. <a href="/catalog">Откройте каталог ↗</a></p>}
  {!loading&&!error&&channels.length===0&&<div className="lp-home-empty"><p>Площадки скоро появятся. Добавить свой канал можно через ReklioBot.</p><a href="https://t.me/ReklioBot">Подключить канал ↗</a></div>}
  <div className="lp-home-channel-grid">{channels.map(c=><a className={c.promotedUntil?"lp-home-channel lp-catalog-sponsored":"lp-home-channel"} href={'/channels/'+c.id} key={c.id}>
   {c.promotedUntil&&<div className="lp-sponsored-label">✦ Продвигается</div>}<div className="lp-catalog-heading"><ChannelAvatar id={c.id} title={c.title}/><div><strong>{c.title}</strong><small>{c.category||'Telegram-канал'}</small></div></div>
   <p>{c.description||'Рекламная площадка в Telegram'}</p>
   <div className="lp-home-channel-meta"><span>{c.subscribers!==null?c.subscribers.toLocaleString('ru-RU')+' подписчиков':'Аудитория уточняется'}</span><span>♥ {c.likes??0}</span><span>От {rub(c.price)}</span></div>
   <span className="lp-home-channel-more">Подробнее о канале ↗</span>
  </a>)}</div>
 </section>;
}
type PublicDetails={rates:{hours:number;price:number}[];reviews:{stars:number;comment:string|null;createdAt?:string}[]};
const rub=(amount:number)=>new Intl.NumberFormat('ru-RU',{style:'currency',currency:'RUB',maximumFractionDigits:0}).format(amount);
const amount=(value:number|null)=>value===null?'Нет данных':value.toLocaleString('ru-RU');
function ChannelPage(){
 const id=window.location.pathname.match(/^\/channels\/(-?\d+)\/?$/)?.[1];
 const [channel,setChannel]=useState<PublicChannel|null>(null);
 const [detail,setDetail]=useState<PublicDetails|null>(null);
 const [loading,setLoading]=useState(true);
 const [detailsError,setDetailsError]=useState(false);
 const [error,setError]=useState('');
 useEffect(()=>{
  if(!id){setError('Неверная ссылка на канал');setLoading(false);return;}
  const controller=new AbortController();
  setLoading(true);setError('');setDetailsError(false);
  const url=publicApi+'/api/public/channels/'+encodeURIComponent(id);
  fetch(url,{signal:controller.signal})
   .then(async response=>{if(!response.ok)throw new Error(response.status===404?'Канал не найден или снят с публикации':'Не удалось загрузить канал');return response.json() as Promise<PublicChannel>;})
   .then(async c=>{
     if(controller.signal.aborted)return;
     setChannel(c);document.title=c.title+' — Reklio';
     try{
      const response=await fetch(url+'/details',{signal:controller.signal});
      if(!response.ok)throw new Error('details');
      const data=await response.json() as PublicDetails;
      if(!controller.signal.aborted)setDetail(data);
     }catch{if(!controller.signal.aborted)setDetailsError(true);}
   })
   .catch(e=>{if(!controller.signal.aborted)setError(e instanceof Error?e.message:'Ошибка загрузки');})
   .finally(()=>{if(!controller.signal.aborted)setLoading(false);});
  return ()=>{controller.abort();document.title='Reklio — реклама в Telegram';};
 },[id]);
 const rates=detail?.rates??[];
 const lowest=rates.length?Math.min(...rates.map(r=>r.price)):channel?.price??0;
 const link=channel?'https://t.me/ReklioBot?startapp=ad_'+channel.id:'https://t.me/ReklioBot?startapp';
 return <section className="lp-channel-page">
  <div className="lp-channel-breadcrumb"><a href="/catalog">Каталог каналов</a><span aria-hidden="true">/</span><span>{channel?.title||'Площадка'}</span></div>
  {loading&&!channel&&!error&&<div className="lp-detail-state" role="status">Загружаем информацию о площадке…</div>}
  {error&&<div className="lp-detail-state" role="alert"><h2>Не удалось открыть канал</h2><p>{error}</p><a href="/catalog">Вернуться в каталог →</a></div>}
  {channel&&<><div className="lp-channel-page-hero">
   <ChannelAvatar id={channel.id} title={channel.title}/>
   <div className="lp-channel-identity">{channel.promotedUntil&&<div className="lp-sponsored-label">✦ Продвигается</div>}<span className="lp-kicker">{channel.category||'TELEGRAM-КАНАЛ'}</span><h1>{channel.title}</h1>
    <p>{channel.description||'Рекламная площадка в Telegram. Уточняйте детали размещения перед оформлением заказа.'}</p>
    {channel.username&&<a className="lp-telegram-channel" href={'https://t.me/'+encodeURIComponent(channel.username.replace(/^@/,''))} target="_blank" rel="noopener noreferrer">Открыть канал в Telegram ↗</a>}
   </div>
  </div>
  <div className="lp-channel-layout"><div className="lp-channel-main">
   <article className="lp-detail-panel"><div className="lp-panel-heading"><h2>Показатели канала</h2><span>За последние 30 дней</span></div>
    <div className="lp-channel-stat-grid"><div><small>Подписчики</small><strong>{amount(channel.subscribers)}</strong><span>Аудитория канала</span></div>
    <div><small>Средние просмотры</small><strong>{amount(channel.avgViews)}</strong><span>На публикацию</span></div>
    <div><small>CPM</small><strong>{channel.avgViews&&channel.avgViews>0?rub(lowest*1000/channel.avgViews):'Нет данных'}</strong><span>За 1 000 просмотров от минимальной цены</span></div></div>
    <p className="lp-detail-note">Статистика основана на доступных данных канала. Фактический охват конкретного рекламного поста может отличаться.</p>
   </article>
   <article className="lp-detail-panel"><div className="lp-panel-heading"><h2>Отзывы рекламодателей</h2><span>{detail?.reviews.length?detail.reviews.length+' последних':''}</span></div>
    {detailsError?<p className="lp-detail-note">Не удалось загрузить отзывы. Обновите страницу позднее.</p>:!detail?<p className="lp-detail-note">Загружаем отзывы…</p>:detail.reviews.length===0?<p className="lp-detail-empty">Отзывов пока нет. После первых размещений здесь появятся оценки рекламодателей.</p>:detail.reviews.map((review,i)=><div className="lp-review" key={i}><div><strong aria-label={review.stars+' из 5'}>{'★'.repeat(Math.max(0,Math.min(5,review.stars)))}<span>{'☆'.repeat(5-Math.max(0,Math.min(5,review.stars)))}</span></strong>{review.createdAt&&<time dateTime={review.createdAt}>{new Date(review.createdAt).toLocaleDateString('ru-RU')}</time>}</div><p>{review.comment||'Оценка без комментария'}</p></div>)}
   </article>
  </div>
  <aside className="lp-detail-panel lp-detail-aside"><span className="lp-kicker">РАЗМЕЩЕНИЕ РЕКЛАМЫ</span><h2>Выберите формат</h2>
   {detailsError?<p className="lp-detail-note">Тарифы временно недоступны. Актуальные условия можно проверить в Telegram.</p>:!detail?<p className="lp-detail-note">Загружаем тарифы…</p>:rates.length?rates.map((rate,i)=><div className="lp-rate-row" key={i}><span>{rate.hours===0?'Без автоматического удаления':rate.hours%24===0?rate.hours/24+' сут.':rate.hours+' ч.'}</span><strong>{rub(rate.price)}</strong></div>):<p className="lp-detail-note">Дополнительные тарифы пока не заданы. Уточните условия в Telegram.</p>}
   <div className="lp-price-start"><small>Стоимость размещения от</small><strong>{rub(lowest)}</strong></div>
   <a className="lp-buy-link" href={link}>Перейти к оформлению рекламы ↗</a>
   <p className="lp-detail-note">Откроется Reklio с выбранным каналом. Укажите срок, дату публикации и рекламный материал — искать площадку повторно не нужно.</p>
   <div className="lp-promo-entry"><strong>Владелец канала?</strong><p>Выделите площадку цветом и поднимите её выше остальных в каталоге.</p><div><span>7 дней · 199 ₽</span><span>30 дней · 499 ₽</span></div><span>Покупка продвижения скоро появится в Mini App.</span></div>
  </aside></div></>}
 </section>;
}
const legalPages={
 'terms':{title:'Условия использования',sections:[
 ['Назначение сервиса','Reklio помогает находить рекламные площадки, согласовывать заказы и отслеживать размещения в Telegram. Доступ к функциям может зависеть от роли и тарифа.'],
 ['Размещение рекламы','Владелец канала указывает условия и стоимость. Рекламодатель предоставляет материалы и выбирает время. Публикация выполняется после подтверждения заказа согласно доступному процессу.'],
 ['Ответственность сторон','Пользователи отвечают за содержание рекламы, права на материалы и соблюдение применимых правил. Результаты продвижения, охват и продажи не гарантируются.'],
 ['Спорные ситуации','При нарушениях размещения стороны могут обратиться к модерации. Действия по возвратам или выплатам зависят от подключённого платёжного механизма.'],
 ['Статус документа','Это информационный проект условий. Окончательная публичная оферта, юридические реквизиты и условия платежей должны быть утверждены до запуска реальных оплат.']
 ]},
 'privacy':{title:'Конфиденциальность',sections:[
 ['Какие данные используются','Для работы сервиса могут обрабатываться Telegram ID, имя пользователя, сведения о подключённых каналах, заказах, действиях и технические журналы.'],
 ['Зачем это необходимо','Данные нужны для входа через Telegram, выполнения заказов, уведомлений, проверки публикаций и разрешения споров.'],
 ['Передача данных','Техническая обработка может осуществляться через Telegram и поставщиков инфраструктуры. При запуске оплаты действуют также документы соответствующего платёжного провайдера.'],
 ['Обращения','Вопросы о персональных данных и запросы на удаление информации можно направлять по адресу support@reklio.site. Законные обязанности хранения отдельных сведений могут ограничивать удаление.'],
 ['Статус документа','Предварительная информационная редакция. До начала обработки данных на коммерческой основе требуется утвердить полную политику с данными оператора и сроками хранения.']
 ]},
 'advertising':{title:'Правила размещения',sections:[
 ['Требования к рекламе','Запрещены материалы, нарушающие закон, права третьих лиц, правила Telegram или вводящие аудиторию в заблуждение.'],
 ['Обязанности владельца канала','Разместить согласованный материал в установленное время и сохранять его на протяжении выбранного срока. Для тарифа «Навсегда» автоматическое удаление не предусмотрено, но это не гарантия вечной доступности Telegram.'],
 ['Досрочное удаление','Если монитор обнаруживает отсутствие поста до завершения срока, владелец может повторно опубликовать рекламу либо открыть спор.'],
 ['Статистика','Число просмотров и CPM рассчитываются по доступным наблюдениям, не являются прогнозом результата рекламы.'],
 ['Модерация','Платформа вправе ограничивать размещения, требующие дополнительной проверки, в соответствии с утверждаемыми условиями сервиса.']
 ]}
} as const;
function LegalPage({kind}:{kind:keyof typeof legalPages}){const page=legalPages[kind];return <section className="lp-section lp-legal" id={kind}><span className="lp-kicker">ДОКУМЕНТЫ REKLIO</span><h1>{page.title}</h1><p className="lp-legal-note">Предварительная редакция для ознакомления. Не является утверждённой публичной офертой.</p>{page.sections.map(([title,body])=><article key={title}><h2>{title}</h2><p>{body}</p></article>)}<a href="/" className="lp-legal-back">← На главную</a></section>}
function ChannelAliasRedirect(){
 const alias=window.location.pathname.match(/^\/ad\/([a-zA-Z0-9_]{4,32})\/?$/)?.[1];
 const [message,setMessage]=useState('Ищем канал и открываем Reklio в Telegram…');
 useEffect(()=>{
  if(!alias){window.location.replace('/');return;}
  const controller=new AbortController();
  fetch(publicApi+'/api/public/channel-alias/'+encodeURIComponent(alias),{signal:controller.signal})
   .then(async r=>{if(!r.ok)throw new Error(r.status===404?'Такой адрес не найден или канал скрыт.':'Не удалось получить канал.');return r.json() as Promise<{channelId:number}>;})
   .then(v=>{if(!controller.signal.aborted)window.location.replace('https://t.me/ReklioBot?startapp=ad_'+v.channelId);})
   .catch(e=>{if(!controller.signal.aborted)setMessage(e instanceof Error?e.message:'Ошибка открытия');});
  return ()=>controller.abort();
 },[alias]);
 return <div className="lp"><main className="lp-section" style={{minHeight:'70vh',display:'grid',placeItems:'center',textAlign:'center',padding:'32px'}}><div><h1>Reklio</h1><p>{message}</p><a href="https://t.me/ReklioBot">Открыть Reklio в Telegram</a></div></main></div>;
}
export default function Landing(){
 const path=window.location.pathname;
 const aliasRoute=window.location.hostname.replace(/^www\./,'')==='reklio.site' && /^\/ad\/[a-zA-Z0-9_]{4,32}\/?$/.test(path);
 const legalKind=path==='/terms'?'terms':path==='/privacy'?'privacy':path==='/advertising-rules'?'advertising':null;
 const [mobileMenu,setMobileMenu]=useState(false);
 const [themePreference,setThemePreference]=useState<'light'|'dark'|'system'>(()=>{
   try {const saved=localStorage.getItem('reklio-theme');return saved==='light'||saved==='dark'?saved:'system';} catch {return 'system';}
 });
 const [systemDark,setSystemDark]=useState(()=>window.matchMedia('(prefers-color-scheme: dark)').matches);
 useEffect(()=>{
   const media=window.matchMedia('(prefers-color-scheme: dark)');
   const onChange=()=>setSystemDark(media.matches);
   media.addEventListener('change',onChange);
   return ()=>media.removeEventListener('change',onChange);
 },[]);
 const effectiveTheme=themePreference==='system'?(systemDark?'dark':'light'):themePreference;
 useEffect(()=>{document.documentElement.style.colorScheme=effectiveTheme;return ()=>{document.documentElement.style.colorScheme='';};},[effectiveTheme]);
 const chooseTheme=(next:'light'|'dark'|'system')=>{
   setThemePreference(next);
   try {if(next==='system')localStorage.removeItem('reklio-theme');else localStorage.setItem('reklio-theme',next);} catch {}
 };
 const [loginState,setLoginState]=useState<{authenticated:boolean;name?:string;username?:string}|null>(null);
 const [loginAvailable,setLoginAvailable]=useState(false);
 useEffect(()=>{
   const controller=new AbortController();
   fetch(publicApi+'/api/auth/config',{signal:controller.signal})
     .then(r=>r.ok?r.json():null).then(v=>{if(v)setLoginAvailable(Boolean(v.telegramLoginAvailable));}).catch(()=>{});
   fetch(publicApi+'/api/auth/me',{credentials:'include',signal:controller.signal})
     .then(r=>r.ok?r.json():null).then(v=>{if(v)setLoginState(v);}).catch(()=>{});
   return ()=>controller.abort();
 },[]);
 const loginAction=loginState?.authenticated
   ? <div className="lp-login-user"><a href="/account">👤 {loginState.name||loginState.username||'Мой аккаунт'}</a><button type="button" onClick={async()=>{
       try{const response=await fetch(publicApi+'/api/auth/logout',{method:'POST',credentials:'include'});
       if(response.ok)setLoginState({authenticated:false});}catch{}
     }}>Выйти</button></div>
   : <a className="lp-nav-action" href={loginAvailable?publicApi+'/api/auth/login':'https://t.me/ReklioBot'}>{loginAvailable?'Войти через Telegram ↗':'Открыть через Telegram ↗'}</a>;
 const themeToggle=<div className="lp-theme-control" role="group" aria-label="Тема оформления">
   <button type="button" className={themePreference==='light'?'selected':''} aria-label="Светлая тема" aria-pressed={themePreference==='light'} title="Светлая" onClick={()=>chooseTheme('light')}>☀</button>
   <button type="button" className={themePreference==='dark'?'selected':''} aria-label="Тёмная тема" aria-pressed={themePreference==='dark'} title="Тёмная" onClick={()=>chooseTheme('dark')}>☾</button>
   <button type="button" className={themePreference==='system'?'selected':''} aria-label="Системная тема" aria-pressed={themePreference==='system'} title="Как на устройстве" onClick={()=>chooseTheme('system')}>◐</button>
 </div>;
 if(aliasRoute)return <ChannelAliasRedirect/>;
 return <div className={"lp lp-theme-"+effectiveTheme}>
 <header className="lp-nav"><a className="lp-brand" href="/"><img src="/reklio-mark.svg" alt=""/>Rekli<span>o</span></a><nav className={mobileMenu?"lp-nav-links open":"lp-nav-links"}><a href="/" onClick={()=>setMobileMenu(false)}>Главная</a><a href="/catalog" onClick={()=>setMobileMenu(false)}>Каталог</a><a href="/help" onClick={()=>setMobileMenu(false)}>Как работает</a><a href="/pricing" onClick={()=>setMobileMenu(false)}>Тарифы</a><a className="lp-mobile-legal" href="/terms" onClick={()=>setMobileMenu(false)}>Документы и правила</a><a className="lp-mobile-legal" href="mailto:support@reklio.site">Связаться с нами</a></nav>{themeToggle}{loginAction}<button type="button" className="lp-mobile-menu" aria-expanded={mobileMenu} aria-label="Открыть меню" onClick={()=>setMobileMenu(!mobileMenu)}>{mobileMenu?"✕":"☰"}</button></header>
 {mobileMenu&&<div className="lp-mobile-drawer-wrap"><button type="button" className="lp-mobile-scrim" aria-label="Закрыть меню" onClick={()=>setMobileMenu(false)}></button><aside className="lp-mobile-drawer" aria-label="Навигация Reklio"><div className="lp-drawer-title"><b>Навигация</b><button onClick={()=>setMobileMenu(false)} aria-label="Закрыть">✕</button></div><a href="/" onClick={()=>setMobileMenu(false)}>⌂　Главная</a><a href="/catalog" onClick={()=>setMobileMenu(false)}>▦　Каталог каналов</a><a href="/pricing" onClick={()=>setMobileMenu(false)}>✦　Тарифы</a><a href="/help" onClick={()=>setMobileMenu(false)}>ⓘ　Как это работает</a><div className="lp-drawer-divider"/><div className="lp-drawer-login">{loginAction}</div>{themeToggle}<a href="/terms">Условия использования</a><a href="/privacy">Конфиденциальность</a><a href="/advertising-rules">Правила рекламы</a><a href="mailto:support@reklio.site">Связаться с нами</a><a className="lp-drawer-cta" href="https://t.me/ReklioBot">Открыть Reklio ↗</a></aside></div>}
 <main>{(path==="/account"||path==="/account/channels"||path==="/account/orders"||path==="/account/finance") ? <WebAccount/> : legalKind ? <LegalPage kind={legalKind}/> : <>{path!=="/"&&!path.startsWith("/channels/")&&<div className="lp-route-heading"><span>REKLIO / {path==="/catalog"?"ПЛОЩАДКИ":path==="/pricing"?"ПОДПИСКА":"ПОДДЕРЖКА"}</span><h1>{path==="/catalog"?"Каталог каналов":path==="/pricing"?"Тарифы FREE и PRO":"Справочный центр"}</h1><p>{path==="/catalog"?"Подберите Telegram-канал для рекламы и сравните реальные показатели.":path==="/pricing"?"Выберите возможности для своего канала.":"Узнайте, как устроены размещения и контроль рекламы."}</p></div>}
 {path==="/" && <><section className="lp-market-hero">
  <div className="lp-market-copy"><span className="lp-kicker">REKLIO · МАРКЕТПЛЕЙС РЕКЛАМЫ</span><h1>Найдите канал<br/><span>для вашей рекламы</span></h1>
   <p>Сравнивайте площадки по аудитории, просмотрам и стоимости. Выбирайте размещение и управляйте заказами через Telegram.</p>
   <form className="lp-market-search" action="/catalog" method="get"><label htmlFor="lp-search-query">Поиск Telegram-каналов</label><div><input id="lp-search-query" name="q" type="search" placeholder="Название, категория или тематика" maxLength={100}/><button type="submit">Найти каналы ↗</button></div></form>
   <div className="lp-market-quick"><span>Популярные направления:</span><a href="/catalog?q=технологии">Технологии</a><a href="/catalog?q=игры">Игры</a><a href="/catalog?q=новости">Новости</a></div>
  </div>
  <aside className="lp-market-side"><span className="lp-kicker">ВАШИ ВОЗМОЖНОСТИ</span><h2>Покупайте и размещайте рекламу</h2>
   <a href="/catalog"><span className="lp-market-side-icon">⌕</span><span><strong>Я рекламодатель</strong><small>Найти площадку и изучить условия</small></span><b>↗</b></a>
   <a href="/pricing"><span className="lp-market-side-icon">✦</span><span><strong>У меня есть канал</strong><small>Тарифы и подключение площадки</small></span><b>↗</b></a>
   <div className="lp-market-side-note">Заказы, уведомления и публикации — в приложении Reklio внутри Telegram.</div>
  </aside>
 </section>
 </>}{path==="/"&&<GettingStarted/>}{path==="/"&&<HomeFeatured/>}{path==="/catalog"&&<PublicCatalog/>}{path.startsWith("/channels/")&&<ChannelPage/>}{path==="/help"&&<><section className="lp-section lp-benefits" id="benefits"><div className="lp-section-head"><span className="lp-kicker">ВОЗМОЖНОСТИ ПЛАТФОРМЫ</span><h2>Всё для рекламных размещений.<br/><em>В одном месте.</em></h2></div><div className="lp-feature-grid"><article><div className="lp-feature-icon">⌕</div><h3>Поиск площадок</h3><p>Каталог каналов, категории и удобный подбор рекламных предложений.</p></article><article><div className="lp-feature-icon">▦</div><h3>Планирование</h3><p>Выбирайте дату и продолжительность размещения прямо при создании заказа.</p></article><article><div className="lp-feature-icon">◇</div><h3>Управление заказами</h3><p>Статусы, уведомления и история взаимодействий в Telegram.</p></article><article><div className="lp-feature-icon">⛨</div><h3>Проверка публикаций</h3><p>Система контролирует наличие рекламных сообщений и помогает выявлять нарушения.</p></article></div></section>
 <section className="lp-section lp-how" id="how"><div className="lp-section-head"><span className="lp-kicker">ПРОСТОЙ ПРОЦЕСС</span><h2>От идеи до публикации<br/><em>в три шага.</em></h2></div><div className="lp-steps">{items.map(v=><article key={v.n}><span>{v.n}</span><h3>{v.title}</h3><p>{v.desc}</p></article>)}</div></section>
 </>}{path==="/pricing"&&<><section className="lp-banner"><div><span className="lp-kicker">ДЛЯ ВЛАДЕЛЬЦЕВ КАНАЛОВ</span><h2>Ваш канал может стать<br/>рекламной площадкой.</h2><p>Добавляйте канал, устанавливайте цены и управляйте входящими заявками в Telegram.</p><a href="https://t.me/ReklioBot">Подключить канал ↗</a></div><div className="lp-banner-mark"><img src="/reklio-mark.svg" alt=""/></div></section>
 </>}{path==="/help"&&<><section className="lp-section lp-faq" id="faq"><div className="lp-section-head"><span className="lp-kicker">ЧАСТЫЕ ВОПРОСЫ</span><h2>Что важно знать</h2></div><details><summary>Нужно ли устанавливать отдельное приложение?</summary><p>Нет. Reklio работает как Telegram Mini App — достаточно открыть его через Telegram.</p></details><details><summary>Как размещается реклама?</summary><p>Вы выбираете канал, условия и время публикации. После согласования заказ обрабатывается платформой, а статусы отображаются в приложении.</p></details><details><summary>Можно ли оплачивать реальные размещения?</summary><p>Платёжная инфраструктура находится в подготовке. Сейчас доступны тестовые сценарии; реальные безопасные выплаты ещё не запущены.</p></details><details><summary>Как Reklio контролирует рекламу?</summary><p>Для поддерживаемых публичных каналов применяется автоматическая проверка доступности опубликованных сообщений.</p></details></section>
 </>}{path==="/pricing"&&<><section className="lp-section lp-pricing" id="pricing"><div className="lp-section-head"><span className="lp-kicker">ТАРИФЫ ДЛЯ ВЛАДЕЛЬЦЕВ</span><h2>Начните бесплатно.<br/><em>Растите с PRO.</em></h2><p>Выбирайте тариф по количеству каналов. Покупка рекламы оплачивается отдельно по условиям выбранной площадки.</p></div><div className="lp-pricing-grid"><article><div className="lp-pricing-head"><h3>FREE</h3><strong>Бесплатно</strong></div><ul><li>До 1 канала</li><li>Без ограничения по времени</li><li>Базовая статистика</li><li>Комиссия 5%</li><li>Управление заявками и уведомления</li></ul><a href="https://t.me/ReklioBot">Начать бесплатно ↗</a></article><article className="featured"><div className="lp-pricing-head"><h3>PRO</h3><strong>399 ₽ / 30 дней</strong></div><ul><li>До 10 каналов</li><li>Полная аналитика и расширенная статистика</li><li>Комиссия 2,5%</li><li>Все функции управления площадками</li></ul><p>Подключение платежей PRO находится в подготовке.</p><a href="https://t.me/ReklioBot">Узнать о PRO ↗</a></article></div></section>
 </>}{path==="/"&&<><section className="lp-cta"><div className="lp-cta-glow"/><img src="/reklio-mark.svg" alt=""/><h2>Реклама начинается здесь.</h2><p>Попробуйте Reklio внутри Telegram.</p><a href="https://t.me/ReklioBot">Открыть Reklio ↗</a></section>
 </>}</>}
 </main><footer className="lp-footer"><a className="lp-brand" href="/"><img src="/reklio-mark.svg" alt=""/>Reklio</a><span>© 2026 Reklio · Платформа рекламных размещений</span><div className="lp-footer-links"><a href="/terms">Условия</a><a href="/privacy">Конфиденциальность</a><a href="/advertising-rules">Правила рекламы</a><a href="mailto:support@reklio.site">Контакты</a></div></footer>
 </div>
}