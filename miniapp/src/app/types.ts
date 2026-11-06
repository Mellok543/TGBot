export type Tab = 'catalog' | 'orders' | 'channels' | 'profile';

export type Channel = {
  id: number;
  title: string;
  category: string;
  price: number;
  description: string;
  username: string | null;
  subscribers: number | null;
  posts30d: number;
  avgViews: number | null;
  viewsSamples: number;
  promotedUntil?: string | null;
  likes: number;
};

export type Rate = { id: number; hours: number; price: number };
export type SellerRate = Rate & { active: boolean };
export type HistoryPoint = { day: string; subscribers: number | null; posts: number; avgViews: number | null };
export type ChannelReview = { stars: number; comment: string | null; createdAt: string };
export type MonthlyTop = { month: string; channels: { id: number; title: string; category: string; votes: number; place: number }[] };

export type Order = {
  id: number;
  channel: string;
  price: number;
  status: string;
  createdAt: string;
  isOwner: boolean;
  text: string;
  scheduledAt: string | null;
  durationHours: number | null;
  paymentStatus: string;
  disputeReason: string | null;
  publicationIssue: string | null;
  publishedAt: string | null;
  claimStatus: string | null;
  claimedAt: string | null;
  resolution: string | null;
  counterpart: string | null;
  canDispute: boolean;
  hasImage: boolean;
};

export type DirectPayment = {
  cardNumber: string | null;
  phone: string | null;
  bank: string | null;
  amount: number;
  channelBlocked: boolean;
};

export type PaymentDetails = { cardNumber: string; phone: string; bank: string };

export type MyChannel = {
  id: number;
  title: string;
  listed: boolean;
  price: number;
  category: string;
  alias?: string | null;
  blocked?: boolean;
  blockedUntil?: string | null;
};

export type ChannelAnalytics = { days: number; points: HistoryPoint[]; orders: number; published: number; revenue: number; pending: number };

export type Subscription = {
  plan: 'FREE' | 'PRO';
  isPro: boolean;
  expiresAt: string | null;
  channelLimit: number;
  proPriceRub: number | null;
  checkoutAvailable: boolean;
  checkoutMessage: string;
};

export type PaymentRecord = { orderUuid: string; product: string; amountKopecks: number; status: string; createdAt: string };

export type Me = { id: number; isDisputeAdmin: boolean; adminRole: string | null };

export type StaffMember = { id: number; role: string; grantedBy: number; createdAt: string };
export type AdminUser = { id: number; name: string; username: string | null; channels: number; purchases: number; blocked: boolean; blockedUntil: string | null; firstSeen: string };
export type AdminOrder = { id: number; channel: string; status: string; price: number; buyerId: number; ownerId: number; createdAt: string };
export type AuditItem = { id: number; actorId: number; action: string; targetId: number | null; details: string | null; createdAt: string };
export type ModerationChannel = { id: number; title: string; category: string; listed: boolean; ownerId: number; price: number; blocked: boolean; blockedUntil: string | null };
export type MtprotoStatus = { configured: boolean; checkedOrders: number; present: number; missing: number; lastCheck: string | null; healthy: boolean };
export type PublicationIssue = { id: number; channel: string; status: string; error: string; scheduledAt: string | null; publishedAt: string | null };
export type AdminOverview = { channels: number; listed: number; orders: number; openDisputes: number; failedPublications: number };
export type FeedbackItem = { id: number; kind: string; message: string; status: string; reply: string | null; createdAt: string };
export type AdminFeedback = FeedbackItem & { userId: number; name: string | null; username: string | null };
export type AdminDispute = { id: number; channel: string; price: number; text: string; reason: string; openedAt: string | null; status: string; resolution: string | null; paymentStatus: string };
