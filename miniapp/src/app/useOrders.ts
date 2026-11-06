import { useEffect, useState } from 'react';
import { get } from './api';
import { errorText } from './format';
import type { Order } from './types';

export function useOrders(pollMs: number) {
  const [orders, setOrders] = useState<Order[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');

  async function reload(nextMessage?: string) {
    if (nextMessage !== undefined) setMessage(nextMessage);
    try {
      setOrders(await get<Order[]>('/api/orders'));
      setError('');
    } catch (e) {
      setError(errorText(e, 'Не удалось загрузить заказы'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void reload();
    const timer = window.setInterval(() => void reload(), pollMs);
    return () => window.clearInterval(timer);
  }, [pollMs]);

  return { orders, loading, error, message, setMessage, reload };
}
