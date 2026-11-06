import { describe, expect, it } from 'vitest';
import { needsAttention, orderView } from './orderFlow';
import type { Order } from './types';

const NOW = Date.parse('2026-11-06T12:00:00Z');

const order = (overrides: Partial<Order>): Order => ({
  id: 1, channel: 'Канал', price: 1000, status: 'Pending', createdAt: '2026-11-06T10:00:00', isOwner: false, text: 'Текст',
  scheduledAt: '2026-11-08T12:00:00', durationHours: 24, paymentStatus: 'Unpaid', disputeReason: null, publicationIssue: null,
  publishedAt: null, claimStatus: null, claimedAt: null, resolution: null, counterpart: null, canDispute: false,
  ...overrides
});

describe('direct payment', () => {
  it('asks the buyer to pay once the owner approves', () => {
    const view = orderView(order({ status: 'AwaitingPayment' }), NOW);
    expect(view.needsYou).toBe(true);
    expect(view.actions).toEqual(['pay', 'cancel']);
  });

  it('asks the owner to check the bank after the buyer reports a transfer', () => {
    const view = orderView(order({ status: 'AwaitingPayment', claimStatus: 'BuyerClaimed', isOwner: true }), NOW);
    expect(view.needsYou).toBe(true);
    expect(view.actions).toEqual(['confirmPayment', 'denyPayment']);
  });

  it('tells the buyer how long to wait before a dispute opens', () => {
    const view = orderView(order({ status: 'AwaitingPayment', claimStatus: 'BuyerClaimed', claimedAt: '2026-11-06T06:00:00' }), NOW);
    expect(view.needsYou).toBe(false);
    expect(view.text).toContain('18 ч');
    expect(view.actions).toEqual([]);
  });

  it('offers a dispute when the owner stays silent', () => {
    const view = orderView(order({ status: 'AwaitingPayment', claimStatus: 'BuyerClaimed', canDispute: true }), NOW);
    expect(view.actions).toEqual(['dispute']);
  });

  it('lets the buyer retry or dispute when the owner says no money arrived', () => {
    const view = orderView(order({ status: 'AwaitingPayment', claimStatus: 'Rejected', canDispute: true }), NOW);
    expect(view.needsYou).toBe(true);
    expect(view.actions).toEqual(['pay', 'dispute', 'cancel']);
  });

  it('asks for a new time when payment was confirmed too late', () => {
    const view = orderView(order({ status: 'PaidReschedule', paymentStatus: 'DirectPaid', canDispute: true }), NOW);
    expect(view.needsYou).toBe(true);
    expect(view.actions).toEqual(['reschedule', 'dispute']);
  });

  it('only offers a new time on an unpaid order within the hour', () => {
    expect(orderView(order({ status: 'AwaitingPayment', scheduledAt: '2026-11-06T12:30:00' }), NOW).actions).toContain('reschedule');
    expect(orderView(order({ status: 'AwaitingPayment' }), NOW).actions).not.toContain('reschedule');
  });
});

describe('publication problems', () => {
  it('asks the owner to repost a deleted post and lets the buyer dispute', () => {
    const owner = orderView(order({ status: 'EarlyDeletionPending', paymentStatus: 'DirectPaid', isOwner: true }), NOW);
    expect(owner.needsYou).toBe(true);
    expect(owner.actions).toEqual(['republish']);
    const buyer = orderView(order({ status: 'EarlyDeletionPending', paymentStatus: 'DirectPaid', canDispute: true }), NOW);
    expect(buyer.needsYou).toBe(false);
    expect(buyer.actions).toEqual(['dispute']);
  });

  it('offers a repost when Telegram refused the message', () => {
    expect(orderView(order({ status: 'PublishFailed', paymentStatus: 'DirectPaid', isOwner: true }), NOW).actions).toEqual(['republish']);
  });

  it('never offers the owner a dispute against the buyer', () => {
    for (const status of ['Published', 'Deleted', 'EarlyDeletionPending', 'PublishFailed', 'Cancelled'])
      expect(orderView(order({ status, isOwner: true, canDispute: true }), NOW).actions).not.toContain('dispute');
  });
});

describe('closed orders', () => {
  it('tells a buyer to chase a refund after a paid order is cancelled', () => {
    const view = orderView(order({ status: 'Cancelled', paymentStatus: 'DirectPaid', canDispute: true }), NOW);
    expect(view.tone).toBe('problem');
    expect(view.actions).toEqual(['dispute']);
  });

  it('has nothing to do on an unpaid cancelled, expired or rejected order', () => {
    for (const status of ['Cancelled', 'Expired', 'Rejected']) expect(orderView(order({ status }), NOW).actions).toEqual([]);
  });

  it('shows a readable fallback for an unknown status', () => {
    expect(orderView(order({ status: 'SomethingNew' }), NOW).actions).toEqual([]);
  });
});

it('counts the orders waiting on this person', () => {
  expect(needsAttention([
    order({ status: 'AwaitingPayment' }),
    order({ status: 'Pending' }),
    order({ status: 'Pending', isOwner: true })
  ])).toBe(2);
});
