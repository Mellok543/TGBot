import { useEffect, useState } from 'react';
import { get, getPublicChannel } from './api';
import { ChannelPage } from './ChannelPage';
import { errorText } from './format';
import { Orders } from './Orders';
import { PlanCard } from './PlanCard';
import type { Channel, Subscription, Tab } from './types';
import { ErrorBox, Loading } from './ui';
import { useOrders } from './useOrders';

// These sections share the Mini App's screens, so the website cannot drift from its order rules.
export function SiteOrders() {
  const { orders, loading, error, message, reload } = useOrders(15000);
  const navigate = (tab: Tab) => window.location.assign(tab === 'catalog' ? '/catalog' : '/account/channels');
  return <div className="app embeddedApp">
    <Orders orders={orders} loading={loading} error={error} message={message} onReload={next => void reload(next)} onNavigate={navigate}/>
  </div>;
}

export function SitePlan() {
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  useEffect(() => { get<Subscription>('/api/subscription').then(setSubscription).catch(() => {}); }, []);
  return <div className="app embeddedApp"><PlanCard subscription={subscription} onChange={setSubscription}/></div>;
}

export function SiteBooking({ channelId }: { channelId: number }) {
  const [channel, setChannel] = useState<Channel | null>(null);
  const [error, setError] = useState('');
  useEffect(() => { getPublicChannel(channelId).then(setChannel).catch(e => setError(errorText(e, 'Не удалось открыть канал'))); }, [channelId]);
  if (error) return <div className="app embeddedApp"><ErrorBox message={error}/></div>;
  if (!channel) return <div className="app embeddedApp"><Loading/></div>;
  return <div className="app embeddedApp"><ChannelPage
    channel={channel}
    onBack={() => window.history.back()}
    onOrderCreated={() => window.location.assign('/account/orders')}/></div>;
}
