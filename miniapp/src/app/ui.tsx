import React, { useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { avatarUrl } from './api';
import { copyText } from './telegram';

export function Loading({ text = 'Загрузка…' }: { text?: string }) {
  return <div className="muted loading" role="status"><RefreshCw size={16}/> {text}</div>;
}

export function ErrorBox({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return <div className="notice" role="alert">{message}{onRetry && <button onClick={onRetry}>Повторить</button>}</div>;
}

export function Empty({ title, text, action }: { title: string; text?: string; action?: React.ReactNode }) {
  return <div className="empty"><strong className="emptyTitle">{title}</strong>{text && <p>{text}</p>}{action}</div>;
}

export function ChannelAvatar({ id, title, extraClass = '' }: { id: number; title: string; extraClass?: string }) {
  const [failed, setFailed] = useState(false);
  return <div className={'avatar ' + extraClass}>
    {!failed && <img src={avatarUrl(id)} alt="" loading="lazy" onError={() => setFailed(true)}/>}
    <span style={{ display: failed ? '' : 'none' }}>{title.slice(0, 1).toUpperCase()}</span>
  </div>;
}

/** Guards an action that cannot be taken back: the first tap only asks for confirmation. */
export function ConfirmButton({ className, question, disabled, onConfirm, children }: {
  className: string;
  question: string;
  disabled?: boolean;
  onConfirm: () => void;
  children: React.ReactNode;
}) {
  const [asking, setAsking] = useState(false);
  if (!asking) return <button type="button" className={className} disabled={disabled} onClick={() => setAsking(true)}>{children}</button>;
  return <div className="confirmBox">
    <p>{question}</p>
    <div className="orderActions">
      <button type="button" className="primary small" disabled={disabled} onClick={() => { setAsking(false); onConfirm(); }}>Да, подтверждаю</button>
      <button type="button" className="secondary small" onClick={() => setAsking(false)}>Отмена</button>
    </div>
  </div>;
}

export function CopyRow({ label, value, copyValue }: { label: string; value: string; copyValue?: string }) {
  const [copied, setCopied] = useState(false);
  async function copy() {
    const ok = await copyText(copyValue ?? value);
    setCopied(ok);
    if (ok) window.setTimeout(() => setCopied(false), 2000);
  }
  return <div className="copyRow">
    <div><small>{label}</small><strong>{value}</strong></div>
    <button type="button" className="secondary small" onClick={() => void copy()}>{copied ? '✓ Скопировано' : 'Копировать'}</button>
  </div>;
}

export function LineChart({ label, color, values, from, to, caption }: {
  label: string;
  color: string;
  values: number[];
  from: string;
  to: string;
  caption: string;
}) {
  const min = Math.min(...values);
  const max = Math.max(...values);
  const points = values
    .map((value, i) => `${(i / (values.length - 1) * 100).toFixed(2)},${(95 - (value - min) / Math.max(1, max - min) * 85).toFixed(2)}`)
    .join(' ');
  return <div className="historyCard">
    <svg viewBox="0 0 100 100" preserveAspectRatio="none" role="img" aria-label={label}>
      <polyline points={points} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke"/>
    </svg>
    <div className="historyLabels"><span>{from}</span><span>{to}</span></div>
    <small>{caption}</small>
  </div>;
}

export const OfferNote = () =>
  <small className="hint">Оплачивая, вы принимаете <a href="https://reklio.site/terms" target="_blank" rel="noopener noreferrer">оферту</a>.</small>;
