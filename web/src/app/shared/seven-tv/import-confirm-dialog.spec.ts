import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { ImportTargetLoadState } from '../../core/emotes/import-target-loader';
import { LanguageService } from '../../core/i18n/language.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { TransferPlan } from '../../core/seven-tv/transfer-plan';
import {
  ImportConfirmDialog,
  ImportConfirmDialogData,
  ImportConfirmOutcome,
} from './import-confirm-dialog';

// Only the keys this dialog translates — not the full app translation file. Texts are the real
// German ones, so an assertion reads as the sentence the user gets rather than as a key.
const DE_TRANSLATIONS = {
  common: {
    cancel: 'Abbrechen',
    loading: 'Lädt …',
  },
  // Same key namespace the audit view's `renderDetail` reads (`leaderboard.model.ts`,
  // `LEADERBOARD_SORT_LABEL_KEYS`) — the dialog translates a leaderboard origin's sort through it
  // too, so the two surfaces say exactly the same thing (E2).
  audit: {
    details: {
      leaderboardSort: {
        TRENDING_DAILY: '7TV Trend heute',
        TOP_ALL_TIME: '7TV Top insgesamt',
      },
    },
  },
  massDelete: {
    sharedSetWarningTitle:
      'Achtung: Das aktive Emote-Set gehört möglicherweise nicht (nur) diesem Channel.',
    notOwnSet: 'Das aktive Set gehört nicht dem eigenen 7TV-Account dieses Channels.',
    knownAffected: 'Bei uns bekannt betroffen: {{ list }}',
    moderatedAffected: 'Von dir moderiert, ebenfalls betroffen: {{ list }}',
    ownershipCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob dieses Set wirklich diesem Channel gehört.',
  },
  restore: {
    capacityProjection: 'Das Set hätte danach {{ projected }} von {{ capacity }} Slots belegt.',
    capacityWarning: 'Das überschreitet die Kapazität — 7TV wird überzählige Emotes ablehnen.',
  },
  import: {
    confirm: {
      title: {
        one: '{{ count }} Emote nach {{ channel }} kopieren?',
        other: '{{ count }} Emotes nach {{ channel }} kopieren?',
      },
      titleSet: {
        one: "{{ count }} Emote in Set ‚{{ setName }}' kopieren?",
        other: "{{ count }} Emotes in Set ‚{{ setName }}' kopieren?",
      },
      titleAlign: {
        one: '{{ count }} Namen im Zielset angleichen?',
        other: '{{ count }} Namen im Zielset angleichen?',
      },
      untrackedTarget:
        'EmotePurge trackt diesen Account nicht — 7TV lässt das Kopieren nur zu, wenn du dort Editor bist.',
      ownershipCheckUnavailable:
        'Wir konnten gerade nicht prüfen, ob dieses Set wirklich diesem Channel gehört — bitte vor dem Kopieren selbst kontrollieren.',
      originChannel: 'Aus Kanal {{ channel }}',
      originFile: 'Aus Datei {{ fileName }}',
      originFileDetails: 'Export aus {{ channel }}, {{ date }}',
      originLeaderboard: 'Aus 7TVs Bestenliste: {{ sort }}',
      dateUnknown: 'Datum unbekannt',
      channelUnknown: 'Kanal unbekannt',
      target: 'Ziel: {{ channel }} · Set {{ setName }}',
      targetUntracked: 'Ziel: Set {{ setName }} von {{ owner }}',
      loadingHint: 'Zieldaten werden geladen…',
      noTargetSet: 'Der Zielkanal hat noch kein aktives 7TV-Set.',
      loadFailed: 'Die Daten des Zielkanals konnten nicht geladen werden.',
      retry: 'Erneut laden',
      staleHint: 'Der letzte Abgleich des Zielkanals ist fehlgeschlagen.',
      alreadyPresent: {
        one: '{{ count }} Emote ist bereits im Zielset und wird übersprungen.',
        other: '{{ count }} Emotes sind bereits im Zielset und werden übersprungen.',
      },
      nameCollisions: {
        one: '{{ count }} Name ist im Zielset schon vergeben, wird nicht übertragen:',
        other: '{{ count }} Namen sind im Zielset schon vergeben, werden nicht übertragen:',
      },
      aliasMismatches: {
        one: '{{ count }} ist vorhanden, heißt dort aber anders:',
        other: '{{ count }} sind vorhanden, heißen dort aber anders:',
      },
      aliasMismatchRow: '{{ source }} → {{ target }}',
      invalidNames: {
        one: '{{ count }} Name enthält Zeichen, die 7TV nicht anlegen kann:',
        other: '{{ count }} Namen enthalten Zeichen, die 7TV nicht anlegen kann:',
      },
      discardedRows: {
        one: '{{ count }} ungültige Zeile in der Quelle verworfen.',
        other: '{{ count }} ungültige Zeilen in der Quelle verworfen.',
      },
      duplicatesCollapsed: {
        one: '{{ count }} doppelte Zeile in der Quelle zusammengefasst.',
        other: '{{ count }} doppelte Zeilen in der Quelle zusammengefasst.',
      },
      nothingToAdd: {
        one: 'Das einzige Emote ist bereits im Zielset.',
        other: 'Alle {{ count }} Emotes sind bereits im Zielset.',
      },
      nothingToAddBlocked: {
        one: 'Das einzige Emote kann nicht hinzugefügt werden — bereits vorhanden, Namenskollision oder anderer Alias.',
        other:
          'Keines der {{ count }} Emotes kann hinzugefügt werden — bereits vorhanden, Namenskollision oder anderer Alias.',
      },
      sameChannelFile: 'Diese Liste stammt aus diesem Kanal.',
      runNotice: 'Das Hinzufügen läuft danach automatisch nacheinander.',
      runNoticeRenameOnly: 'Das Umbenennen läuft danach automatisch nacheinander.',
      execute: 'Kopieren',
      executeRenameOnly: 'Übertragen',
      removals: {
        one: '{{ count }} Emote wird aus dem Zielset entfernt.',
        other: '{{ count }} Emotes werden aus dem Zielset entfernt.',
      },
      renames: {
        one: '{{ count }} Eintrag im Zielset wird umbenannt.',
        other: '{{ count }} Einträge im Zielset werden umbenannt.',
      },
      saveRecovery: 'Rückweg sichern',
      start: 'Starten',
      verifying: 'Das Zielset wird gerade live geprüft…',
      targetDrifted: 'Das Zielset hat sich seit der Vorschau geändert: {{ rows }}.',
      targetReadFailed: 'Das Zielset ließ sich gerade nicht vollständig lesen.',
      saveRecoveryFailed: 'Der Browser hat den Download der Rückweg-Datei verweigert.',
      reloadTarget: 'Ziel neu laden',
      decisionsDropped:
        'Nach dem Neuladen passt die Entscheidung für diese Zeilen nicht mehr: {{ rows }}.',
    },
    resolve: {
      open: 'Auflösen',
      openCollisions: 'Namenskollisionen auflösen',
      openMismatches: 'Abweichende Namen auflösen',
      resolvedCount: 'Davon aufgelöst: {{ count }}',
      titleCollisions: {
        one: '{{ count }} Namenskollision auflösen',
        other: '{{ count }} Namenskollisionen auflösen',
      },
      titleMismatches: {
        one: '{{ count }} abweichenden Namen auflösen',
        other: '{{ count }} abweichende Namen auflösen',
      },
      hintCollisions: 'Der Name ist im Zielset schon vergeben.',
      hintMismatches: 'Das Emote ist im Zielset schon vorhanden, heißt dort aber anders.',
      listLabelCollisions: 'Namenskollisionen',
      listLabelMismatches: 'Abweichende Namen',
      rowLabel: '{{ source }}, im Ziel: {{ target }}',
      targetGone: 'nicht mehr im Zielset',
      aliaslessEntry: '+ ein Eintrag ohne Namen',
      actionGroupLabel: 'Aktion für {{ sourceName }}',
      action: {
        skip: 'Überspringen',
        renameSource: 'Umbenennen',
        replaceTarget: 'Ziel ersetzen',
        adoptSourceName: 'Namen übernehmen',
      },
      reloadTargetFirst: 'Ziel hat sich geändert — erst neu laden',
      adoptBlocked: {
        nameTaken: 'Name im Zielset vergeben',
        duplicateTarget: 'im Ziel doppelt vorhanden',
      },
      renameLabel: 'Neuer Name',
      fieldError: {
        invalid: 'Diesen Namen nimmt 7TV nicht an.',
        taken: 'Dieser Name ist im Zielset schon vergeben.',
        duplicate: 'Diesen Namen erzeugt auch eine andere Zeile.',
      },
      violation: {
        duplicateGeneratedAlias: 'Mehrere Zeilen erzeugen denselben Namen: {{ rows }}.',
        aliasHeldByTarget: 'Name im Zielset schon vergeben: {{ rows }}.',
        invalidTypedAlias: 'Name, den 7TV nicht annimmt: {{ rows }}.',
        duplicateReplaceTarget: 'Dasselbe Ziel wird mehrfach ersetzt: {{ rows }}.',
        targetTouchedByReplaceAndAdopt: 'Dasselbe Ziel wird ersetzt und umbenannt: {{ rows }}.',
        adoptBlocked: 'Namen übernehmen ist hier nicht möglich: {{ rows }}.',
      },
      back: 'Zurück',
      apply: 'Übernehmen',
    },
  },
};

