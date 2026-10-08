import { describe, expect, it } from 'vitest';

import { OrphanedTagRunEvent, TagRunNoticeSink } from './tag-run-notice-sink';

describe('TagRunNoticeSink', () => {
  it('holds one notice with its channel until it is cleared', () => {
    const sink = new TagRunNoticeSink();
    expect(sink.notice()).toBeNull();

    sink.raise('handofblood', { key: 'tags.errors.setChanged' });
    expect(sink.notice()).toEqual({
      channelName: 'handofblood',
      notice: { key: 'tags.errors.setChanged' },
    });

    sink.clear();
    expect(sink.notice()).toBeNull();
  });

  it('lets a later notice replace an earlier one', () => {
    const sink = new TagRunNoticeSink();
    sink.raise('handofblood', { key: 'tags.errors.setChanged' });
    sink.raise('handofblood', { key: 'tags.errors.reportFailed' });

    expect(sink.notice()?.notice.key).toBe('tags.errors.reportFailed');
  });

  it('passes feedback and completion on as events, without keeping them', () => {
    const sink = new TagRunNoticeSink();
    sink.feedback('handofblood', 'missed', {});
    const events: OrphanedTagRunEvent[] = [];
    sink.events.subscribe((event) => events.push(event));

    sink.feedback('handofblood', 'tags.feedback.removedNothing', { tag: 'Stronghold' });
    sink.completed('handofblood');

    expect(events).toEqual([
      {
        kind: 'feedback',
        channelName: 'handofblood',
        key: 'tags.feedback.removedNothing',
        params: { tag: 'Stronghold' },
      },
      { kind: 'completed', channelName: 'handofblood' },
    ]);
    expect(sink.notice()).toBeNull();
  });
});
