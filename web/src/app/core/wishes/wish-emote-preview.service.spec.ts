import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { SevenTvEmoteLookup, SevenTvEmoteLookupResult } from '../seven-tv/seven-tv-emote-lookup';
import { WishEmotePreviewService } from './wish-emote-preview.service';

const A = '01EZPHAJ3R000C438200A44F19';
const B = '01F5VW2TKR0003RCV2Z6JBHCST';
const C = '01F6MZGCNG000255K4X1K7NTHR';
/** The 24-character hex form of C (measured pair, `seven-tv-emote-lookup.ts`). */
const C_HEX = '60ae958e229664e8667aea38';
/** One consumer for the cases that are not about several of them. */
const ROW = 'row-1';

describe('WishEmotePreviewService', () => {
  let reads: { id: string; answer: Subject<SevenTvEmoteLookupResult> }[];
  let service: WishEmotePreviewService;

  beforeEach(() => {
    reads = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SevenTvEmoteLookup,
          useValue: {
            lookup: (id: string) => {
              const answer = new Subject<SevenTvEmoteLookupResult>();
              reads.push({ id, answer });
              return answer;
            },
          },
        },
      ],
    });
    service = TestBed.inject(WishEmotePreviewService);
  });

  function answer(index: number, result: SevenTvEmoteLookupResult): void {
    reads[index].answer.next(result);
    reads[index].answer.complete();
  }

  it('reads one emote at a time, in the order the rows asked', () => {
    service.request(A, ROW);
    service.request(B, ROW);

    expect(reads.map((read) => read.id)).toEqual([A]);
    answer(0, { id: A, kind: 'found', name: 'Pag', animated: true });

    expect(reads.map((read) => read.id)).toEqual([A, B]);
    expect(service.nameOf(A)).toBe('Pag');
    expect(service.nameOf(B)).toBeNull();
  });

  it('reads each emote once per session, under either id form', () => {
    service.request(C, ROW);
    service.request(C_HEX, ROW);
    answer(0, { id: C, kind: 'found', name: 'peepo', animated: false });
    service.request(C, ROW);
    service.request(C_HEX.toUpperCase(), ROW);

    expect(reads.map((read) => read.id)).toEqual([C]);
    expect(service.nameOf(C_HEX)).toBe('peepo');
  });

  it('does not read an unknown emote again', () => {
    service.request(A, ROW);
    answer(0, { id: A, kind: 'unknown' });
    service.request(A, ROW);

    expect(reads).toHaveLength(1);
    expect(service.previewOf(A)).toEqual({ kind: 'unknown' });
    expect(service.nameOf(A)).toBeNull();
  });

  it('reads a failed emote once more when it is asked for again, and keeps a second failure', () => {
    service.request(B, ROW);
    answer(0, { id: B, kind: 'unreachable' });
    expect(service.previewOf(B)).toEqual({ kind: 'unavailable' });

    service.request(B, ROW);
    expect(reads.map((read) => read.id)).toEqual([B, B]);
    // While the second read is on the way the emote counts as not read yet.
    expect(service.previewOf(B)).toBeNull();
    answer(1, { id: B, kind: 'unreachable' });

    service.request(B, ROW);
    expect(reads).toHaveLength(2);
    expect(service.previewOf(B)).toEqual({ kind: 'unavailable' });
  });

  it('shows the emote once the second read after a failure finds it', () => {
    service.request(C, ROW);
    answer(0, { id: C, kind: 'unreachable' });
    service.request(C_HEX, ROW);
    answer(1, { id: C, kind: 'found', name: 'peepo', animated: false });

    expect(service.nameOf(C)).toBe('peepo');
    service.request(C, ROW);
    expect(reads).toHaveLength(2);
  });

  it('skips a row that left the view before its turn', () => {
    service.request(A, ROW);
    service.request(B, ROW);
    service.request(C, ROW);
    service.withdraw(B, ROW);
    answer(0, { id: A, kind: 'found', name: 'Pag', animated: null });

    expect(reads.map((read) => read.id)).toEqual([A, C]);
  });

  it('keeps a waiting emote while another consumer still needs it', () => {
    const dialog = {};
    service.request(A, ROW);
    service.request(B, 'row-2');
    service.request(B, 'row-3');
    service.request(B, dialog);
    service.withdraw(B, 'row-2');
    service.withdraw(B, 'row-3');
    answer(0, { id: A, kind: 'found', name: 'Pag', animated: null });

    // The open dialog still waits for B: it is read.
    expect(reads.map((read) => read.id)).toEqual([A, B]);
  });

  it('drops a waiting emote once its last consumer has withdrawn, under either id form', () => {
    const dialog = {};
    service.request(A, ROW);
    service.request(C, 'row-2');
    service.request(C_HEX, dialog);
    service.withdraw(C, 'row-2');
    service.withdraw(C_HEX, dialog);
    answer(0, { id: A, kind: 'found', name: 'Pag', animated: null });

    expect(reads.map((read) => read.id)).toEqual([A]);
  });

  it('stops for the session after a throttle and drops what was waiting', () => {
    service.request(A, ROW);
    service.request(B, ROW);
    answer(0, { id: A, kind: 'throttled' });

    expect(service.stopped()).toBe(true);
    expect(reads).toHaveLength(1);
    service.request(C, ROW);
    service.request(A, ROW);
    expect(reads).toHaveLength(1);
    expect(service.previewOf(A)).toBeNull();
  });

  it('never reads for a wish without an emote', () => {
    service.request('', ROW);

    expect(reads).toEqual([]);
    expect(service.previewOf('')).toBeNull();
  });
});