/** The few English texts the language-switch case reads — enough to see the open step re-translate. */
const EN_TRANSLATIONS = {
  import: {
    resolve: {
      titleCollisions: {
        one: 'Resolve {{ count }} name collision',
        other: 'Resolve {{ count }} name collisions',
      },
      actionGroupLabel: 'Action for {{ sourceName }}',
      action: {
        skip: 'Skip',
        renameSource: 'Rename',
        replaceTarget: 'Replace target',
        adoptSourceName: 'Adopt name',
      },
      back: 'Back',
      apply: 'Apply',
    },
  },
};

const CANCEL = 'Abbrechen';
const EXECUTE = 'Kopieren';
const RETRY = 'Erneut laden';

/** The check ran and found nothing worth flagging — the quiet case. */
const OWN_SET: EmoteSetWarning = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

/** What `loadImportTarget` falls back to when only `getSetWarning` failed: not verified, and
 *  deliberately neither a clean bill of health nor an alarm. */
const UNAVAILABLE_WARNING: EmoteSetWarning = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

type ReadyTarget = Extract<ImportTargetLoadState, { status: 'ready' }>;

/** jsdom has no ResizeObserver, and the resolution step's CDK viewport reads it during init. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function row(sevenTvEmoteId: string, name: string): ImportRow {
  return { sevenTvEmoteId, name, imageUrl: null };
}

/** The plan of a dialog nobody resolved anything in: one `add` row per row, under its own name. */
function addPlan(rows: ImportRow[]): TransferPlan {
  return { rows: rows.map((each) => ({ action: 'add', source: each, alias: each.name })) };
}

function channelSource(rows: ImportRow[], overrides: Partial<ImportSource> = {}): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'sourcechannel' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
    ...overrides,
  };
}

/** The third source (spec §7): a channel EmotePurge does not track, read live from 7TV. Rows carry
 *  the *alias* of the source set, which is what makes a name collision in the target likely. */
