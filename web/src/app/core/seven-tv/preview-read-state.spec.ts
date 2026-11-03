import { previewOmittedCount, previewReadState } from './preview-read-state';

/*
 * #457, AK 6.
 * protects: an older Api response without `readState` fails closed (derived from `truncated`), and
 *   only an answer that positively says "gap" unlocks anything.
 * fails_when: a missing field reads as gap or complete while `truncated` is true, or a
 *   contradictory answer reads as gap or complete.
 * no_claim: the Api (the backend's own tests pin what it sends).
 */
describe('previewReadState', () => {
  it('returns the readState the Api sent', () => {
    expect(previewReadState({ truncated: false, readState: 'complete', omittedCount: 0 })).toBe(
      'complete',
    );
    expect(previewReadState({ truncated: true, readState: 'gap', omittedCount: 2 })).toBe('gap');
    expect(previewReadState({ truncated: true, readState: 'inconsistent', omittedCount: 0 })).toBe(
      'inconsistent',
    );
  });

  it('derives the state from truncated for a response without readState (an older Api)', () => {
    expect(previewReadState({ truncated: true })).toBe('inconsistent');
    expect(previewReadState({ truncated: false })).toBe('complete');
  });

  it('reads every answer that contradicts the wire contract as inconsistent', () => {
    // A "complete" list the same answer calls truncated.
    expect(previewReadState({ truncated: true, readState: 'complete' })).toBe('inconsistent');
    // A gap that is not truncated, or names nothing omitted.
    expect(previewReadState({ truncated: false, readState: 'gap', omittedCount: 2 })).toBe(
      'inconsistent',
    );
    expect(previewReadState({ truncated: true, readState: 'gap', omittedCount: 0 })).toBe(
      'inconsistent',
    );
    expect(previewReadState({ truncated: true, readState: 'gap' })).toBe('inconsistent');
    // A value this frontend does not know.
    expect(
      previewReadState({
        truncated: false,
        readState: 'partial' as unknown as 'complete',
      }),
    ).toBe('inconsistent');
  });
});

describe('previewOmittedCount', () => {
  it('is the omitted count for a gap read and 0 for every other read', () => {
    expect(previewOmittedCount({ truncated: true, readState: 'gap', omittedCount: 2 })).toBe(2);
    expect(
      previewOmittedCount({ truncated: true, readState: 'inconsistent', omittedCount: 3 }),
    ).toBe(0);
    expect(previewOmittedCount({ truncated: true })).toBe(0);
    expect(previewOmittedCount({ truncated: false, readState: 'complete' })).toBe(0);
  });
});
