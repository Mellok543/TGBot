import { useEffect, useState } from 'react';
import { Admin } from './Admin';
import { get, getPublicChannel } from './api';
import { Catalog } from './Catalog';
import { ChannelPage } from './ChannelPage';
import { Channels } from './Channels';
import { errorText } from './format';
import { Orders } from './Orders';
import { PlanCard } from './PlanCard';
import type { Channel, Subscription, Tab } from './types';
import { ErrorBox, Loading } from './ui';
import { useOrders } from './useOrders';

// These sections share the Mini App's screens, so the website cannot drift from its rules.
export function SiteOrders() {
  const { orders, loading, error, message, reload } = useOrders(15000);
  const navigate = (tab: Tab) => window.location.assign(tab === 'catalog' ? '/account/catalog' : '/account/channels');
  return <div className="app embeddedApp">
    <Orders orders={orders} loading={loading} error={error} message={message} onReload={next => void reload(next)} onNavigate={navigate}/>
  </div>;
}

function useSubscription() {
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  useEffect(() => { get<Subscription>('/api/subscription').then(setSubscription).catch(() => {}); }, []);
  return [subscription, setSubscription] as const;
}

export function SitePlan() {
  const [subscription, setSubscription] = useSubscription();
  return <div className="app embeddedApp"><PlanCard subscription={subscription} onChange={setSubscription}/></div>;
}

export function SiteChannels({ editId }: { editId: number | null }) {
  const [subscription] = useSubscription();
  return <div className="app embeddedApp"><Channels subscription={subscription} initialEditId={editId}/></div>;
}

export function SiteCatalog() {
  return <div className="app embeddedApp"><Catalog onOrderCreated={() => window.location.assign('/account/orders')}/></div>;
}

export function SiteAdmin({ role }: { role: string }) {
  return <div className="app embeddedApp"><Admin role={role}/></div>;
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
