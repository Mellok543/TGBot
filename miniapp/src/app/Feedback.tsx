import { useEffect, useState, type FormEvent } from 'react';
import { get, post } from './api';
import { dateTime, errorText } from './format';
import type { FeedbackItem } from './types';

export const FEEDBACK_KINDS: Record<string, string> = { idea: 'Идея или пожелание', bug: 'Ошибка', question: 'Вопрос' };
const STATUS: Record<string, string> = { new: 'Отправлено, ждёт ответа', answered: 'Есть ответ', closed: 'Прочитано' };

export function Feedback() {
  const [kind, setKind] = useState('idea');
  const [message, setMessage] = useState('');
  const [items, setItems] = useState<FeedbackItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [sent, setSent] = useState(false);

  const load = () => get<FeedbackItem[]>('/api/feedback').then(setItems);
  useEffect(() => { load().catch(() => {}); }, []);

  async function send(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError('');
    setSent(false);
    try {
      await post('/api/feedback', { kind, message: message.trim() });
      setMessage('');
      setSent(true);
      await load();
    } catch (e) {
      setError(errorText(e, 'Не удалось отправить сообщение'));
    } finally {
      setBusy(false);
    }
  }

  return <section className="card" id="feedback">
    <h3>Связаться с нами</h3>
    <p className="description">Расскажите об ошибке, предложите идею или задайте вопрос. Мы читаем каждое сообщение.</p>
    <form onSubmit={e => void send(e)}>
      <label className="field">Тема
        <select value={kind} onChange={e => setKind(e.target.value)}>
          {Object.entries(FEEDBACK_KINDS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
      </label>
      <label className="field">Сообщение
        <textarea rows={4} required minLength={5} maxLength={2000} value={message} onChange={e => setMessage(e.target.value)} placeholder="Если речь об ошибке, опишите, что вы делали, и укажите номер заказа"/>
      </label>
      {error && <div className="notice" role="alert">{error}</div>}
      {sent && <div className="notice success" role="status">Сообщение отправлено. Ответ придёт в бот @ReklioBot и появится здесь.</div>}
      <button className="primary small" disabled={busy || message.trim().length < 5}>{busy ? 'Отправляем…' : 'Отправить'}</button>
    </form>
    <p className="hint">Также можно написать на support@reklio.site.</p>
    {items.length > 0 && <>
      <h3 className="detailSubtitle">Мои обращения</h3>
      {items.map(item => <div className="feedbackItem" key={item.id}>
        <small>{FEEDBACK_KINDS[item.kind] ?? item.kind} · {dateTime(item.createdAt)} · {STATUS[item.status] ?? item.status}</small>
        <p className="description">{item.message}</p>
        {item.reply && <p className="feedbackReply"><strong>Ответ Reklio:</strong> {item.reply}</p>}
      </div>)}
    </>}
  </section>;
}
