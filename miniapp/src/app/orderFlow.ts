import { parseUtc } from './format';
import type { Order } from './types';

export type OrderAction = 'approve' | 'reject' | 'pay' | 'confirmPayment' | 'denyPayment' | 'reschedule' | 'cancel' | 'dispute' | 'republish';

/** What the person looking at the order needs to know and can do. */
export type OrderView = {
  label: string;
  tone: 'action' | 'wait' | 'done' | 'problem';
  /** The order is waiting on this person, not on the other side. */
  needsYou: boolean;
  title: string;
  text: string;
  actions: OrderAction[];
};

const HOUR = 60 * 60 * 1000;
const CLAIM_WAIT_HOURS = 24;

function hoursUntilDisputeAllowed(order: Order, now: number): number {
  if (!order.claimedAt) return CLAIM_WAIT_HOURS;
  return Math.max(1, Math.ceil(CLAIM_WAIT_HOURS - (now - parseUtc(order.claimedAt).getTime()) / HOUR));
}

// The server only lets a buyer move an unpaid order when its time is within the hour.
function tooCloseToPublish(order: Order, now: number): boolean {
  return !!order.scheduledAt && parseUtc(order.scheduledAt).getTime() <= now + HOUR;
}

const view = (label: string, tone: OrderView['tone'], needsYou: boolean, title: string, text: string, actions: OrderAction[] = []): OrderView =>
  ({ label, tone, needsYou, title, text, actions });

function ownerView(order: Order): OrderView {
  switch (order.status) {
    case 'Pending':
      return view('Новая заявка', 'action', true, 'Проверьте заявку', 'Прочитайте текст рекламы и дату. Деньги покупатель переведёт только после вашего одобрения.', ['approve', 'reject']);
    case 'PendingPaid':
      return view('Новое время', 'action', true, 'Покупатель выбрал новое время', 'Оплата уже подтверждена вами. Согласуйте новую дату или отмените размещение и верните деньги.', ['approve', 'cancel']);
    case 'AwaitingPayment':
      if (order.claimStatus === 'BuyerClaimed')
        return view('Проверьте оплату', 'action', true, 'Покупатель сообщил о переводе', 'Откройте банк и проверьте поступление. Подтверждайте, только если деньги действительно пришли: после этого реклама уйдёт в публикацию.', ['confirmPayment', 'denyPayment']);
      if (order.claimStatus === 'Rejected')
        return view('Оплата не подтверждена', 'problem', false, 'Вы сообщили, что денег нет', 'Покупатель может перевести деньги повторно или открыть спор. Если деньги всё же пришли, напишите ему и попросите нажать «Я перевёл деньги» ещё раз.', ['cancel']);
      return view('Ждём оплату', 'wait', false, 'Ждём перевод от покупателя', 'Он видит ваши реквизиты. Когда он сообщит о переводе, вам придёт уведомление.', ['cancel']);
    case 'PaidReschedule':
      return view('Нужно новое время', 'wait', false, 'Ждём новое время от покупателя', 'Оплата подтверждена, но время публикации прошло. Покупатель выбирает новое время.', ['cancel']);
    case 'Scheduled':
      return view('Запланирована', 'wait', false, 'Реклама запланирована', 'Бот опубликует её сам. Не убирайте бота из администраторов канала.', ['cancel']);
    case 'Publishing':
      return view('Публикуется', 'wait', false, 'Публикуем рекламу', 'Обычно это занимает несколько секунд. Если статус не меняется, напишите в поддержку.');
    case 'Published':
      return view('Опубликована', 'wait', false, 'Реклама в канале', 'Не удаляйте пост до конца оплаченного срока, иначе покупатель сможет открыть спор, а канал могут заблокировать.');
    case 'EarlyDeletionPending':
      return view('Пост удалён', 'problem', true, 'Пост пропал раньше срока', 'Опубликуйте его снова в течение 24 часов, срок начнётся заново. Иначе автоматически откроется спор.', ['republish']);
    case 'PublishFailed':
      return view('Не опубликована', 'problem', true, 'Telegram не принял публикацию', 'Проверьте, что бот остаётся администратором канала с правом публикации, и опубликуйте снова.', ['republish']);
    case 'Deleted':
      return view('Завершена', 'done', false, 'Размещение завершено', 'Срок истёк, бот удалил пост. Заказ выполнен.');
    case 'DeleteFailed':
      return view('Удалите пост', 'action', true, 'Не удалось удалить пост', 'Срок размещения истёк, но бот не смог убрать пост. Удалите его вручную.');
    case 'Disputed':
      return view('Открыт спор', 'problem', false, 'Покупатель открыл спор', 'Решение примет администрация Reklio. Подготовьте подтверждение: чек перевода, скриншоты и переписку.');
    case 'DisputeResolved':
      return resolvedView(order);
    case 'Rejected':
      return view('Отклонена', 'done', false, 'Вы отклонили заявку', 'Покупатель получил уведомление.');
    case 'Cancelled':
      return order.paymentStatus === 'DirectPaid'
        ? view('Отменена', 'problem', false, 'Вы отменили оплаченный заказ', 'Верните деньги покупателю. Иначе он может открыть спор, а канал могут заблокировать.')
        : view('Отменена', 'done', false, 'Заявка отменена', 'Заказ закрыт, деньги не переводились.');
    case 'Expired':
      return view('Истекла', 'done', false, 'Заявка истекла', 'Время публикации прошло, а заказ не был оплачен. Слот снова свободен.');
    default:
      return view(order.status, 'wait', false, 'Заказ в обработке', 'Обновите список чуть позже.');
  }
}

