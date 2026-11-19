import { SevenTvMutation } from './seven-tv-run-engine';

/** The one ADD every run that puts an entry into a 7TV set sends: the import, the restore, the undo
 *  and the wish add. `alias` is the name the entry gets in the set — without it 7TV falls back to the
 *  emote's default name, and `null` asks for exactly that fallback. It travels *inside* the
 *  `EmoteSetEmoteId` input object, not as a sibling argument — v4's `addEmote` field replaces v3's
 *  single `emotes(action: ADD, name:)` mutation with one field per operation (see docs/DECISIONS.md,
 *  #149). What each caller puts into `$alias` is that caller's own rule and is documented there. */
export const ADD_EMOTE_MUTATION: SevenTvMutation = {
  query: `
  mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) {
    emoteSets {
      emoteSet(id: $setId) {
        addEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`,
  resultPath: ['emoteSets', 'emoteSet', 'addEmote'],
};
