// @ts-check
import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';
import angular from 'angular-eslint';

/**
 * Narrow rule set on purpose — see docs/DECISIONS.md (2026-08-01).
 *
 * This is NOT `recommended`. Every rule below either enforces a convention that
 * `web/.claude/CLAUDE.md` already states in prose, or catches a bug class that
 * is genuinely easy to hit in a signal-based codebase. Rules that would flag the
 * existing code without a corresponding written convention are left off, so that
 * a red lint run always means "you broke a documented rule" and never "the linter
 * has an opinion nobody agreed to".
 */
export default defineConfig(
  {
    ignores: ['.angular/', 'dist/', 'coverage/', 'test-results/', '.audit-out/'],
  },
  {
    files: ['**/*.ts'],
    // No `projectService`: none of the rules below need type information
    // (verified via each rule's `requiresTypeChecking` meta). Staying
    // syntax-only keeps the run fast and covers files outside any tsconfig,
    // such as playwright.config.ts.
    languageOptions: { parser: tseslint.parser },
    plugins: {
      '@typescript-eslint': tseslint.plugin,
      '@angular-eslint': angular.tsPlugin,
    },
    rules: {
      // The member order this repo already lives by. Deliberately coarse: `field`
      // is one bucket because input(), inject(), signal() and computed() are all
      // property initializers and no linter can tell them apart. What this does
      // enforce is the part that actually drifted — state declared *after* the
      // constructor, and private helpers wedged between public methods.
      '@typescript-eslint/member-ordering': [
        'error',
        {
          default: [
            'signature',
            'call-signature',
            'field',
            'constructor',
            'public-method',
            'protected-method',
            'private-method',
            '#private-method',
          ],
        },
      ],

      // Mirror the conventions written down in web/.claude/CLAUDE.md.
      '@angular-eslint/prefer-inject': 'error',
      '@angular-eslint/prefer-host-metadata-property': 'error',
      '@angular-eslint/prefer-standalone': 'error',
      '@angular-eslint/sort-lifecycle-methods': 'error',

      // Bug class, not style: a computed() that falls through returns undefined.
      '@angular-eslint/computed-must-return': 'error',

      // Deliberately NOT enabled: `@angular-eslint/no-uncalled-signals`. Its meta
      // claims `requiresTypeChecking: false`, but it calls getParserServices() and
      // fails without a type-aware parser. Turning it on would mean building a full
      // TypeScript program on every lint run — for a mistake that `ng build` and the
      // test suite already surface. Revisit if typed linting is ever wanted anyway.
    },
  },
  {
    files: ['**/*.html'],
    languageOptions: { parser: angular.templateParser },
    plugins: { '@angular-eslint/template': angular.templatePlugin },
    rules: {
      '@angular-eslint/template/prefer-control-flow': 'error',
      '@angular-eslint/template/prefer-ngsrc': 'error',
    },
  },
);
