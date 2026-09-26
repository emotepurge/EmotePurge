import { ImportOrigin } from './import-source';
import { RunItemStatus } from './seven-tv-run-engine';

/**
 * One target entry as an undo candidate names it — `alias: null` for the one entry that came back
 * without one, same shape as a purge/transfer-run restore row's own entries.
 */
export interface UndoCandidateTargetEntry {
  alias: string | null;
}

/**
 * One `replace` row's REMOVE, read back out as something a #254 undo might reverse: the source
 * emote to bring back, and the target entries it once held. `provenance` says how much trust the
 * *file itself* — as opposed to a live re-check — puts in the row (F17, Codex-Befund 2): a
 * `finished` file only ever names a row 7TV actually confirmed the REMOVE for, so its candidates are
 * `'confirmed'`; a `planned` file is written *before* the first REMOVE and proves nothing about
 * whether its run ever started, so every one of its candidates is `'unproven'` until a human
 * confirms the source shown really is the one that vanished (spec 17 K1/K2, the undo confirm
 * dialog's checkbox). Classification (#254 T2) reads this through unchanged; it is not something
 * the live check can derive.
 */
export interface UndoCandidate {
  sourceSevenTvEmoteId: string;
  sourceName: string;
  /** The alias the row's ADD wrote onto the target — the name a REMOVE-then-ADD undo pair moves
   *  back to the source. */
  alias: string;
  fileStatus: RunItemStatus | 'pending';
  target: {
    sevenTvEmoteId: string;
    entries: UndoCandidateTargetEntry[];
    defaultName: string | null;
  };
  provenance: 'confirmed' | 'unproven';
}

/**
 * Where an undo candidate's own transfer-run file came from — carried into the `transfer-undo`
 * file's `meta.undoneFile` (F6) so a human reading the undo's paper trail can follow the chain back
 * to the transfer it reverses, the same way `TransferRunRow.sourceName` lets a restore reconstruct a
 * rename. `verifiedAt`/`finishedAt` mirror whichever of `TransferRunMetaPlanned`/`Finished` the file
 * actually was — only one of the two is ever non-null. `origin` is `null` when the file's own
 * `meta.origin` is missing or not a recognizable {@link ImportOrigin} — untrusted JSON from a file,
 * validated rather than cast (see {@link readImportOrigin}), so a corrupted or hand-edited field
 * shows up here as an honest "unknown" instead of throwing later wherever this gets displayed or
 * re-serialized.
 *
 * `stage` is `TransferRunMeta['stage']`'s own literal union, spelled out here rather than imported:
 * `core/` may not import from `shared/` (layering rule), and `shared/export/transfer-run-export.ts`
 * re-exports this type for its own importers, so the two stay in sync by construction.
 */
export interface UndoSourceFileInfo {
  stage: 'planned' | 'finished';
  exportedAt: string;
  verifiedAt: string | null;
  finishedAt: string | null;
  origin: ImportOrigin | null;
}