function foreignChannelSource(rows: ImportRow[], channelName = 'handofblood'): ImportSource {
  return {
    origin: { kind: 'seventv-channel', channelName },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

/** The fourth source (#148): 7TV's network-wide leaderboard. No channel at all — the sort itself is
 *  the origin (spec E2/E8). */
function leaderboardSource(
  rows: ImportRow[],
  sortBy: 'TRENDING_DAILY' | 'TOP_ALL_TIME' = 'TRENDING_DAILY',
): ImportSource {
  return {
    origin: { kind: 'seventv-leaderboard', sortBy },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

function fileSource(
  rows: ImportRow[],
  origin: Partial<Extract<ImportSource['origin'], { kind: 'file' }>> = {},
  overrides: Partial<ImportSource> = {},
): ImportSource {
  return {
    origin: {
      kind: 'file',
      fileName: 'emotes.json',
      exportedAt: '2026-08-01T12:00:00Z',
      channelName: 'sourcechannel',
      envelopeKind: 'emote-list',
      ...origin,
    },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
    ...overrides,
  };
}

function readyTarget(overrides: Partial<ReadyTarget> = {}): ImportTargetLoadState {
  return {
    status: 'ready',
    setId: 'set-1',
    // `null` by default, matching the "today" (active-set) path, which never has one — a test
    // about the header's setName sets this explicitly (spec 8.6, AK 39).
    setName: null,
    occupiedSlots: 10,
    capacity: 1000,
    syncFailureReason: null,
    emotes: [],
    warning: OWN_SET,
    ...overrides,
  };
}

/**
 * The order the markers actually appear in the rendered dialog. Sorting by position rather than
 * asserting on positions keeps the failure readable — the expectation is the contract's own list.
 */
function inRenderedOrder(text: string, markers: readonly string[]): string[] {
  for (const marker of markers) {
    if (!text.includes(marker)) {
      throw new Error(`marker not rendered: ${marker}`);
    }
  }
  return [...markers].sort((a, b) => text.indexOf(a) - text.indexOf(b));
}

interface RenderOptions {
  source?: ImportSource;
  targetChannelName?: string | null;
  targetOwnerDisplayName?: string | null;
  /** Defaults to `true` — every existing test in this file predates findings 1/3 and exercises the
   *  active-set target, whose title/dock behaviour must stay exactly as it was. */
  targetIsActiveSet?: boolean;
  /** Only meaningful together with `targetIsActiveSet: false` — defaults to `null`, matching an
   *  active target's title, which never names the set. */
  titleSetName?: string | null;
  target?: ImportTargetLoadState;
  runBlocked?: boolean;
}

interface Harness {
  fixture: ComponentFixture<ImportConfirmDialog>;
  /** The flow's live view of the target — the dialog opens on `loading` and fills in. */
  target: WritableSignal<ImportTargetLoadState>;
  runBlocked: WritableSignal<boolean>;
  detect(): void;
  text(): string;
  title(): string;
  button(label: string): HTMLButtonElement;
  hasButton(label: string): boolean;
  element(id: string): HTMLElement | null;
}

describe('ImportConfirmDialog', () => {
  let dialogData: ImportConfirmDialogData;
  let closed: (ImportConfirmOutcome | undefined)[];
  let retryCalls: number;
  let panelClasses: Set<string>;

  beforeEach(async () => {
    closed = [];
    retryCalls = 0;
    panelClasses = new Set();
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    await TestBed.configureTestingModule({
      imports: [
        ImportConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS, en: EN_TRANSLATIONS },
          // As in app.config.ts: a language switch re-renders what is already on screen.
          translocoConfig: {
            availableLangs: ['de', 'en'],
            defaultLang: 'de',
            reRenderOnLangChange: true,
          },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Resolved when the component is created, so a test may shape the data first.
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: {
            close: (result?: ImportConfirmOutcome) => closed.push(result),
            // The pane the dialog widens for its second step.
            overlayRef: {
              addPanelClass: (name: string) => panelClasses.add(name),
              removePanelClass: (name: string) => panelClasses.delete(name),
            },
          },
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  afterEach(() => {
    // No test may leave a live read of the target set unanswered — or fire one it did not expect.
    TestBed.inject(HttpTestingController).verify();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /**
   * One dialog per test: `DIALOG_DATA` is resolved once per injector, so a second `render()` in the
   * same test would silently hand the first test data to the second component. Everything that has
   * to change while the dialog is up changes through the two signals in the returned harness —
   * which is also how the flow itself feeds this dialog.
   */
  function render(options: RenderOptions = {}): Harness {
    const target = signal<ImportTargetLoadState>(options.target ?? readyTarget());
    const runBlocked = signal(options.runBlocked ?? false);

    dialogData = {
      source: options.source ?? channelSource([row('new-1', 'Kappa')]),
      targetChannelName:
        options.targetChannelName === undefined ? 'targetchannel' : options.targetChannelName,
      targetOwnerDisplayName: options.targetOwnerDisplayName ?? null,
      targetIsActiveSet: options.targetIsActiveSet ?? true,
      titleSetName: options.titleSetName ?? null,
      target,
      retry: () => {
        retryCalls += 1;
      },
      runBlocked,
      httpClient: TestBed.inject(HttpClient),
    };

    const fixture = TestBed.createComponent(ImportConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      target,
      runBlocked,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      title: () => host.querySelector('#app-dialog-title')?.textContent?.trim() ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      hasButton: (label) => buttons().some((button) => button.textContent?.trim() === label),
      element: (id) => host.querySelector<HTMLElement>(`#${id}`),
    };
  }

  describe('executor lock', () => {
    it('locks the executor while the target data is still loading, and says why next to it', () => {
      const dialog = render({ target: { status: 'loading' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      // The one lock reason without a banner: while loading, the target block is a skeleton, so the
      // reason lives in the action row instead — and the button has to point at it.
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-loading-hint');
      expect(dialog.element('import-confirm-loading-hint')?.textContent).toContain(
        'Zieldaten werden geladen…',
      );
      // Cancelling is never blocked.
      expect(dialog.button(CANCEL).disabled).toBe(false);
    });

    it('locks it after a failed target load and offers the retry the flow owns', () => {
      const dialog = render({ target: { status: 'failed' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-load-failed');
      expect(dialog.element('import-confirm-load-failed')?.textContent).toContain(
        'Die Daten des Zielkanals konnten nicht geladen werden.',
      );

      dialog.button(RETRY).click();
      // The dialog only asks; the flow reloads and pushes a new state into the signal.
      expect(retryCalls).toBe(1);
    });

    it('locks it when the target channel has no active set — a different reason, not "failed"', () => {
      const dialog = render({ target: { status: 'no-set' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-no-target-set');
      expect(dialog.element('import-confirm-no-target-set')?.textContent).toContain(
        'Der Zielkanal hat noch kein aktives 7TV-Set.',
      );
      // No retry here: reloading would answer the same, the target has to gain a set first.
      expect(dialog.hasButton(RETRY)).toBe(false);
      expect(dialog.element('import-confirm-load-failed')).toBeNull();
    });

    it('locks it when every offered row is already in the target set', () => {
      const dialog = render({
        source: channelSource([row('existing-1', 'PogU'), row('existing-2', 'Kappa')]),
        target: readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-2',
              name: 'Kappa',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-nothing-to-add');
      // The banner counts the offered rows, not the (empty) rest — "all 2 of them" is the statement.
      expect(dialog.element('import-confirm-nothing-to-add')?.textContent).toContain(
        'Alle 2 Emotes sind bereits im Zielset.',
      );
    });

    it('says why nothing is left to add when name collisions took every row — not the "already present" wording', () => {
      // Codex Sol P2: before the fix this banner always used the `nothingToAdd` key regardless of
      // reason, so a run blocked entirely by collisions still claimed every emote was "already in
      // the target set" — false, and contradicting the collision group shown just above it.
      const dialog = render({
        source: channelSource([row('new-1', 'Collides'), row('new-2', 'AlsoCollides')]),
        target: readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'Collides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-2',
              name: 'AlsoCollides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-nothing-to-add');
      const banner = dialog.element('import-confirm-nothing-to-add')?.textContent ?? '';
      expect(banner).toContain(
        'Keines der 2 Emotes kann hinzugefügt werden — bereits vorhanden, Namenskollision oder anderer Alias.',
      );
      expect(banner).not.toContain('Alle 2 Emotes sind bereits im Zielset.');

      dialog.button(EXECUTE).click();
      expect(closed).toEqual([]);
    });

    it('says why nothing is left to add on a mixed reason — some present, some collided', () => {
      const dialog = render({
        source: channelSource([row('existing-1', 'PogU'), row('new-1', 'Collides')]),
        target: readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-2',
              name: 'Collides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      const banner = dialog.element('import-confirm-nothing-to-add')?.textContent ?? '';
      expect(banner).toContain(
        'Keines der 2 Emotes kann hinzugefügt werden — bereits vorhanden, Namenskollision oder anderer Alias.',
      );
      expect(banner).not.toContain('Alle 2 Emotes sind bereits im Zielset.');
    });

    it('keeps the plain "already present" wording when that is the only reason', () => {
      // Regression: the mixed/collision wording above must not swallow the existing, more specific
      // case — every row present under the same alias still gets the original sentence.
      const dialog = render({
        source: channelSource([row('existing-1', 'PogU')]),
        target: readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      const banner = dialog.element('import-confirm-nothing-to-add')?.textContent ?? '';
      expect(banner).toContain('Das einzige Emote ist bereits im Zielset.');
    });

    it('releases it once the target is ready and something is left to add', () => {
      const dialog = render({ target: { status: 'loading' } });
      expect(dialog.button(EXECUTE).disabled).toBe(true);

      dialog.target.set(readyTarget());
      dialog.detect();

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(false);
      // Nothing to explain any more, so the button describes itself.
      expect(execute.getAttribute('aria-describedby')).toBeNull();
    });

    it('locks it silently while another 7TV run is going — the running progress is the reason', () => {
      const dialog = render({ runBlocked: true });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      // The second lock source is independent of `blockReason` and deliberately has no text of its
      // own: the dock's progress next to this dialog already says what is happening (§4.2).
      expect(execute.getAttribute('aria-describedby')).toBeNull();

      dialog.runBlocked.set(false);
      dialog.detect();
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });

    it('keeps the run from starting while it is locked, not just greyed out', () => {
      const dialog = render({ runBlocked: true });

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([]);
    });
  });

  describe('outcome', () => {
    it('closes with the target set and only the rows that would actually be added', () => {
      const dialog = render({
        source: channelSource([
          row('existing-1', 'PogU'),
          row('new-1', 'Kappa'),
          // A name collision is excluded outright since spec 2026-09-20 — 7TV would reject it
          // every time, so it never even reaches the run (AK 40: the run's own `failed` count for
          // this row is 0, not "1 rejected").
          row('new-2', 'Collides'),
        ]),
        target: readyTarget({
          setId: 'set-42',
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-9',
              name: 'Collides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([
        {
          targetSetId: 'set-42',
          targetSetName: 'set-42',
          plan: addPlan([row('new-1', 'Kappa')]),
        },
      ]);
    });

    it('closes empty-handed on cancel', () => {
      const dialog = render();

      dialog.button(CANCEL).click();

      expect(closed).toEqual([undefined]);
    });
  });

  describe('title', () => {
    it('counts the offered rows until the target answers, then only what is left to add', () => {
      const dialog = render({
        source: channelSource([
          row('existing-1', 'PogU'),
          row('existing-2', 'Kappa'),
          row('new-1', 'Pepega'),
        ]),
        targetChannelName: 'targetchannel',
        target: { status: 'loading' },
      });

      // The honest upper bound while nothing is known about the target — and the number the user
      // just picked, rather than a headless dialog.
      expect(dialog.title()).toBe('3 Emotes nach targetchannel kopieren?');

      dialog.target.set(
        readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-2',
              name: 'Kappa',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      );
      dialog.detect();

      // Settles to the rest list, and picks the singular sibling key for it.
      expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
    });

    // Finding 1 (Live-Verifikation K2 2026-09-21): "nach {channel}" claims the channel's active
    // set — wrong whenever the target is not that active set (a non-active tracked pick, or any
    // untracked one), since finding 3 is the direct consequence of that claim being false.
    it('names the set instead of the channel for a non-active tracked target', () => {
      const dialog = render({
        targetChannelName: 'targetchannel',
        targetIsActiveSet: false,
        titleSetName: 'Wegwerf',
      });

      expect(dialog.title()).toBe("1 Emote in Set ‚Wegwerf' kopieren?");
    });

    it('names the set instead of the owner for an untracked target', () => {
      const dialog = render({
        targetChannelName: null,
        targetOwnerDisplayName: 'Stranger',
        targetIsActiveSet: false,
        titleSetName: 'Wegwerf',
      });

      expect(dialog.title()).toBe("1 Emote in Set ‚Wegwerf' kopieren?");
    });

    it('keeps the "nach {channel}" wording for the active-set target — the one-click path stays', () => {
      const dialog = render({ targetChannelName: 'targetchannel', targetIsActiveSet: true });

      expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
    });
  });

  describe('set-ownership findings', () => {
    it('stays silent when the check ran and found an unshared own set', () => {
      const dialog = render({ target: readyTarget({ warning: OWN_SET }) });

      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('raises the shared-set warning when the set is not the channel own', () => {
      const dialog = render({
        target: readyTarget({ warning: { ...OWN_SET, isOwnSet: false } }),
      });

      expect(dialog.text()).toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
      expect(dialog.text()).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('raises it for other channels sharing the set even when the set is the channel own', () => {
      const dialog = render({
        target: readyTarget({
          warning: {
            ...OWN_SET,
            otherTrackedChannelsSharingSet: ['tracked1', 'tracked2'],
            otherModeratedChannelsSharingSet: ['modded1'],
          },
        }),
      });

      expect(dialog.text()).toContain('Bei uns bekannt betroffen: tracked1, tracked2');
      expect(dialog.text()).toContain('Von dir moderiert, ebenfalls betroffen: modded1');
      // The set is the channel's own, so that one line stays out of the banner.
      expect(dialog.text()).not.toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
    });

    it('downgrades to "could not check" for a TRACKED target whose check itself failed — not a confirmed finding', () => {
      const dialog = render({
        targetChannelName: 'targetchannel',
        target: readyTarget({ warning: UNAVAILABLE_WARNING }),
      });

      // `isOwnSet: false` is part of the fallback shape and must not be read as evidence: an
      // unavailable check is amber ("unknown"), never the red "this set is foreign".
      expect(dialog.text()).toContain('Wir konnten gerade nicht prüfen');
      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).not.toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
      // Finding 5: this branch keeps the delete flow's amber warning styling, worded for a copy
      // rather than a deletion — never the untracked branch's neutral hint below.
      expect(dialog.text()).not.toContain('EmotePurge trackt diesen Account nicht');
      // And it blocks nothing.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });

    // Finding 5: an UNTRACKED target's warning is `UNAVAILABLE_WARNING` by contract, always — there
    // is no channel for `EmoteSetOwnershipService` to check at all (spec 8.6), so this is never a
    // check that "failed"; the delete flow's alarm text (`massDelete.ownershipCheckUnavailable`)
    // misdescribed it as one. A short, neutral hint instead, and no warning-styled banner.
    it('shows a neutral hint instead of an alarm for an UNTRACKED target — the ownership check never applies there', () => {
      const dialog = render({
        targetChannelName: null,
        targetOwnerDisplayName: 'SomeEditor',
        target: readyTarget({ warning: UNAVAILABLE_WARNING }),
      });

      expect(dialog.text()).toContain('EmotePurge trackt diesen Account nicht');
      expect(dialog.text()).not.toContain('Wir konnten gerade nicht prüfen');
      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });
  });

  describe('slot projection', () => {
    it('warns when the copy would push the target set past its capacity', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa'), row('new-2', 'Pepega')]),
        target: readyTarget({ occupiedSlots: 999, capacity: 1000 }),
      });

      expect(dialog.text()).toContain('Das Set hätte danach 1001 von 1000 Slots belegt.');
      expect(dialog.text()).toContain('Das überschreitet die Kapazität');
      // Informational, not a lock: 7TV decides which of the overflowing adds it rejects.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });

    it('states the projection quietly when it still fits', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa')]),
        target: readyTarget({ occupiedSlots: 999, capacity: 1000 }),
      });

      expect(dialog.text()).toContain('Das Set hätte danach 1000 von 1000 Slots belegt.');
      expect(dialog.text()).not.toContain('Das überschreitet die Kapazität');
    });

    it('says nothing about slots when 7TV reports no usable capacity', () => {
      const dialog = render({ target: readyTarget({ capacity: null }) });

      expect(dialog.text()).not.toContain('Slots belegt');
    });
  });

  describe('origin', () => {
    it('names the file with its export channel and date, formatted in the active language', () => {
      const lang = TestBed.inject(LanguageService).lang;
      lang.set('de');

      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], {
          fileName: 'emotes-2026.json',
          channelName: 'sourcechannel',
          exportedAt: '2026-08-01T12:00:00Z',
        }),
      });

      expect(dialog.text()).toContain('Aus Datei emotes-2026.json');
      expect(dialog.text()).toContain('Export aus sourcechannel, 1.8.2026');

      // Only the date follows the language signal here — the labels come from the (German-only)
      // test dictionary, and re-formatting on a language switch is the point of reading `lang()`.
      lang.set('en');
      dialog.detect();
      expect(dialog.text()).toContain('Export aus sourcechannel, 8/1/2026');
    });

    it('falls back to "unknown" for a file without a channel or with an unreadable date', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], {
          channelName: null,
          exportedAt: 'not-a-date',
        }),
      });

      expect(dialog.text()).toContain('Export aus Kanal unbekannt, Datum unbekannt');
    });

    it('flags a file that was exported from the target channel itself, comparing normalized names', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], { channelName: 'HandOfBlood' }),
        targetChannelName: 'handofblood',
      });

      expect(dialog.text()).toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('does not flag a channel origin, even when it is the target channel', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa')], {
          origin: { kind: 'channel', channelName: 'targetchannel' },
        }),
        targetChannelName: 'targetchannel',
      });

      // A channel origin can only be a *different* channel's grid in this flow, and the line is
      // about a downloaded file having travelled in a circle.
      expect(dialog.text()).toContain('Aus Kanal targetchannel');
      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('names a foreign channel as a channel origin, not as a file', () => {
      // The template used to ask `origin.kind === 'channel'` and fell into the *file* branch for
      // everything else — this origin would have been announced as a file and then read a fileName
      // it does not have (spec F6).
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa')]),
      });

      expect(dialog.text()).toContain('Aus Kanal handofblood');
      expect(dialog.text()).not.toContain('Aus Datei');
      expect(dialog.text()).not.toContain('Export aus');
    });

    it('does not flag a foreign channel origin as a list that came from this channel', () => {
      // The "came back to where it started" line is about a downloaded file; the target picker
      // excludes the source channel, so this pairing cannot even be produced by the flow.
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa')], 'targetchannel'),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('does not flag a file from another channel', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], { channelName: 'someoneelse' }),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('names the sort as the origin for a leaderboard pick — no channel, no file (spec E2)', () => {
      // AK 17: nothing about the file/channel origin lines changes for this — they are covered by
      // the tests above and left untouched.
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')], 'TRENDING_DAILY'),
      });

      expect(dialog.text()).toContain('Aus 7TVs Bestenliste: 7TV Trend heute');
      expect(dialog.text()).not.toContain('Aus Kanal');
      expect(dialog.text()).not.toContain('Aus Datei');
    });

    it('names the other sort for the other leaderboard pick', () => {
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')], 'TOP_ALL_TIME'),
      });

      expect(dialog.text()).toContain('Aus 7TVs Bestenliste: 7TV Top insgesamt');
    });

    it('does not flag a leaderboard pick as a list that came from this channel', () => {
      // A leaderboard row has no source channel at all — `sameChannelFile` stays `fileOrigin`-only.
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')]),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });
  });

  describe('stale target', () => {
    it('stays quiet while the target last synced cleanly', () => {
      const dialog = render({ target: readyTarget({ syncFailureReason: null }) });

      expect(dialog.text()).not.toContain('Der letzte Abgleich des Zielkanals ist fehlgeschlagen.');
    });

    it('points out that the target picture may be stale after a failed sync', () => {
      const dialog = render({ target: readyTarget({ syncFailureReason: 'seventv_unavailable' }) });

      expect(dialog.text()).toContain('Der letzte Abgleich des Zielkanals ist fehlgeschlagen.');
      // A stale picture is a caveat, not a lock — 7TV decides at run time.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });
  });

  describe('name collisions for the foreign source (spec E7/AK 17)', () => {
    it('warns about a colliding alias and keeps it out of the run', () => {
      // Nothing new was built for the detection itself — `buildImportPreview` has produced
      // `nameCollisions` since #72. What is pinned here is that it keeps working for the third
      // source, whose rows carry the *alias* of the foreign set and therefore collide more readily
      // than a base name would — and that, since spec 2026-09-20, the row is excluded from the run
      // rather than merely flagged (AK 37/40).
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa'), row('new-2', 'Collides')]),
        target: readyTarget({
          setId: 'set-42',
          emotes: [
            {
              sevenTvEmoteId: 'existing-9',
              name: 'Collides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      expect(dialog.text()).toContain(
        '1 Name ist im Zielset schon vergeben, wird nicht übertragen:',
      );
      expect(dialog.text()).toContain('Collides');
      expect(dialog.button(EXECUTE).disabled).toBe(false);

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([
        {
          targetSetId: 'set-42',
          targetSetName: 'set-42',
          plan: addPlan([row('new-1', 'Kappa')]),
        },
      ]);
    });
  });

  describe('row order (§7.2 contract)', () => {
    it('renders every finding in the documented order', () => {
      const dialog = render({
        source: fileSource(
          [
            row('existing-1', 'AlreadyThere'),
            row('new-1', 'Collides'),
            row('new-2', 'Weird/Emote'),
          ],
          { fileName: 'emotes.json', channelName: 'HandOfBlood' },
          { discardedRows: 2, duplicatesCollapsed: 3 },
        ),
        targetChannelName: 'handofblood',
        target: readyTarget({
          setId: 'set-7',
          occupiedSlots: 999,
          capacity: 1000,
          syncFailureReason: 'seventv_unavailable',
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'AlreadyThere',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
            {
              sevenTvEmoteId: 'existing-2',
              name: 'Collides',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
          warning: {
            available: true,
            isOwnSet: false,
            otherTrackedChannelsSharingSet: ['tracked1'],
            otherModeratedChannelsSharingSet: ['modded1'],
          },
        }),
      });

      // The contract from docs/UI-Designsprache.md §7.2 — the sequence of statements, not the
      // markup carrying them. Note the two source findings: discarded rows (data actually lost)
      // stand before collapsed duplicates (merely folded), because the heavier finding reads first.
      // The title counts only `new-2` now: `new-1`/'Collides' is a name collision and, since spec
      // 2026-09-20, no longer part of `toAdd` at all (AK 37).
      const contract = [
        '1 Emote nach handofblood kopieren?',
        'Aus Datei emotes.json',
        'Export aus HandOfBlood,',
        'Ziel: handofblood · Set set-7',
        'Achtung: Das aktive Emote-Set',
        'Das Set hätte danach 1000 von 1000 Slots belegt.',
        'Der letzte Abgleich des Zielkanals ist fehlgeschlagen.',
        '1 Emote ist bereits im Zielset',
        '1 Name ist im Zielset schon vergeben, wird nicht übertragen:',
        '1 Name enthält Zeichen, die 7TV nicht anlegen kann:',
        '2 ungültige Zeilen in der Quelle verworfen.',
        '3 doppelte Zeilen in der Quelle zusammengefasst.',
        'Diese Liste stammt aus diesem Kanal.',
        'Das Hinzufügen läuft danach automatisch nacheinander.',
      ];

      expect(inRenderedOrder(dialog.text(), contract)).toEqual(contract);
      // The name lists belong to the two rejection lines above them, in the same order.
      expect(inRenderedOrder(dialog.text(), ['Collides', 'Weird/Emote'])).toEqual([
        'Collides',
        'Weird/Emote',
      ]);
      // No loading hint once the target has answered.
      expect(dialog.text()).not.toContain('Zieldaten werden geladen…');
    });

    it('puts the nothing-to-add banner after the source findings and before the closing lines', () => {
      const dialog = render({
        source: fileSource(
          [row('existing-1', 'PogU')],
          { channelName: 'targetchannel' },
          { duplicatesCollapsed: 1 },
        ),
        targetChannelName: 'targetchannel',
        target: readyTarget({
          emotes: [
            {
              sevenTvEmoteId: 'existing-1',
              name: 'PogU',
              imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
            },
          ],
        }),
      });

      const contract = [
        '1 Emote ist bereits im Zielset',
        '1 doppelte Zeile in der Quelle zusammengefasst.',
        'Das einzige Emote ist bereits im Zielset.',
        'Diese Liste stammt aus diesem Kanal.',
        'Das Hinzufügen läuft danach automatisch nacheinander.',
      ];

      expect(inRenderedOrder(dialog.text(), contract)).toEqual(contract);
    });
  });

  describe('target label and grouping (spec 8.6, AK 38/39/40)', () => {
    it('names the channel and the set in the header when both are known', () => {
      const dialog = render({
        targetChannelName: 'handofblood',
        target: readyTarget({ setName: 'Halloween' }),
      });

      expect(dialog.text()).toContain('Ziel: handofblood · Set Halloween');
    });

    it('falls back to the raw set id when the loader has no set name (the "today" path)', () => {
      const dialog = render({
        targetChannelName: 'handofblood',
        target: readyTarget({ setId: 'set-9', setName: null }),
      });

      expect(dialog.text()).toContain('Ziel: handofblood · Set set-9');
    });

    it('names the owner and the set for an untracked target, not a channel', () => {
      const dialog = render({
        targetChannelName: null,
        targetOwnerDisplayName: 'SomeEditor',
        target: readyTarget({ setName: 'Wegwerf-Set' }),
      });

      expect(dialog.text()).toContain('Ziel: Set Wegwerf-Set von SomeEditor');
      expect(dialog.text()).not.toContain('Ziel: handofblood');
    });

    it('reproduces the Halloween-set import proportions and the projection without an overflow banner (AK 38)', () => {
      const ALREADY_PRESENT_COUNT = 328;
      const ALIAS_MISMATCH_COUNT = 10;
      const NAME_COLLISION_COUNT = 192;
      const TO_ADD_COUNT = 232;

      const targetEmotes: EmoteListItem[] = [];
      const sourceRows: ReturnType<typeof row>[] = [];

      for (let index = 0; index < ALREADY_PRESENT_COUNT; index++) {
        const id = `present-${index}`;
        targetEmotes.push({
          sevenTvEmoteId: id,
          name: `Present${index}`,
          imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
        });
        sourceRows.push(row(id, `Present${index}`));
      }
      for (let index = 0; index < ALIAS_MISMATCH_COUNT; index++) {
        const id = `mismatch-${index}`;
        targetEmotes.push({
          sevenTvEmoteId: id,
          name: `TargetAlias${index}`,
          imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
        });
        sourceRows.push(row(id, `SourceAlias${index}`));
      }
      for (let index = 0; index < NAME_COLLISION_COUNT; index++) {
        targetEmotes.push({
          sevenTvEmoteId: `collision-target-${index}`,
          name: `Collide${index}`,
          imageUrl: 'https://cdn.7tv.app/placeholder/1x.webp',
        });
        sourceRows.push(row(`collision-source-${index}`, `Collide${index}`));
      }
      for (let index = 0; index < TO_ADD_COUNT; index++) {
        sourceRows.push(row(`new-${index}`, `New${index}`));
      }

      expect(sourceRows.length).toBe(762);

      const dialog = render({
        source: channelSource(sourceRows),
        target: readyTarget({
          setId: 'set-halloween',
          setName: 'Halloween',
          occupiedSlots: 687,
          capacity: 1000,
          emotes: targetEmotes,
        }),
      });

      expect(dialog.title()).toBe(`${TO_ADD_COUNT} Emotes nach targetchannel kopieren?`);
      expect(dialog.text()).toContain(
        `${ALREADY_PRESENT_COUNT} Emotes sind bereits im Zielset und werden übersprungen.`,
      );
      expect(dialog.text()).toContain(
        `${ALIAS_MISMATCH_COUNT} sind vorhanden, heißen dort aber anders:`,
      );
      expect(dialog.text()).toContain(
        `${NAME_COLLISION_COUNT} Namen sind im Zielset schon vergeben, werden nicht übertragen:`,
      );
      // 687 occupied + 232 toAdd = 919, comfortably under the capacity of 1000 — no overflow line.
      expect(dialog.text()).toContain('Das Set hätte danach 919 von 1000 Slots belegt.');
      expect(dialog.text()).not.toContain('Das überschreitet die Kapazität');

      // AK 40: none of the excluded rows (collisions or alias mismatches) reach the run at all —
      // the eventual `failed` count downstream is 0 for them, because they were never attempted.
      dialog.button(EXECUTE).click();
      expect(closed[0]?.plan.rows.length).toBe(TO_ADD_COUNT);
      expect(closed[0]?.plan.rows.some((r) => r.source.name.startsWith('Collide'))).toBe(false);
      expect(closed[0]?.plan.rows.some((r) => r.source.name.startsWith('SourceAlias'))).toBe(false);
    });
  });

  describe('resolving conflicts per row (#230)', () => {
    const IMG = 'https://cdn.7tv.app/placeholder/1x.webp';
    const GQL = 'https://7tv.io/v4/gql';

    function emote(sevenTvEmoteId: string, name: string): EmoteListItem {
      return { sevenTvEmoteId, name, imageUrl: IMG };
    }

    /** One plain add, two name collisions (the second onto a #74 duplicate with two aliases) and
     *  one alias mismatch. */
    function conflictSource(): ImportSource {
      return channelSource([
        row('new-1', 'Kappa'),
        row('src-a', 'Collides'),
        row('src-b', 'Dup'),
        row('src-m', 'Pog'),
      ]);
    }

    function conflictTarget(): ImportTargetLoadState {
      return readyTarget({
        setId: 'set-9',
        occupiedSlots: 10,
        capacity: 1000,
        emotes: [
          emote('tgt-a', 'Collides'),
          emote('tgt-b', 'Dup'),
          emote('tgt-b', 'Dup2'),
          emote('src-m', 'PogOld'),
        ],
      });
    }

    interface LiveEntry {
      id: string;
      alias: string | null;
      defaultName?: string;
    }

    /** The target set as 7TV's live read reports it — unchanged since the preview by default. */
    const LIVE_UNCHANGED: LiveEntry[] = [
      { id: 'tgt-a', alias: 'Collides', defaultName: 'CollidesDefault' },
      { id: 'tgt-b', alias: 'Dup', defaultName: 'DupDefault' },
      { id: 'tgt-b', alias: 'Dup2', defaultName: 'DupDefault' },
      { id: 'src-m', alias: 'PogOld', defaultName: 'Pog' },
    ];

    function setRead(entries: LiveEntry[], totalCount = entries.length) {
      return {
        data: {
          emoteSets: {
            emoteSet: {
              emotes: {
                totalCount,
                pageCount: 1,
                items: entries.map((entry) => ({
                  alias: entry.alias,
                  emote: { id: entry.id, defaultName: entry.defaultName ?? entry.alias },
                })),
              },
            },
          },
        },
      };
    }

    interface CapturedDownload {
      filename: string;
      blob: Blob;
    }

    /** Spies on the two seams `downloadFile` touches — same approach as `usage-stats-page.spec.ts`,
     *  since the unit-test builder refuses `vi.mock` for relative imports. */
    function captureDownloads(): CapturedDownload[] {
      const downloads: CapturedDownload[] = [];
      if (!('createObjectURL' in URL)) {
        Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
      }
      vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
        downloads.push({ filename: '', blob: blob as Blob });
        return 'blob:test';
      });
      vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
      const originalCreateElement = document.createElement.bind(document);
      vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
        const element = originalCreateElement(tag);
        if (tag === 'a') {
          vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
            const pending = downloads[downloads.length - 1];
            if (pending) {
              pending.filename = (element as HTMLAnchorElement).download;
            }
          });
        }
        return element;
      });
      return downloads;
    }

    /** jsdom has no layout — the step's viewport renders its rows on an animation frame. */
    async function waitFor(dialog: Harness, done: () => boolean): Promise<void> {
      for (let attempt = 0; attempt < 50; attempt++) {
        dialog.detect();
        if (done()) {
          return;
        }
        await new Promise((resolve) => setTimeout(resolve, 10));
        await dialog.fixture.whenStable();
      }
      throw new Error('condition not reached');
    }

    function host(dialog: Harness): HTMLElement {
      return dialog.fixture.nativeElement;
    }

    async function openStep(
      dialog: Harness,
      group: 'nameCollision' | 'aliasMismatch',
    ): Promise<void> {
      const trigger = dialog.element(`import-confirm-resolve-${group}`);
      if (!trigger) {
        throw new Error(`no resolve control for ${group}`);
      }
      trigger.click();
      await waitFor(dialog, () => host(dialog).querySelector('[data-resolve-index]') !== null);
    }

    function option(dialog: Harness, sourceName: string, kind: string): HTMLInputElement {
      const found = host(dialog).querySelector<HTMLInputElement>(
        `[role="radiogroup"][aria-label="Aktion für ${sourceName}"] input[value="${kind}"],` +
          `[role="radiogroup"][aria-label="Action for ${sourceName}"] input[value="${kind}"]`,
      );
      if (!found) {
        throw new Error(`no ${kind} option for ${sourceName}`);
      }
      return found;
    }

    function choose(dialog: Harness, sourceName: string, kind: string): void {
      option(dialog, sourceName, kind).click();
      dialog.detect();
    }

    function typeAlias(dialog: Harness, key: string, value: string): void {
      const field = host(dialog).querySelector<HTMLInputElement>(`#resolve-alias-${key}`);
      if (!field) {
        throw new Error(`no rename field for ${key}`);
      }
      field.value = value;
      field.dispatchEvent(new Event('input'));
      dialog.detect();
    }

    function apply(dialog: Harness): void {
      dialog.button('Übernehmen').click();
      dialog.detect();
    }

    function answerRead(dialog: Harness, body: object): void {
      TestBed.inject(HttpTestingController).expectOne(GQL).flush(body);
      dialog.detect();
    }

    /** Resolves `Collides` by replacing its target and saves the recovery file from a clean read. */
    async function replaceAndSave(dialog: Harness, live: LiveEntry[] = LIVE_UNCHANGED) {
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      answerRead(dialog, setRead(live));
    }

    it('shows no resolve control and today’s action row when nothing conflicts (AK 2)', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa')]),
        target: readyTarget({ emotes: [emote('other', 'Other')] }),
      });

      expect(dialog.hasButton('Auflösen')).toBe(false);
      expect(dialog.hasButton('Rückweg sichern')).toBe(false);
      // The §7.2 sequence is untouched by the new rows, which only exist with a conflict.
      const contract = [
        '1 Emote nach targetchannel kopieren?',
        'Aus Kanal sourcechannel',
        'Ziel: targetchannel · Set set-1',
        'Das Set hätte danach 11 von 1000 Slots belegt.',
        'Das Hinzufügen läuft danach automatisch nacheinander.',
      ];
      expect(inRenderedOrder(dialog.text(), contract)).toEqual(contract);
      expect(dialog.text()).not.toContain('aus dem Zielset entfernt');

      dialog.button(EXECUTE).click();
      expect(closed).toEqual([
        { targetSetId: 'set-1', targetSetName: 'set-1', plan: addPlan([row('new-1', 'Kappa')]) },
      ]);
    });

    it('offers one resolve control per conflict group, each described by its own finding (AK 3)', () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });

      const collisions = dialog.element('import-confirm-resolve-nameCollision');
      const mismatches = dialog.element('import-confirm-resolve-aliasMismatch');
      expect(collisions?.textContent?.trim()).toBe('Auflösen');
      expect(mismatches?.textContent?.trim()).toBe('Auflösen');
      expect(
        dialog.element(collisions?.getAttribute('aria-describedby') ?? '')?.textContent,
      ).toContain('2 Namen sind im Zielset schon vergeben');
      expect(
        dialog.element(mismatches?.getAttribute('aria-describedby') ?? '')?.textContent,
      ).toContain('1 ist vorhanden, heißt dort aber anders');
      // Two buttons with the same visible text: the accessible name says which group each opens.
      expect(collisions?.getAttribute('aria-label')).toBe('Namenskollisionen auflösen');
      expect(mismatches?.getAttribute('aria-label')).toBe('Abweichende Namen auflösen');
    });

    it('re-translates the open step on a language switch', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      expect(option(dialog, 'Collides', 'skip').parentElement?.textContent).toContain(
        'Überspringen',
      );

      const transloco = TestBed.inject(TranslocoService);
      await firstValueFrom(transloco.load('en'));
      transloco.setActiveLang('en');
      await waitFor(dialog, () => dialog.title() === 'Resolve 2 name collisions');

      // The row's action group is found by its English name now.
      expect(option(dialog, 'Collides', 'skip').parentElement?.textContent).toContain('Skip');
      expect(dialog.hasButton('Apply')).toBe(true);
      expect(dialog.hasButton('Back')).toBe(true);
    });

    it('locks Übernehmen when a replace here meets an adopt of the same target in the other group', async () => {
      // `PogOld` collides with the target entry `src-m`, which is also the emote the mismatch row
      // `Pog` would rename — replacing it and adopting it at once contradict each other.
      const dialog = render({
        source: channelSource([row('src-m', 'Pog'), row('src-z', 'PogOld')]),
        target: readyTarget({ emotes: [emote('src-m', 'PogOld')] }),
      });
      await openStep(dialog, 'aliasMismatch');
      choose(dialog, 'Pog', 'adoptSourceName');
      apply(dialog);

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'PogOld', 'replaceTarget');

      const applyButton = dialog.button('Übernehmen');
      expect(applyButton.disabled).toBe(true);
      expect(
        dialog.element(applyButton.getAttribute('aria-describedby') ?? '')?.textContent,
      ).toContain('Dasselbe Ziel wird ersetzt und umbenannt: PogOld, Pog.');
    });

    it('widens the pane only while the resolution step is up', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      expect(panelClasses.has('app-dialog-panel-wide')).toBe(false);

      await openStep(dialog, 'nameCollision');
      expect(panelClasses.has('app-dialog-panel-wide')).toBe(true);
      expect(dialog.title()).toBe('2 Namenskollisionen auflösen');

      dialog.button('Zurück').click();
      dialog.detect();
      expect(panelClasses.has('app-dialog-panel-wide')).toBe(false);
      expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
    });

    it('closes a dialog nobody resolved anything in with the plan of toAdd, without reading the set (AK 5)', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });

      await openStep(dialog, 'nameCollision');
      dialog.button('Zurück').click();
      dialog.detect();
      await openStep(dialog, 'aliasMismatch');
      dialog.button('Zurück').click();
      dialog.detect();
      dialog.button(EXECUTE).click();

      expect(closed).toEqual([
        { targetSetId: 'set-9', targetSetName: 'set-9', plan: addPlan([row('new-1', 'Kappa')]) },
      ]);
    });

    // #253, spec 4.5 point 15/18: the replace lock for an untracked target is gone — the dialog
    // offers "Ziel ersetzen" for an untracked target exactly as for a tracked one (AK 16), still
    // requires the recovery file before it starts (AK 17), and the file's name falls back to the
    // set id where a tracked run would have named the channel (spec 4.5 point 18).
    it('offers replace for an untracked target too, requires the recovery file, and names it by set id (R5, AK 16, 17)', async () => {
      const downloads = captureDownloads();
      const dialog = render({
        source: conflictSource(),
        target: conflictTarget(),
        targetChannelName: null,
        targetOwnerDisplayName: 'SomeEditor',
      });

      await openStep(dialog, 'nameCollision');
      for (const name of ['Collides', 'Dup']) {
        expect(option(dialog, name, 'replaceTarget').disabled).toBe(false);
        expect(option(dialog, name, 'renameSource').disabled).toBe(false);
      }
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      expect(dialog.element('import-confirm-removals')?.textContent).toContain(
        '1 Emote wird aus dem Zielset entfernt.',
      );
      // The safeguard is still a file, not a typed confirmation — a plan with a replace row never
      // gets the plain "Kopieren" button, untracked target included.
      expect(dialog.hasButton(EXECUTE)).toBe(false);
      expect(downloads).toEqual([]);

      dialog.button('Rückweg sichern').click();
      dialog.detect();
      answerRead(dialog, setRead(LIVE_UNCHANGED));

      // conflictTarget()'s own setId ('set-9') stands in for the channel name the tracked case
      // uses (`emotepurge_targetchannel_transfer-plan_…`, the sibling AK 16/17 test above).
      expect(downloads).toHaveLength(1);
      expect(downloads[0].filename).toMatch(/^emotepurge_set-9_transfer-plan_.*\.json$/);
      expect(dialog.hasButton('Starten')).toBe(true);
    });

    it('shows the removal line only once a replace is applied, and swaps Kopieren for Rückweg sichern (AK 20)', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      expect(dialog.element('import-confirm-removals')).toBeNull();

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      expect(dialog.element('import-confirm-removals')?.textContent).toContain(
        '1 Emote wird aus dem Zielset entfernt.',
      );
      expect(dialog.hasButton(EXECUTE)).toBe(false);
      expect(dialog.button('Rückweg sichern').disabled).toBe(false);
      // Replacing still adds: the title counts every ADD of the plan.
      expect(dialog.title()).toBe('2 Emotes nach targetchannel kopieren?');
    });

    describe('target renames', () => {
      it('titles a rename-only plan "align names" and shows the rename line, with no add at all', async () => {
        const dialog = render({
          source: channelSource([row('src-m', 'Pog')]),
          target: readyTarget({ emotes: [emote('src-m', 'PogOld')] }),
        });
        await openStep(dialog, 'aliasMismatch');
        choose(dialog, 'Pog', 'adoptSourceName');
        apply(dialog);

        expect(dialog.title()).toBe('1 Namen im Zielset angleichen?');
        expect(dialog.text()).toContain('1 Eintrag im Zielset wird umbenannt.');
        // Neutral hint, not a warning: nothing here is lost.
        expect(dialog.element('import-confirm-removals')).toBeNull();
      });

      // Spec #255: a rename-only plan adds nothing, so the ordinary "Kopieren" button and its
      // "Hinzufügen läuft danach…" notice would both misdescribe the run — the button reuses the
      // one established main-action verb ("Übertragen", DECISIONS #92) instead of a fourth word,
      // and the notice swaps to a matching sentence about the renames.
      it('shows the "Übertragen" button and a matching run notice for a rename-only plan', async () => {
        const dialog = render({
          source: channelSource([row('src-m', 'Pog')]),
          target: readyTarget({ emotes: [emote('src-m', 'PogOld')] }),
        });
        await openStep(dialog, 'aliasMismatch');
        choose(dialog, 'Pog', 'adoptSourceName');
        apply(dialog);

        expect(dialog.hasButton('Übertragen')).toBe(true);
        expect(dialog.hasButton('Kopieren')).toBe(false);
        expect(dialog.text()).toContain('Das Umbenennen läuft danach automatisch nacheinander.');
        expect(dialog.text()).not.toContain(
          'Das Hinzufügen läuft danach automatisch nacheinander.',
        );
      });

      // The same plan, but with an ordinary add row beside the adopt (titleIsRenameOnly is false
      // once addCount > 0) — the button and the notice both stay exactly as they were before #255.
      it('keeps the "Kopieren" button and the "Hinzufügen" notice when the plan also adds', async () => {
        const dialog = render({
          source: channelSource([row('new-1', 'Kappa'), row('src-m', 'Pog')]),
          target: readyTarget({ emotes: [emote('src-m', 'PogOld')] }),
        });
        await openStep(dialog, 'aliasMismatch');
        choose(dialog, 'Pog', 'adoptSourceName');
        apply(dialog);

        expect(dialog.hasButton(EXECUTE)).toBe(true);
        expect(dialog.hasButton('Übertragen')).toBe(false);
        expect(dialog.text()).toContain('Das Hinzufügen läuft danach automatisch nacheinander.');
        expect(dialog.text()).not.toContain(
          'Das Umbenennen läuft danach automatisch nacheinander.',
        );
      });

      it('keeps the add-counting title when the plan also adds, but still shows the rename line', async () => {
        const dialog = render({
          source: channelSource([row('new-1', 'Kappa'), row('src-m', 'Pog')]),
          target: readyTarget({ emotes: [emote('src-m', 'PogOld')] }),
        });
        await openStep(dialog, 'aliasMismatch');
        choose(dialog, 'Pog', 'adoptSourceName');
        apply(dialog);

        expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
        expect(dialog.text()).toContain('1 Eintrag im Zielset wird umbenannt.');
      });

      it('shows no rename line and the ordinary title while the mismatch row is left on skip', () => {
        const dialog = render({ source: conflictSource(), target: conflictTarget() });

        expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
        expect(dialog.text()).not.toContain('wird umbenannt');
        expect(dialog.text()).not.toContain('werden umbenannt');
      });
    });

    it('projects the slots from the net change: rename +1, replace 0, replace on a duplicate −1 (AK 21)', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      const projected = () => /danach (\d+) von 1000/.exec(dialog.text())?.[1];
      expect(projected()).toBe('11');

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'renameSource');
      typeAlias(dialog, 'src-a', 'CollidesNew');
      apply(dialog);
      expect(projected()).toBe('12');

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);
      expect(projected()).toBe('11');

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Dup', 'replaceTarget');
      apply(dialog);
      // Dup's target holds two entries (Dup, Dup2); the REMOVE takes both, the ADD puts one back.
      expect(projected()).toBe('10');
      expect(dialog.element('import-confirm-removals')?.textContent).toContain(
        '2 Emotes werden aus dem Zielset entfernt.',
      );
    });

    it('locks Übernehmen while a rename is invalid, naming the row next to the button (AK 10)', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');

      choose(dialog, 'Collides', 'renameSource');
      typeAlias(dialog, 'src-a', 'Collides neu');

      const applyButton = dialog.button('Übernehmen');
      expect(applyButton.disabled).toBe(true);
      const reason = dialog.element(applyButton.getAttribute('aria-describedby') ?? '');
      expect(reason?.textContent).toContain('Name, den 7TV nicht annimmt: Collides.');

      typeAlias(dialog, 'src-a', 'CollidesNeu');
      expect(dialog.button('Übernehmen').disabled).toBe(false);
    });

    it('starts a plan without replace straight away — no read, no download, still Kopieren', async () => {
      const downloads = captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'renameSource');
      typeAlias(dialog, 'src-a', 'CollidesNew');
      apply(dialog);
      await openStep(dialog, 'aliasMismatch');
      choose(dialog, 'Pog', 'adoptSourceName');
      apply(dialog);

      dialog.button(EXECUTE).click();

      expect(downloads).toEqual([]);
      expect(closed[0]?.plan.rows.map((each) => [each.action, each.alias])).toEqual([
        ['adoptSourceName', 'Pog'],
        ['add', 'Kappa'],
        ['renameSource', 'CollidesNew'],
      ]);
    });

    it('with a replace, reads the set live, saves the recovery file and only then offers Starten (AK 16, 17)', async () => {
      const downloads = captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      dialog.button('Rückweg sichern').click();
      dialog.detect();

      // While the read runs: locked, with its reason beside it, and no way to start yet.
      const pending = dialog.button('Rückweg sichern');
      expect(pending.disabled).toBe(true);
      expect(pending.getAttribute('aria-describedby')).toBe('import-confirm-verifying');
      expect(dialog.element('import-confirm-verifying')?.textContent).toContain(
        'Das Zielset wird gerade live geprüft…',
      );
      expect(dialog.hasButton('Starten')).toBe(false);
      expect(downloads).toEqual([]);

      answerRead(dialog, setRead(LIVE_UNCHANGED));

      expect(downloads).toHaveLength(1);
      expect(downloads[0].filename).toMatch(/^emotepurge_targetchannel_transfer-plan_.*\.json$/);
      const record = JSON.parse(await downloads[0].blob.text());
      expect(record.kind).toBe('transfer-run');
      expect(record.meta.stage).toBe('planned');
      expect(record.meta.counts).toEqual({ planned: 2, removals: 1 });
      expect(closed).toEqual([]);

      // A run elsewhere still locks the start silently, like "Kopieren" (§4.2).
      dialog.runBlocked.set(true);
      dialog.detect();
      expect(dialog.button('Starten').disabled).toBe(true);
      dialog.runBlocked.set(false);
      dialog.detect();

      dialog.button('Starten').click();
      expect(closed[0]?.plan.rows.map((each) => each.action)).toEqual(['replace', 'add']);
    });

    it('carries the live-read aliases and default name on each replace target of the closed plan (AK 17)', async () => {
      captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Dup', 'replaceTarget');
      apply(dialog);
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      // Same entries as the preview, listed in another order by 7TV.
      answerRead(
        dialog,
        setRead([
          { id: 'tgt-b', alias: 'Dup2', defaultName: 'DupDefault' },
          { id: 'tgt-b', alias: 'Dup', defaultName: 'DupDefault' },
          { id: 'tgt-a', alias: 'Collides' },
          { id: 'src-m', alias: 'PogOld' },
        ]),
      );

      dialog.button('Starten').click();

      const replace = closed[0]?.plan.rows.find((each) => each.action === 'replace');
      expect(replace?.action === 'replace' ? replace.target : null).toEqual({
        sevenTvEmoteId: 'tgt-b',
        aliases: ['Dup2', 'Dup'],
        hasAliaslessEntry: false,
        defaultName: 'DupDefault',
      });
    });

    it('on a drifted target: no download, the banner names the row, and the row is back on skip showing its live counterpart', async () => {
      const downloads = captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });

      await replaceAndSave(dialog, [
        { id: 'tgt-a', alias: 'Collides' },
        { id: 'tgt-a', alias: 'CollidesToo' },
        ...LIVE_UNCHANGED.slice(1),
      ]);

      expect(downloads).toEqual([]);
      expect(dialog.element('import-confirm-target-check')?.textContent).toContain(
        'Das Zielset hat sich seit der Vorschau geändert: Collides.',
      );
      expect(dialog.element('import-confirm-removals')).toBeNull();
      expect(dialog.hasButton(EXECUTE)).toBe(true);
      expect(dialog.hasButton('Starten')).toBe(false);

      dialog.button('Ziel neu laden').click();
      expect(retryCalls).toBe(1);

      await openStep(dialog, 'nameCollision');
      expect(option(dialog, 'Collides', 'skip').checked).toBe(true);
      // The user now confirms against the target as it is: both of its live aliases.
      const collidesRow = host(dialog).querySelector('[data-resolve-index="0"]');
      expect(collidesRow?.getAttribute('aria-label')).toBe(
        'Collides, im Ziel: Collides, CollidesToo',
      );
    });

    // Spec #255: the slot projection follows the last successful live read, not just the picker's
    // own load — it stays current after "Rückweg sichern"'s own re-read, and only a reload of the
    // target (never merely a decision change) puts it back.
    it('uses the live occupied-slot count from the last successful live read, and a target reload resets it back', async () => {
      captureDownloads();
      const source = channelSource([row('src-a', 'Collides')]);
      const freshTarget = () =>
        readyTarget({
          setId: 'set-slots',
          occupiedSlots: 15,
          capacity: 1000,
          emotes: [emote('tgt-a', 'Collides')],
        });
      const dialog = render({ source, target: freshTarget() });

      expect(dialog.text()).toContain('Das Set hätte danach 15 von 1000 Slots belegt.');

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      // The replace's own ADD cancels its own REMOVE (net delta 0) — the only thing that can move
      // the projected number below is the live read's own occupancy, not the plan.
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      answerRead(dialog, setRead([{ id: 'tgt-a', alias: 'Collides' }], 16));

      expect(dialog.text()).toContain('Das Set hätte danach 16 von 1000 Slots belegt.');

      // A reload of the target (a fresh ready()/preview()) resets the live number — the same reset
      // targetOverlays already gets, and for the same reason: it shows a fresh count of its own.
      dialog.target.set(freshTarget());
      dialog.detect();
      expect(dialog.text()).toContain('Das Set hätte danach 15 von 1000 Slots belegt.');
    });

    it('releases nothing on a failed or incomplete read, keeping the decision for another try', async () => {
      const downloads = captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      dialog.button('Rückweg sichern').click();
      dialog.detect();
      TestBed.inject(HttpTestingController).expectOne(GQL).error(new ProgressEvent('network'));
      dialog.detect();

      expect(dialog.element('import-confirm-target-check')?.textContent).toContain(
        'Das Zielset ließ sich gerade nicht vollständig lesen.',
      );
      expect(dialog.element('import-confirm-removals')).not.toBeNull();
      expect(dialog.button('Rückweg sichern').disabled).toBe(false);

      // A read whose count does not add up is treated the same — it cannot vouch for a removal.
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      answerRead(dialog, setRead(LIVE_UNCHANGED, LIVE_UNCHANGED.length + 1));

      expect(dialog.element('import-confirm-target-check')).not.toBeNull();
      expect(dialog.hasButton('Starten')).toBe(false);
      expect(downloads).toEqual([]);
    });

    it('discards a failed read that lands after the target already reloaded past it', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      dialog.button('Rückweg sichern').click();
      dialog.detect();
      const pending = TestBed.inject(HttpTestingController).expectOne(GQL);

      // The target reloads while this read is still in flight — a new plan, without cancelling
      // the subscription (only a fresh click does that).
      dialog.target.set(conflictTarget());
      dialog.detect();
      expect(dialog.element('import-confirm-target-check')).toBeNull();

      pending.error(new ProgressEvent('network'));
      dialog.detect();

      // The error answers a plan that is gone: it names nothing for the plan that replaced it.
      expect(dialog.element('import-confirm-target-check')).toBeNull();
    });

    it('starts one read per click, locks the resolve triggers while it runs, and lets only the newest read answer', async () => {
      const downloads = captureDownloads();
      const http = TestBed.inject(HttpTestingController);
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);

      // A double click reads once.
      dialog.button('Rückweg sichern').click();
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      const [first] = http.match(GQL);
      expect(http.match(GQL)).toEqual([]);
      // No plan change from the dialog itself while the read runs.
      expect(
        (dialog.element('import-confirm-resolve-nameCollision') as HTMLButtonElement).disabled,
      ).toBe(true);
      expect(
        (dialog.element('import-confirm-resolve-aliasMismatch') as HTMLButtonElement).disabled,
      ).toBe(true);

      // The target reloads mid-read: a new plan, so the executor offers a fresh read.
      dialog.target.set(conflictTarget());
      dialog.detect();
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      const second = http.expectOne(GQL);

      // The older read is cancelled — its answer can no longer land on the newer state.
      expect(first.cancelled).toBe(true);
      expect(() => first.flush(setRead(LIVE_UNCHANGED))).toThrow();
      expect(dialog.button('Rückweg sichern').disabled).toBe(true);
      expect(dialog.element('import-confirm-verifying')).not.toBeNull();

      second.flush(setRead(LIVE_UNCHANGED));
      dialog.detect();
      expect(downloads).toHaveLength(1);
      expect(dialog.hasButton('Starten')).toBe(true);
    });

    it('lets a failing older read change nothing once a newer read is running', async () => {
      captureDownloads();
      const http = TestBed.inject(HttpTestingController);
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      apply(dialog);
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      const first = http.expectOne(GQL);

      dialog.target.set(conflictTarget());
      dialog.detect();
      dialog.button('Rückweg sichern').click();
      dialog.detect();
      const second = http.expectOne(GQL);

      expect(first.cancelled).toBe(true);
      expect(() => first.error(new ProgressEvent('network'))).toThrow();
      dialog.detect();
      // No failure banner for the newer read, which is still running.
      expect(dialog.element('import-confirm-target-check')).toBeNull();
      expect(dialog.element('import-confirm-verifying')).not.toBeNull();

      second.flush(setRead(LIVE_UNCHANGED));
      dialog.detect();
      expect(dialog.hasButton('Starten')).toBe(true);
    });

    it('names committed decisions a reload of the target no longer fits', async () => {
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Collides', 'replaceTarget');
      choose(dialog, 'Dup', 'replaceTarget');
      apply(dialog);
      expect(dialog.element('import-confirm-target-check')).toBeNull();

      // After the reload both names belong to one target entry: two replaces of the same target.
      dialog.target.set(
        readyTarget({
          setId: 'set-9',
          emotes: [emote('tgt-x', 'Collides'), emote('tgt-x', 'Dup'), emote('src-m', 'PogOld')],
        }),
      );
      dialog.detect();

      expect(dialog.element('import-confirm-target-check')?.textContent).toContain(
        'Nach dem Neuladen passt die Entscheidung für diese Zeilen nicht mehr: Collides, Dup.',
      );
      expect(dialog.element('import-confirm-removals')).toBeNull();
      // Nothing to reload again, the user just did.
      expect(dialog.hasButton('Ziel neu laden')).toBe(false);

      // The next commit settles it.
      await openStep(dialog, 'nameCollision');
      apply(dialog);
      expect(dialog.element('import-confirm-target-check')).toBeNull();
    });

    it('asks for a new recovery file after a change, but not after an unchanged apply', async () => {
      captureDownloads();
      const dialog = render({ source: conflictSource(), target: conflictTarget() });
      await replaceAndSave(dialog);
      expect(dialog.hasButton('Starten')).toBe(true);

      await openStep(dialog, 'nameCollision');
      apply(dialog);
      expect(dialog.hasButton('Starten')).toBe(true);

      await openStep(dialog, 'nameCollision');
      choose(dialog, 'Dup', 'renameSource');
      typeAlias(dialog, 'src-b', 'DupNew');
      apply(dialog);
      // The file on disk describes the old plan.
      expect(dialog.hasButton('Starten')).toBe(false);
      expect(dialog.hasButton('Rückweg sichern')).toBe(true);
    });
  });
});
