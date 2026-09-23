import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportOrigin, ImportRow } from './import-source';
import { DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvImportService } from './seven-tv-import.service';
import { SevenTvRestoreService } from './seven-tv-restore.service';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys the two services actually translate.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
    },
  },
};

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

// An import carries no internal id at all — it writes into another channel's set (#72).
const IMPORT_ROWS: ImportRow[] = [{ sevenTvEmoteId: '7tv-3', name: 'Sadge', imageUrl: null }];
const IMPORT_ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'sensitron' };

describe('SevenTvRunArbiter', () => {
  let arbiter: SevenTvRunArbiter;
  let deleteService: SevenTvDeleteService;
  let restoreService: SevenTvRestoreService;
  let importService: SevenTvImportService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    // Both services log a closing measurement on finish() — silenced, it is not what this suite is about.
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    arbiter = TestBed.inject(SevenTvRunArbiter);
    deleteService = TestBed.inject(SevenTvDeleteService);
    restoreService = TestBed.inject(SevenTvRestoreService);
    importService = TestBed.inject(SevenTvImportService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    // Every test below ends a run via cancel() rather than letting it complete, specifically so the
    // arbiter's own behaviour stays isolated from the services' closing sync-deleted/sync-restored/
    // resync calls (a different contract, already covered by the service specs). cancel() leaves its
    // in-flight GQL request marked cancelled, not gone — ignoreCancelled is this suite's deliberate
    // choice, not a fallback for an unrelated leak.
    httpMock.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('reports no active run when neither service is running', () => {
    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "delete" while a delete run is active, then null again after it ends', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]]);

    expect(arbiter.activeRun()).toBe('delete');

    deleteService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "restore" while a restore run is active, then null again after it ends', () => {
    restoreService.startRestore('set-1', 'sensitron', [EMOTES[0]]);

    expect(arbiter.activeRun()).toBe('restore');

    restoreService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "import" while an import run is active, then null again after it ends', () => {
    importService.startImport(
      { setId: 'set-2', channelName: 'kanal_b' },
      IMPORT_ORIGIN,
      IMPORT_ROWS,
    );

    expect(arbiter.activeRun()).toBe('import');

    importService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  // Constructed: the panels prevent this from happening in practice (they check activeRun() before
  // starting a second kind), so this pins the computed's fixed check order rather than an outcome
  // that can occur unaided — see Plan-70 Task 3.
  it('prefers "delete" when a delete and a restore run are both active at once', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]]);
    restoreService.startRestore('set-1', 'sensitron', [EMOTES[1]]);

    expect(arbiter.activeRun()).toBe('delete');

    deleteService.cancel();
    restoreService.cancel();
  });

  // Same construction one branch further down: the import is checked last, so a delete running
  // alongside it wins.
  it('prefers "delete" when a delete and an import run are both active at once', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]]);
    importService.startImport(
      { setId: 'set-2', channelName: 'kanal_b' },
      IMPORT_ORIGIN,
      IMPORT_ROWS,
    );

    expect(arbiter.activeRun()).toBe('delete');

    deleteService.cancel();
    importService.cancel();
  });
});
