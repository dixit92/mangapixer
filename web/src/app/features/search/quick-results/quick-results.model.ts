import { signal, computed, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, defer, of, timer } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';

import { ApiService } from '../../../core/api/api.service';
import { SearchSuggestDto, SuggestItemDto } from '../../../core/api/api-types';

/** What choosing a quick result does (1.49.0, lane O). */
export type QuickAction =
  /** Opens a folder (browse) or an archive (reader): the router commands. */
  | { type: 'open'; commands: string[] }
  /** Opens the tag view for a creator, tag or genre: the ready `f` value. */
  | { type: 'filter'; filter: string }
  /** Completes the typed field's token (replaces the text from `start`). */
  | { type: 'complete'; start: number; insert: string };

export interface QuickEntry {
  /** Unique within an answer; also the option's element id suffix. */
  key: string;
  label: string;
  detail: string | null;
  count: number | null;
  icon: string;
  action: QuickAction;
}

export interface QuickSection {
  id: string;
  title: string;
  entries: QuickEntry[];
}

/** The server waits for no more than this: the client sends the text this long after the last keystroke. */
export const QUICK_DEBOUNCE_MS = 200;

/** The shortest text asked about (the server answers nothing for less). */
export const QUICK_MIN_CHARS = 2;

const GROUPS: readonly { id: keyof Pick<SearchSuggestDto, 'series' | 'folders' | 'archives' | 'creators' | 'tags'>; title: string }[] = [
  { id: 'series', title: 'Series' },
  { id: 'folders', title: 'Folders' },
  { id: 'archives', title: 'Archives' },
  { id: 'creators', title: 'Creators' },
  { id: 'tags', title: 'Tags' },
];

const FIELD_TITLES: Record<string, string> = {
  creator: 'Creators', tag: 'Tags', genre: 'Genres', publisher: 'Publishers', magazine: 'Magazines', type: 'Types', year: 'Years',
  series: 'Series', library: 'Libraries', is: 'Read state',
};

function iconOf(item: SuggestItemDto): string {
  switch (item.kind) {
    case 'creator': return 'person';
    case 'tag': return 'sell';
    case 'genre': return 'category';
    case 'series': return 'auto_stories';
    case 'archive': return 'menu_book';
    default: return 'folder';
  }
}

/** Where a node result opens: a folder in browse, an archive in the reader (the same links the Search page's cards use). */
export function nodeCommands(item: SuggestItemDto): string[] {
  return item.nodeKind === 'Archive' || (!item.nodeKind && item.kind === 'archive')
    ? ['/reader', item.id]
    : ['/libraries', item.libraryId ?? '', 'browse', item.id];
}

function entryOf(item: SuggestItemDto, group: string): QuickEntry {
  const action: QuickAction = item.filter ? { type: 'filter', filter: item.filter } : { type: 'open', commands: nodeCommands(item) };
  return {
    key: `${group}-${item.id}`,
    label: item.label,
    detail: item.detail ?? null,
    count: item.count ?? null,
    icon: iconOf(item),
    action,
  };
}

/** The answer as sections to show: the typed field's values when the text ends in one, else the non-empty groups. */
export function quickSections(answer: SearchSuggestDto | null): QuickSection[] {
  if (!answer) return [];
  if (answer.field) {
    const field = answer.field;
    return field.items.length === 0
      ? []
      : [{
        id: 'field',
        title: FIELD_TITLES[field.name] ?? 'Values',
        entries: field.items.map((v, i) => ({
          key: `field-${i}`,
          label: v.label,
          detail: null,
          count: v.count ?? null,
          icon: 'edit',
          action: { type: 'complete', start: field.start, insert: v.insert } as QuickAction,
        })),
      }];
  }
  return GROUPS
    .map((g) => ({ id: g.id as string, title: g.title, entries: answer[g.id].map((item) => entryOf(item, g.id)) }))
    .filter((s) => s.entries.length > 0);
}

export function quickEntries(sections: readonly QuickSection[]): QuickEntry[] {
  return sections.flatMap((s) => s.entries);
}

/** The text after choosing a typed value: the field's token replaced, a space after it so the next word can start. */
export function completeToken(text: string, start: number, insert: string): string {
  return `${text.slice(0, start)}${insert} `;
}

/**
 * The quick results of one search box (1.49.0, lane O): the text goes in through {@link ask}, 200 ms after the last keystroke the server is
 * asked once (a newer text cancels the request in flight, so a late answer never replaces a newer one), and the grouped answer comes out
 * as sections with one active entry for the keyboard. Created inside an injection context (a component field); stops with it.
 */
export class QuickResults {
  private readonly api = inject(ApiService);
  private readonly typed = new Subject<string>();
  /** The text last handed to the pipeline ('' = nothing to ask): the same text twice in a row asks once. */
  private last = '';

  readonly answer = signal<SearchSuggestDto | null>(null);
  readonly sections = computed(() => quickSections(this.answer()));
  readonly entries = computed(() => quickEntries(this.sections()));
  /** The keyboard's entry (index into {@link entries}); -1 = none, so Enter searches the text. */
  readonly active = signal(-1);
  readonly activeEntry = computed<QuickEntry | null>(() => this.entries()[this.active()] ?? null);

  constructor() {
    // A newer text (or "nothing") cancels the wait and the request of the one before it - the debounce lives INSIDE the switch, so
    // clearing never waits 200 ms and a late answer to an older text cannot arrive.
    this.typed.pipe(
      switchMap((text) => text
        ? timer(QUICK_DEBOUNCE_MS).pipe(switchMap(() => defer(() => this.api.searchSuggest(text)).pipe(catchError(() => of(null)))))
        : of(null)),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe((answer) => {
      this.answer.set(answer);
      this.active.set(-1);
    });
  }

  /** The text changed. */
  ask(text: string): void {
    const next = text.trim().length >= QUICK_MIN_CHARS ? text : '';
    if (next === this.last) return;
    this.last = next;
    this.typed.next(next);
  }

  /** Forgets the answer and cancels what is pending (the box was cleared or closed). The same text typed again asks again. */
  clear(): void {
    this.last = '';
    this.typed.next('');
  }

  /** Moves the keyboard's entry by one, wrapping (and past the ends through "none"). */
  move(step: 1 | -1): void {
    const count = this.entries().length;
    if (count === 0) return;
    const next = this.active() + step;
    this.active.set(next < -1 ? count - 1 : next >= count ? -1 : next);
  }
}
