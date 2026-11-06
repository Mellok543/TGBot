import { useState } from 'react';
import { webApp } from './telegram';
import type { Me, Order, Subscription } from './types';
import { Admin } from './Admin';
import { Feedback } from './Feedback';
import { PlanCard } from './PlanCard';
import { HowItWorks } from './Welcome';

export function Profile({ me, orders, subscription, onSubscriptionChange }: {
  me: Me | null;
  orders: Order[];
  subscription: Subscription | null;
  onSubscriptionChange: (value: Subscription) => void;
}) {
  const [admin, setAdmin] = useState(false);
  const user = webApp()?.initDataUnsafe?.user;

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

    <PlanCard subscription={subscription} onChange={onSubscriptionChange}/>

    <div className="profileStats">
      <div><span>Покупок</span><strong>{purchases.length}</strong></div>
      <div><span>Покупок выполнено</span><strong>{completed(purchases)}</strong></div>
      <div><span>Продаж</span><strong>{sales.length}</strong></div>
      <div><span>Продаж выполнено</span><strong>{completed(sales)}</strong></div>
    </div>

    <details className="card aboutPanel"><summary>Как работает Reklio</summary><HowItWorks/></details>

    <Feedback/>

    <section className="card">
      <h3>Документы</h3>
      <p className="hint">
        <a href="https://reklio.site/terms" target="_blank" rel="noopener noreferrer">Оферта</a> · <a href="https://reklio.site/advertising-rules" target="_blank" rel="noopener noreferrer">Правила размещения</a> · <a href="https://reklio.site/privacy" target="_blank" rel="noopener noreferrer">Конфиденциальность</a>
      </p>
    </section>

    {me?.isDisputeAdmin && <button className="secondary sellerToggle" onClick={() => setAdmin(!admin)}>{admin ? 'Скрыть панель модерации' : 'Панель модерации'}</button>}
    {admin && me && <Admin role={me.adminRole}/>}
  </div>;
}
