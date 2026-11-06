import { describe, expect, it } from 'vitest';
import { sourceFromStartParam, telegramLinkWithSource, validSource } from './traffic';

describe('traffic sources', () => {
  it('accepts known sources with an optional video number', () => {
    expect(validSource('tiktok')).toBe('tiktok');
    expect(validSource('tiktok_12')).toBe('tiktok_12');
  });

  it('rejects anything outside the list', () => {
    expect(validSource('direct')).toBeNull();
    expect(validSource('tiktok_abc')).toBeNull();
    expect(validSource('<script>')).toBeNull();
    expect(validSource(null)).toBeNull();
  });

  it('reads the source from a Telegram start parameter', () => {
    expect(sourceFromStartParam('src_vk_2')).toBe('vk_2');
    expect(sourceFromStartParam('ad_123')).toBeNull();
    expect(sourceFromStartParam(undefined)).toBeNull();
  });

  it('adds the source to general Telegram links only', () => {
    expect(telegramLinkWithSource('https://t.me/ReklioBot', 'tiktok')).toBe('https://t.me/ReklioBot?startapp=src_tiktok');
    expect(telegramLinkWithSource('https://t.me/ReklioBot?startapp', 'tiktok')).toBe('https://t.me/ReklioBot?startapp=src_tiktok');
    expect(telegramLinkWithSource('https://t.me/ReklioBot?startapp=ad_5', 'tiktok')).toBe('https://t.me/ReklioBot?startapp=ad_5');
    expect(telegramLinkWithSource('https://reklio.site/terms', 'tiktok')).toBe('https://reklio.site/terms');
  });
});