function resolvedView(order: Order): OrderView {
  const buyerRight = order.resolution === 'BuyerRight' || order.resolution === 'RefundRecommended';
  return view('Спор решён', 'done', false, buyerRight ? 'Администрация признала нарушение' : 'Нарушение не подтверждено',
    'Деньги передавались напрямую, поэтому Reklio не может вернуть их сам. Подробности решения пришли в уведомлении.');
}

function buyerView(order: Order, now: number): OrderView {
  const dispute: OrderAction[] = order.canDispute ? ['dispute'] : [];
  switch (order.status) {
    case 'Pending':
      return view('На согласовании', 'wait', false, 'Ждём ответ владельца', 'Пока ничего платить не нужно. Реквизиты появятся здесь после одобрения.',
        tooCloseToPublish(order, now) ? ['reschedule', 'cancel'] : ['cancel']);
    case 'AwaitingPayment':
      if (order.claimStatus === 'BuyerClaimed') {
        const wait = hoursUntilDisputeAllowed(order, now);
        return view('Проверка оплаты', 'wait', false, 'Владелец проверяет перевод',
          order.canDispute ? 'Владелец не ответил за 24 часа. Напишите ему или откройте спор.' : `Обычно это быстро. Если ответа не будет ещё ${wait} ч, вы сможете открыть спор.`,
          dispute);
      }
      if (order.claimStatus === 'Rejected')
        return view('Оплата не найдена', 'problem', true, 'Владелец не нашёл перевод', 'Проверьте реквизиты и сумму. Если вы уже перевели деньги, сообщите о переводе ещё раз или откройте спор.', ['pay', ...dispute, 'cancel']);
      return view('Нужна оплата', 'action', true, 'Заявка одобрена — оплатите', 'Переведите точную сумму по реквизитам владельца и нажмите «Я перевёл деньги». Reklio деньги не получает.',
        tooCloseToPublish(order, now) ? ['pay', 'reschedule', 'cancel'] : ['pay', 'cancel']);
    case 'PaidReschedule':
      return view('Выберите время', 'action', true, 'Оплата подтверждена, нужно новое время', 'Прежнее время публикации уже прошло. Выберите новое, владелец его согласует.', ['reschedule', ...dispute]);
    case 'PendingPaid':
      return view('Новое время', 'wait', false, 'Владелец согласует новое время', 'Оплата уже подтверждена, ничего платить не нужно.', dispute);
    case 'Scheduled':
      return view('Запланирована', 'wait', false, 'Оплата подтверждена', 'Реклама выйдет автоматически в назначенное время.', dispute);
    case 'Publishing':
      return view('Публикуется', 'wait', false, 'Публикуем рекламу', 'Обычно это занимает несколько секунд.', dispute);
    case 'Published':
      return view('Опубликована', 'done', false, 'Реклама в канале', 'Проверьте пост в канале. Если он пропал раньше срока, откройте спор.', dispute);
    case 'EarlyDeletionPending':
      return view('Пост удалён', 'problem', false, 'Пост пропал раньше срока', 'У владельца есть 24 часа, чтобы опубликовать его снова. Иначе спор откроется автоматически. Вы можете открыть его и сейчас.', dispute);
    case 'PublishFailed':
      return view('Не опубликована', 'problem', false, 'Telegram не принял публикацию', 'Владелец может опубликовать рекламу снова. Если он не реагирует, откройте спор.', dispute);
    case 'Deleted':
      return view('Завершена', 'done', false, 'Размещение завершено', 'Срок истёк, пост удалён автоматически. Если что-то пошло не так, можно открыть спор.', dispute);
    case 'DeleteFailed':
      return view('Завершена', 'done', false, 'Срок размещения истёк', 'Бот не смог удалить пост, владелец уберёт его вручную.', dispute);
    case 'Disputed':
      return view('Открыт спор', 'problem', false, 'Спор на рассмотрении', 'Решение примет администрация Reklio. Сохраните чек перевода и переписку с владельцем.');
    case 'DisputeResolved':
      return resolvedView(order);
    case 'Rejected':
      return view('Отклонена', 'done', false, 'Владелец отклонил заявку', 'Деньги переводить не нужно. Выберите другой канал в каталоге.');
    case 'Cancelled':
      return order.paymentStatus === 'DirectPaid'
        ? view('Отменена', 'problem', true, 'Владелец отменил оплаченный заказ', 'Он должен вернуть деньги. Если этого не произошло, откройте спор.', dispute)
        : view('Отменена', 'done', false, 'Заявка отменена', 'Деньги не переводились.');
    case 'Expired':
      return view('Истекла', 'done', false, 'Заявка истекла', 'Время публикации прошло, а заказ не был оплачен. Оформите новую заявку в каталоге.');
    default:
      return view(order.status, 'wait', false, 'Заказ в обработке', 'Обновите список чуть позже.');
  }
}

export const orderView = (order: Order, now = Date.now()): OrderView =>
  order.isOwner ? ownerView(order) : buyerView(order, now);

export const needsAttention = (orders: Order[]) => orders.filter(order => orderView(order).needsYou).length;
