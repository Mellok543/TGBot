import type { Tab } from './types';

const STORAGE_KEY = 'reklio-welcome-seen';

export function welcomeSeen(): boolean {
  try { return localStorage.getItem(STORAGE_KEY) === '1'; }
  catch { return false; }
}

function rememberWelcome() {
  try { localStorage.setItem(STORAGE_KEY, '1'); }
  catch { /* Without storage the greeting simply shows again next time. */ }
}

export function HowItWorks() {
  return <>
    <h3>Если вы покупаете рекламу</h3>
    <ol>
      <li>Выберите канал и срок, напишите текст и выберите время.</li>
      <li>Дождитесь одобрения владельца. До этого платить ничего не нужно.</li>
      <li>Переведите деньги по реквизитам владельца и нажмите «Я перевёл деньги».</li>
      <li>Владелец подтвердит перевод, и реклама выйдет сама.</li>
    </ol>
    <h3>Если у вас есть канал</h3>
    <ol>
      <li>Добавьте @ReklioBot в администраторы канала и подключите канал.</li>
      <li>Укажите категорию, цены и реквизиты, затем опубликуйте канал.</li>
      <li>Одобряйте заявки и подтверждайте полученную оплату.</li>
      <li>Бот сам опубликует пост и удалит его по окончании срока.</li>
    </ol>
    <h3>Куда идут деньги</h3>
    <p>Покупатель платит напрямую владельцу канала. Reklio не получает эти деньги, не берёт комиссию и не может их вернуть.</p>
    <h3>Если что-то пошло не так</h3>
    <ul>
      <li><b>Деньги перевели, но владелец не подтверждает.</b> Он должен ответить за 24 часа. Если он пишет, что денег нет, или молчит, откройте спор.</li>
      <li><b>Пост удалили раньше срока.</b> У владельца есть 24 часа, чтобы опубликовать его снова, иначе спор откроется сам. Покупатель может открыть его и сразу.</li>
      <li><b>Реклама не вышла.</b> Через час после назначенного времени можно открыть спор.</li>
    </ul>
    <p>Споры решает администрация Reklio и может заблокировать канал на время или навсегда. Перед оплатой проверьте канал, а после перевода сохраните чек.</p>
  </>;
}

export function Welcome({ onClose }: { onClose: (tab?: Tab) => void }) {
  function choose(tab?: Tab) {
    rememberWelcome();
    onClose(tab);
  }
  return <div className="bookingOverlay" role="dialog" aria-modal="true" aria-label="Добро пожаловать"><div className="bookingPanel aboutPanel">
    <div className="aboutHero">
      <div className="eyebrow">REKLIO</div>
      <h2>Реклама в Telegram-каналах</h2>
      <p>Покупайте размещения напрямую у владельцев каналов или продавайте рекламу в своём канале. Бот сам публикует и удаляет посты.</p>
    </div>
    <h3>С чего начнём?</h3>
    <button className="primary" onClick={() => choose('catalog')}>Хочу купить рекламу</button>
    <button className="secondary welcomeSecond" onClick={() => choose('channels')}>У меня есть канал</button>
    <details className="channelMore"><summary>Как это работает</summary><HowItWorks/></details>
    <button className="back" onClick={() => choose()}>Пропустить</button>
  </div></div>;
}
