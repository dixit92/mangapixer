import { vi } from 'vitest';
import { PagePrefetchQueue, PrefetchFetcher } from './page-prefetch-queue';

describe('PagePrefetchQueue', () => {
  let calls: string[];
  let resolvers: Map<string, () => void>;
  let signals: Map<string, AbortSignal>;
  let fetcher: PrefetchFetcher;

  beforeEach(() => {
    calls = [];
    resolvers = new Map();
    signals = new Map();
    fetcher = (url, signal) => {
      calls.push(url);
      signals.set(url, signal);
      return new Promise<void>((resolve) => resolvers.set(url, resolve));
    };
  });

  const flush = () => new Promise((r) => setTimeout(r, 0));

  it('runs at most maxInFlight at a time, in order, and starts the next when one finishes', async () => {
    const q = new PagePrefetchQueue(fetcher, 2);
    ['a', 'b', 'c', 'd'].forEach((u) => q.enqueue(u));
    expect(calls).toEqual(['a', 'b']);
    expect(q.running).toBe(2);

    resolvers.get('a')!();
    await flush();
    expect(calls).toEqual(['a', 'b', 'c']);
  });

  it('never asks twice for a URL that is waiting, running or done', async () => {
    const q = new PagePrefetchQueue(fetcher, 1);
    q.enqueue('a');
    q.enqueue('a');
    q.enqueue('b');
    q.enqueue('b');
    resolvers.get('a')!();
    await flush();
    q.enqueue('a');
    expect(calls).toEqual(['a', 'b']);
  });

  it('drops a waiting URL that became the current page', () => {
    const q = new PagePrefetchQueue(fetcher, 1);
    ['a', 'b', 'c'].forEach((u) => q.enqueue(u));
    q.drop('b');
    expect(q.waiting).toEqual(['c']);
  });

  it('starts nothing while held, and a hold ends by itself', () => {
    vi.useFakeTimers();
    try {
      const q = new PagePrefetchQueue(fetcher, 2, 1000);
      q.hold();
      q.enqueue('a');
      expect(calls).toEqual([]);
      vi.advanceTimersByTime(1000);
      expect(calls).toEqual(['a']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('aborts running prefetches and forgets everything on abortAll', async () => {
    const q = new PagePrefetchQueue(fetcher, 1);
    q.enqueue('a');
    q.enqueue('b');
    q.abortAll();
    expect(signals.get('a')!.aborted).toBe(true);
    expect(q.running).toBe(0);
    expect(q.waiting).toEqual([]);
    // A finished-but-aborted fetch does not start the forgotten one.
    resolvers.get('a')!();
    await flush();
    expect(calls).toEqual(['a']);
    // After a chapter change the same URL may be asked for again.
    q.enqueue('a');
    expect(calls).toEqual(['a', 'a']);
  });
});
