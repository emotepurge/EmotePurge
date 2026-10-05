import { Injectable, signal } from '@angular/core';
import { Observable, Subject } from 'rxjs';

/** Why a tag flow stopped, shown as an error banner until the next attempt. `leadKey` is an
 *  optional first sentence (a confirmed clear-out that did not start says "Nothing was deleted.");
 *  `retry`, when present, is what the banner's "Try again" button does — a report resend with the
 *  same operation id, or a fresh start of the flow. */
export interface TagRunNotice {
  leadKey?: string;
  key: string;
  params?: Record<string, unknown>;
  retry?: () => void;
}

/** A notice whose flow outlived its `TagRunActions`, with the channel it was raised for. */
export interface OrphanedTagRunNotice {
  channelName: string;
  notice: TagRunNotice;
}

/** What else such a flow still produces: a transient acknowledgement, or a report without a run
 *  that succeeded (the page reloads the tag's numbers then). */
export type OrphanedTagRunEvent =
  | { kind: 'feedback'; channelName: string; key: string; params: Record<string, unknown> }
  | { kind: 'completed'; channelName: string };

/**
 * The page-level surface for whatever a tag flow still produces after its host is gone (#201 T-C).
 *
 * `TagRunActions` hosts the flows, but both of its dialogs, an import hook and an in-flight report
 * outlive it: a live set switch on the usage page, or a tag-list reload with a new set on the tags
 * page, tears the host down behind them. The set guard then aborts correctly — but a notice written
 * into the destroyed component's banner is never seen, and a confirmed run that silently does
 * nothing reads as success. So the flows hand such notices here instead (`raiseNotice`,
 * `sendTagReport`), and both pages render this one root-provided notice in their run status
 * region, the way they render `SevenTvRunArbiter.refusedStart()`.
 *
 * The notice stays until it is dismissed or a new tag run starts, like the banner it stands in for.
 * A retry survives only for a report without a run (resending the same operation needs no host); a
 * retry that would restart the whole flow is dropped with the host it belonged to.
 *
 * Feedback and "completed" are events, not state: a page that is not mounted when they happen has
 * no numbers to reload and no status line to show — they are dropped with intent then.
 */
@Injectable({ providedIn: 'root' })
export class TagRunNoticeSink {
  private readonly noticeState = signal<OrphanedTagRunNotice | null>(null);
  private readonly eventsSubject = new Subject<OrphanedTagRunEvent>();

  readonly notice = this.noticeState.asReadonly();
  readonly events: Observable<OrphanedTagRunEvent> = this.eventsSubject.asObservable();

  raise(channelName: string, notice: TagRunNotice): void {
    this.noticeState.set({ channelName, notice });
  }

  clear(): void {
    this.noticeState.set(null);
  }

  feedback(channelName: string, key: string, params: Record<string, unknown>): void {
    this.eventsSubject.next({ kind: 'feedback', channelName, key, params });
  }

  completed(channelName: string): void {
    this.eventsSubject.next({ kind: 'completed', channelName });
  }
}
