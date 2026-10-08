import { TestRequest } from '@angular/common/http/testing';

/** Where a mutation's query puts its result: the field names from `data` down to the mutation
 *  field (the last one), and the one scalar that field selects. */
export interface MutationSelection {
  path: string[];
  leaf: string;
}

/**
 * Reads the selection of a 7TV run mutation off its query text — `mutation Name(...) { a { b(...)
 * { c(...) { leaf } } } }` gives `{ path: ['a', 'b', 'c'], leaf: 'leaf' }`. Arguments are skipped
 * whole, including the input objects inside them. Only the single-chain shape every run mutation
 * has is understood; anything else throws, so a spec cannot answer a query this does not know.
 */
export function mutationSelection(query: string): MutationSelection {
  const tokens: string[] = query.match(/[A-Za-z_]\w*|[(){}]/g) ?? [];
  let index = tokens.indexOf('{');
  if (tokens[0] !== 'mutation' || index < 0) {
    throw new Error(`Not a mutation: ${query}`);
  }
  const path: string[] = [];
  for (;;) {
    const name = tokens[++index];
    if (name === undefined || !/^\w+$/.test(name)) {
      throw new Error(`Unexpected selection in: ${query}`);
    }
    index = skipArguments(tokens, index + 1);
    if (tokens[index] !== '{') {
      if (path.length === 0 || tokens[index] !== '}') {
        throw new Error(`Not a single-chain mutation: ${query}`);
      }
      return { path, leaf: name };
    }
    path.push(name);
  }
}

/**
 * Spec support, never imported by production code: answers a run's 7TV mutation the way 7TV answers
 * one it applied — HTTP 200, no `errors`, the result nested along the path the query text selects
 * (`mutationSelection`). Since #285 that result is what makes the run engine count a step `done`.
 *
 * Because the answer is built from the query and not from the `resultPath` a service declares, a
 * step confirmed through this proves the declared path is not longer than the query's, nor off to
 * the side of it. It cannot prove the path is not too *short* — a shorter path finds an object in
 * this answer as well. That is what `flushWithoutResult` is for.
 */
export function flushApplied(request: TestRequest): void {
  const { path, leaf } = mutationSelection(queryOf(request));
  request.flush({ data: nest(path, { [leaf]: 'applied' }) });
}

/**
 * The counterpart of `flushApplied`: everything along the query's path except the mutation field
 * itself — `{ data: { emoteSets: { emoteSet: {} } } }` for a `removeEmote`. A declared `resultPath`
 * that stops short of the mutation field would read this as confirmed, so a spec that expects the
 * step *not* to be `done` here pins the full path.
 */
export function flushWithoutResult(request: TestRequest): void {
  const { path } = mutationSelection(queryOf(request));
  request.flush({ data: nest(path.slice(0, -1), {}) });
}

function queryOf(request: TestRequest): string {
  const query: unknown = request.request.body?.query;
  if (typeof query !== 'string') {
    throw new Error('Not a GraphQL request');
  }
  return query;
}

/** Skips a parenthesised argument list starting at `index`, if there is one, and returns the index
 *  of the first token after it. */
function skipArguments(tokens: string[], index: number): number {
  if (tokens[index] !== '(') {
    return index;
  }
  let depth = 0;
  for (let at = index; at < tokens.length; at += 1) {
    if (tokens[at] === '(') {
      depth += 1;
    } else if (tokens[at] === ')') {
      depth -= 1;
      if (depth === 0) {
        return at + 1;
      }
    }
  }
  throw new Error('Unbalanced arguments');
}

function nest(path: readonly string[], value: object): object {
  return path.reduceRight<object>((inner, field) => ({ [field]: inner }), value);
}
