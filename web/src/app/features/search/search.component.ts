import { Component, DestroyRef, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { BreakpointObserver } from '@angular/cdk/layout';
import { map } from 'rxjs/operators';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatChipListbox, MatChipsModule } from '@angular/material/chips';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { CatalogNodeDto, FacetBucketDto, FacetChipDto, LibraryReadStateFilter, SearchResultsDto, SeriesMatchDto } from '../../core/api/api-types';
import {
  addFilterParam, bucketLabel, bucketRank, bucketToken, filterToken, filterValueId, isExcludeParam, isFilterParam, locationText, toggleExcludeParam,
} from '../metadata/info/info-labels';
import { FacetLocationDto, FacetSearchFields, FacetWorkDto, SearchFilterDto } from '../metadata/info/info-types';
import { QuickResultsComponent } from './quick-results/quick-results.component';
import { QuickEntry, QuickResults, completeToken } from './quick-results/quick-results.model';

/**
 * The search page's state as it appears in the URL (1.45.0): `q` (the search text), `read` (the read-state filter; absent = all) and
 * `f` (lane P: tag filters `<bucket>:<value id>`, repeatable, all must match). ONE model read from and written to the query string.
 */
export interface SearchParams {
  q: string;
  read: LibraryReadStateFilter;
  f: string[];
}

const READ_FILTERS: readonly LibraryReadStateFilter[] = ['all', 'reading', 'read', 'unread'];

/** Reads the page state from the URL; unknown values fall back to the defaults, a malformed or repeated `f` is dropped. */
export function searchParamsFromQuery(query: ParamMap): SearchParams {
  const read = query.get('read') as LibraryReadStateFilter | null;
  const f = [...new Set(query.getAll('f').filter(isFilterParam))];
  return { q: query.get('q') ?? '', read: read && READ_FILTERS.includes(read) ? read : 'all', f };
}

/** The query string for a page state: defaults are left out (an empty search is a bare `/search`). */
export function searchParamsToQuery(params: SearchParams): Params {
  return {
    q: params.q.trim() ? params.q : null,
    read: params.read !== 'all' ? params.read : null,
    f: params.f.length > 0 ? params.f : null,
  };
}

function sameParams(a: SearchParams, b: SearchParams): boolean {
  return a.q === b.q && a.read === b.read && a.f.length === b.f.length && a.f.every((v, i) => v === b.f[i]);
}

/** The search answer with lane Q's tag-search fields (works + resolved filters). */
type SearchAnswer = SearchResultsDto & FacetSearchFields;

/** A filter chip in the field: the `f` value, and its name once the server resolved it. */
interface FilterChip {
  param: string;
  label: string;
  /** False when the server did not know the value (or the viewer may not see it): the chip says so and can be removed. */
  known: boolean;
  /** 1.49.0 (lane T): an exclusion (`-genre:fv_..`) - the works must not carry it. */
  excluded: boolean;
}

/** 1.49.0 (lane T): the typed syntax the "?" panel lists - each line can be put into the field. */
export const SYNTAX_EXAMPLES: readonly { example: string; meaning: string }[] = [
  { example: 'tag:"time travel"', meaning: 'Works with this tag' },
  { example: 'genre:horror|mystery', meaning: 'Either genre (| = or)' },
  { example: '-genre:horror', meaning: 'Without this genre (- = not)' },
  { example: 'creator:urasawa', meaning: 'A creator with this name; quotes for the exact name' },
  { example: 'year:2010..2015', meaning: 'Started in these years (also ..1999, 2020..)' },
  { example: 'series:monster', meaning: 'Title, other titles or folder name' },
  { example: 'publisher:"Big Comic" type:manga', meaning: 'Publisher, magazine:, type:' },
  { example: 'is:unread', meaning: 'Read state: is:read, is:unread, is:reading' },
  { example: 'library:manga', meaning: 'Only this library' },
];

/**
 * 1.50.0 (lane U): the quick results show under the field on every width (desktop and tablet too, with a keyboard hint line and
 * arrow / Enter / Escape on the field; the header box steps aside on this page); Refine's "+N more" opens the whole bucket (and "Show
 * fewer" closes it); "or" asks which included tag of the bucket to join when there are two or more.
 *
 * Search component. Uses the FTS5 trigram search via the API (1.49.0, lane S: words in any order over names, close matches for a typo,
 * "+N inside" on folder cards, counts by kind; favourites lifted by the server). 1.45.0 (lane P): the tag view - a chip of the Info panel opens this page
 * with `f=<bucket>:<value id>`; the filters show as removable chips in the field (they scroll sideways in their own row on a phone),
 * typed field syntax (`creator:"..."`) passes through `q`, and the results are WORK cards (cover, title, where the work lives as a
 * breadcrumb of display names, "2 locations" for a split series) with the same keyset "Load more" and (1.47.0) read filter. Without a filter the
 * page is the folder-name search it was.
 */
@Component({
  selector: 'app-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    MatIconModule,
    MatFormFieldModule,
    MatButtonModule,
    MatTooltipModule,
    MatChipsModule,
    CoverImageDirective,
    StarToggleComponent,
    InfoToggleComponent,
    SeriesInfoHoverDirective,
    FolderRollupBadgeComponent,
    QuickResultsComponent,
  ],
  template: `
    <h2>Search</h2>
    <!-- 1.49.0 (lane T): the field and its "?" syntax help side by side. -->
    <div class="search-wrap">
    <div class="search-row">
    <mat-form-field appearance="outline" class="search-field" [class.has-filters]="filterChips().length > 0">
      <mat-label>Search libraries</mat-label>
      <!-- Tag filters (1.45.0): removable chips in the field, in their own row that scrolls sideways when it is full. -->
      <mat-chip-grid #filterGrid class="filter-chips" aria-label="Tag filters" data-testid="search-filters">
        @for (chip of filterChips(); track chip.param) {
          <mat-chip-row (removed)="removeFilter(chip.param)" [class.unknown]="!chip.known" [class.excluded]="chip.excluded"
                        data-testid="search-filter-chip" [attr.data-excluded]="chip.excluded">
            <!-- 1.49.0 (lane T): include <-> exclude this tag. -->
            <button type="button" class="exclude-toggle" (click)="toggleExclude(chip.param); $event.stopPropagation()"
                    [attr.aria-label]="(chip.excluded ? 'Include ' : 'Exclude ') + chip.label" [attr.aria-pressed]="chip.excluded"
                    [matTooltip]="chip.excluded ? 'Excluded - click to include' : 'Click to exclude'" data-testid="search-filter-exclude">
              <mat-icon>{{ chip.excluded ? 'block' : 'add_circle_outline' }}</mat-icon>
            </button>
            <span class="filter-label">{{ chip.label }}</span>
            <button matChipRemove type="button" [attr.aria-label]="'Remove filter ' + chip.label"><mat-icon>cancel</mat-icon></button>
          </mat-chip-row>
        }
      </mat-chip-grid>
      <!-- The text field under the chip row (not inside it): a full row scrolls sideways and the text stays in reach. -->
      <input class="search-text" [matChipInputFor]="filterGrid" [(ngModel)]="query" (ngModelChange)="onSearch()"
             [placeholder]="filterChips().length > 0 ? 'Add search text...' : 'Enter search text...'" aria-label="Search text"
             aria-autocomplete="list" [attr.aria-controls]="quickOpen() ? 'search-quick-list' : null"
             [attr.aria-activedescendant]="quickActiveId()"
             (focus)="onQuickFocus()" (blur)="onQuickBlur()" (keydown)="onQuickKey($event)" data-testid="search-input">
      <mat-icon matSuffix>search</mat-icon>
    </mat-form-field>
    <button mat-icon-button type="button" class="syntax-toggle" (click)="syntaxOpen.set(!syntaxOpen())" [attr.aria-expanded]="syntaxOpen()"
            aria-controls="search-syntax" aria-label="Search syntax help" matTooltip="Search syntax" data-testid="search-syntax-toggle">
      <mat-icon>help_outline</mat-icon>
    </button>
    </div>

    <!-- 1.49.0 (lane O) / 1.50.0 (lane U): the grouped quick results under the field while typing (series, folders, archives, creators,
         tags - or the values of a typed field); a result opens, a field value completes the token. On a phone the list sits in the page flow
         (the header has no box there); on desktop and tablet it is a dropdown over the results with a keyboard hint line, and the header box
         is hidden on this page so there is one box. -->
    @if (quickOpen()) {
      <div class="quick-panel" [class.quick-desktop]="!phone()" data-testid="search-quick">
        <app-quick-results [sections]="quick.sections()" [activeKey]="quick.activeEntry()?.key ?? null" idPrefix="sq-"
                           listId="search-quick-list" label="Quick results" [keyboardHint]="!phone()" (picked)="pickQuick($event)" />
      </div>
    }
    <span class="sr-status" role="status" aria-live="polite">{{ quickStatus() }}</span>
    </div>

    <!-- 1.49.0 (lane T, S4): the typed syntax with examples (as YACReader's search help); an example goes into the field. -->
    @if (syntaxOpen()) {
      <section id="search-syntax" class="syntax" aria-label="Search syntax" data-testid="search-syntax" tabindex="-1" (keydown.escape)="syntaxOpen.set(false)">
        <p class="syntax-intro">Plain words find names. Fields narrow by tags; combine any of them:</p>
        <ul class="syntax-list">
          @for (ex of syntaxExamples; track ex.example) {
            <li>
              <button type="button" class="syntax-example" (click)="useExample(ex.example)" data-testid="search-syntax-example">
                <code>{{ ex.example }}</code>
              </button>
              <span class="syntax-meaning">{{ ex.meaning }}</span>
            </li>
          }
        </ul>
        <p class="syntax-intro">A word a field does not know stays plain text.</p>
      </section>
    }

    <!-- Read-state filter (1.45.0): the browse Show filter's choices as one chip row over the results, applied on the server before
         paging (the count and "Load more" stay right). Shown once there is search text or a tag, so a filter with no hits can be switched
         back. 1.47.0: also in the tag view - a WORK is read, reading or unread by the browse rule over its places you can see. -->
    @if (active()) {
      <mat-chip-listbox class="read-filter" aria-label="Show" [value]="params().read" (change)="onReadChip($event.value)"
                        hideSingleSelectionIndicator>
        @for (opt of readOptions; track opt.value) {
          <mat-chip-option [value]="opt.value">{{ opt.label }}</mat-chip-option>
        }
      </mat-chip-listbox>
    }

    <!-- 1.49.0 (lane T, owner decision 9 a): Refine - the tags of all the results with how many carry each; a chip adds it to the search,
         "or" joins it to a tag of the bucket already in the search (any of; with two or more included, it asks which), the minus excludes
         it. 1.50.0 (lane U): "+N more" opens the whole bucket (a scrolling list), "Show fewer" closes it again. -->
    @if (tagView() && refine().length > 0) {
      <details class="refine" open data-testid="search-refine">
        <summary class="section-heading">Refine</summary>
        @for (b of refine(); track b.bucket) {
          <div class="refine-bucket" [attr.data-bucket]="b.bucket">
            <div class="refine-head">
              <h4 class="refine-label">{{ label(b.bucket) }}</h4>
              @if (expandedChips()[b.bucket]) {
                <button type="button" class="refine-fewer" (click)="collapseBucket(b.bucket)" data-testid="refine-fewer"
                        [attr.aria-label]="'Show fewer ' + label(b.bucket)">Show fewer</button>
              }
            </div>
            @if (orPick(); as pick) {
              @if (pick.bucket === b.bucket) {
                <div class="or-pick" role="group" [attr.aria-label]="'Join ' + pick.display + ' with'" data-testid="refine-or-menu"
                     (keydown.escape)="orPick.set(null)">
                  <span class="or-pick-title">Either {{ pick.display }} or</span>
                  @for (target of pick.targets; track target.param) {
                    <button type="button" class="or-pick-target" (click)="chooseOr(target.param)" data-testid="refine-or-target">{{ target.label }}</button>
                  }
                  <button type="button" class="or-pick-cancel" (click)="orPick.set(null)" data-testid="refine-or-cancel">Cancel</button>
                </div>
              }
            }
            <ul class="refine-chips" [class.open]="!!expandedChips()[b.bucket]">
              @for (chip of shownChips(b); track chip.id) {
                <li class="refine-chip">
                  <button type="button" class="refine-add" (click)="addRefine(chip.filter, 'all')" data-testid="refine-chip"
                          [attr.aria-label]="'Add ' + chip.display + ', ' + chip.count + ' of these results'">
                    <span class="name">{{ chip.display }}</span><span class="count">{{ chip.count }}</span>
                  </button>
                  @if (canOr(chip.filter)) {
                    <button type="button" class="refine-or" (click)="onOr(chip, b.bucket)" data-testid="refine-or"
                            [attr.aria-haspopup]="orTargets(chip.filter).length > 1 ? 'true' : null"
                            [attr.aria-label]="'Or ' + chip.display" matTooltip="Or: either tag">or</button>
                  }
                  <button type="button" class="refine-not" (click)="addRefine(chip.filter, 'not')" data-testid="refine-not"
                          [attr.aria-label]="'Exclude ' + chip.display" matTooltip="Exclude">
                    <mat-icon>remove_circle_outline</mat-icon>
                  </button>
                </li>
              }
              @if (b.more > 0 && !expandedChips()[b.bucket]) {
                <li class="refine-more">
                  <button type="button" class="refine-more-btn" (click)="expandBucket(b.bucket)" [disabled]="expanding() === b.bucket"
                          data-testid="refine-more" [attr.aria-label]="'Show all ' + label(b.bucket) + ', ' + b.more + ' more'">
                    {{ expanding() === b.bucket ? 'Loading…' : '+' + b.more + ' more' }}
                  </button>
                </li>
              }
            </ul>
            @if (expandError() === b.bucket) {
              <p class="refine-error" role="alert" data-testid="refine-error">Could not load the rest. Try again.</p>
            }
          </div>
        }
      </details>
    }

    @if (seriesMatches().length > 0) {
      <section class="series-matches" aria-labelledby="series-matches-heading">
        <h3 id="series-matches-heading" class="section-heading">Series matches</h3>
        <div class="results-grid">
          @for (match of seriesMatches(); track match.node.id) {
            <ng-container *ngTemplateOutlet="card; context: { node: match.node, aka: match.matchedTitle }" />
          }
        </div>
      </section>
    }

    @if (works().length > 0) {
      <p class="result-count">{{ totalCount() }} work{{ totalCount() === 1 ? '' : 's' }}</p>
      <div class="results-grid" data-testid="work-results">
        @for (work of works(); track work.node.id + ':' + $index) {
          <div class="result-card work-card" data-testid="work-card">
            <!-- The work opens where its first visible location is: a folder in browse, a one-shot archive in the reader. -->
            <a [routerLink]="getNodeLink(work.node)" class="work-link">
              <div class="cover">
                @if (work.coverUrl ?? coverSrc(work.node); as src) {
                  <img appCover [src]="src" alt="" loading="lazy">
                }
                <mat-icon class="cover-fallback">{{ kindIcon(work.node) }}</mat-icon>
                @if (work.node.isRead) {
                  <span class="badge read" matTooltip="Read">✓ Read</span>
                } @else if (work.node.readingState === 'InProgress') {
                  <span class="badge reading">Reading</span>
                }
                <app-folder-rollup-badge [rollup]="work.node.readRollup" />
              </div>
              <div class="result-title" [title]="work.title">{{ work.title }}</div>
            </a>
            @if (work.locations[0]; as first) {
              <div class="crumbs">
                <span class="crumb-line" [title]="trailText(first)">{{ trailText(first) }}</span>
              </div>
            }
            @if (work.locationCount > 1) {
              <button type="button" class="locations-toggle" (click)="toggleLocations(workKey(work, $index))"
                      [attr.aria-expanded]="expanded().has(workKey(work, $index))" data-testid="work-locations-toggle">
                {{ work.locationCount }} locations
                <mat-icon>{{ expanded().has(workKey(work, $index)) ? 'expand_less' : 'expand_more' }}</mat-icon>
              </button>
              @if (expanded().has(workKey(work, $index))) {
                <ul class="locations" data-testid="work-locations">
                  @for (loc of work.locations; track loc.nodeId) {
                    <li><a [routerLink]="locationLink(work, loc)" [title]="trailText(loc)">{{ trailText(loc) }}</a></li>
                  }
                  @if (work.locationCount > work.locations.length) {
                    <li class="more-places">and {{ work.locationCount - work.locations.length }} more</li>
                  }
                </ul>
              }
            }
          </div>
        }
      </div>
      @if (hasMore()) {
        <div class="load-more">
          <button mat-stroked-button type="button" (click)="loadMore()" [disabled]="loadingMore()">
            {{ loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        </div>
      }
    } @else if (results().length > 0) {
      <!-- 1.49.0 (lane S): "N folders, M archives", and a note when close matches for a typo are listed (always after exact hits). -->
      <p class="result-count" data-testid="search-count">{{ countText() }}</p>
      @if (closeMatchText(); as hint) {
        <p class="search-hint" data-testid="search-close-hint">{{ hint }}</p>
      }
      <div class="results-grid">
        @for (node of results(); track node.id) {
          <ng-container *ngTemplateOutlet="card; context: { node: node, aka: null }" />
        }
      </div>
      <!-- Keyset paging (1.45.0): the next page after the last result, with the same text and filter. -->
      @if (hasMore()) {
        <div class="load-more">
          <button mat-stroked-button type="button" (click)="loadMore()" [disabled]="loadingMore()">
            {{ loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        </div>
      }
    } @else if (searched() && seriesMatches().length === 0) {
      <p class="no-results">{{ noResultsText() }}</p>
    }
    <!-- 1.49.0: a 1-2 character search matches names with a word starting with it - say so, it is not a typo search. -->
    @if (shortSearch() && searched() && works().length === 0) {
      <p class="search-hint" data-testid="search-short-hint">Names with a word starting with "{{ params().q.trim() }}". Type 3 or more characters to search inside words.</p>
    }

    <ng-template #card let-node="node" let-aka="aka">
      <!-- Series info (1.27.0): the (i) in the cover's bottom-left corner as in browse, and
           the cover and title are hover zones for the summary popover. -->
      <a #cardEl [routerLink]="getNodeLink(node)" class="result-card">
        <div class="cover" [appSeriesInfoHover]="node.hasSeriesInfo ? node.id : null" [hoverAnchor]="cardEl">
          @if (coverSrc(node); as src) {
            <img appCover [src]="src" alt="" loading="lazy">
          }
          <mat-icon class="cover-fallback">{{ kindIcon(node) }}</mat-icon>
          <!-- Folder-vs-archive badge (1.12.0): shown over every result - including
               ones with a real cover image, where the fallback icon above is hidden -
               so the result kind stays legible regardless of cover art. -->
          <span class="kind-badge" [matTooltip]="kindLabel(node)" [attr.aria-label]="kindLabel(node)" role="img">
            <mat-icon>{{ kindIcon(node) }}</mat-icon>
          </span>
          <!-- Favorites prominence (1.21.0): the star (badge + interactive toggle)
               appears in search only when the user opted in; when off, results render
               normally. 1.49.0: the server lifts favourites to the top across pages (it was per page here). -->
          @if (prominence()) {
            <app-star-toggle [nodeId]="node.id" [favorite]="!!node.isFavorite" [overlay]="true" [compact]="true" />
          }
          <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo" [overlay]="true" />
          <!-- The viewer's read state (1.45.0), as browse cards show it: an archive's mark, a folder's rollup (top-right corner). -->
          @if (node.isRead) {
            <span class="badge read" matTooltip="Read">✓ Read</span>
          } @else if (node.readingState === 'InProgress') {
            <span class="badge reading">Reading</span>
          }
          <app-folder-rollup-badge [rollup]="node.readRollup" />
        </div>
        <div class="result-title" [title]="node.displayName"
             [appSeriesInfoHover]="node.hasSeriesInfo ? node.id : null" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
        @if (aka) {
          <div class="result-aka" [matTooltip]="aka">aka {{ aka }}</div>
        }
        <!-- 1.49.0 "+N inside": archives found only through this folder's name are counted on its card instead of listed. -->
        @if (insideCounts()[node.id]; as inside) {
          <div class="result-inside" data-testid="search-inside">+{{ inside }} inside</div>
        }
      </a>
    </ng-template>
  `,
  styles: [`
    .search-wrap { position: relative; max-width: 648px; min-width: 0; }
    .search-row { display: flex; align-items: flex-start; gap: 4px; max-width: 648px; min-width: 0; }
    .search-field { flex: 1 1 auto; width: 100%; max-width: 600px; min-width: 0; }
    .syntax-toggle { flex: none; margin-top: 8px; color: var(--mp-text-muted); }
    .quick-panel {
      max-width: 600px; margin: -8px 0 12px; border-radius: 8px; overflow: hidden; min-width: 0;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); background: var(--mp-surface-high);
    }
    /* Desktop / tablet (1.50.0): a dropdown over the results, under the field, as the header box's list. */
    .quick-desktop {
      position: absolute; z-index: 20; left: 0; top: calc(100% - 22px); width: min(600px, calc(100% - 48px)); margin: 0;
      box-shadow: 0 8px 24px rgb(var(--mp-shade-rgb) / 0.35);
    }
    .sr-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    .syntax {
      max-width: 600px; margin: -8px 0 12px; padding: 10px 12px; border-radius: 8px;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.12); background: rgb(var(--mp-ink-rgb) / 0.03); font-size: 13px;
    }
    .syntax-intro { margin: 4px 0; color: var(--mp-text-muted); }
    .syntax-list { margin: 4px 0; padding: 0; list-style: none; }
    .syntax-list li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 10px; padding: 2px 0; min-width: 0; }
    .syntax-example {
      padding: 4px 6px; border: 0; border-radius: 4px; background: rgb(var(--mp-ink-rgb) / 0.06); color: var(--mp-text);
      font: inherit; cursor: pointer; max-width: 100%; overflow-wrap: anywhere; text-align: left;
    }
    .syntax-example:hover { background: rgb(var(--mp-ink-rgb) / 0.1); }
    .syntax-example:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 1px; }
    .syntax-meaning { color: var(--mp-text-muted); min-width: 0; }
    .exclude-toggle {
      display: inline-flex; align-items: center; justify-content: center; flex: none; width: 24px; height: 24px; margin: 0 2px 0 -6px;
      padding: 0; border: 0; border-radius: 50%; background: transparent; color: var(--mp-text-muted); cursor: pointer; vertical-align: middle;
    }
    .exclude-toggle mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .exclude-toggle:focus-visible { outline: 2px solid var(--mp-accent); }
    .excluded .filter-label { text-decoration: line-through; }
    .excluded .exclude-toggle { color: var(--mp-warn); }
    .refine { max-width: 960px; margin: 4px 0 8px; min-width: 0; }
    .refine summary { cursor: pointer; margin: 8px 0; }
    .refine-bucket { margin: 0 0 8px; min-width: 0; }
    .refine-label { margin: 0 0 4px; font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: var(--mp-text-dim); }
    .refine-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; min-width: 0; }
    .refine-chip {
      display: inline-flex; align-items: center; max-width: 100%; min-width: 0; height: 32px; border-radius: 16px;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); background: rgb(var(--mp-ink-rgb) / 0.04);
    }
    .refine-chip button {
      display: inline-flex; align-items: center; gap: 6px; height: 100%; border: 0; background: transparent; color: var(--mp-text);
      font: inherit; font-size: 13px; cursor: pointer; min-width: 0;
    }
    .refine-chip button:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: -2px; }
    .refine-add { padding: 0 6px 0 12px; border-radius: 16px 0 0 16px; }
    .refine-add .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .refine-add .count { flex: none; font-size: 12px; color: var(--mp-text-muted); }
    .refine-or { flex: none; padding: 0 6px; font-size: 12px !important; color: var(--mp-accent) !important; }
    .refine-not { flex: none; width: 30px; justify-content: center; padding: 0 6px 0 0; border-radius: 0 16px 16px 0; color: var(--mp-text-muted) !important; }
    .refine-not mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .refine-chip button:hover { background: rgb(var(--mp-ink-rgb) / 0.08); }
    .refine-more { align-self: center; font-size: 12px; color: var(--mp-text-muted); }
    .refine-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .refine-head .refine-label { margin: 0; }
    .refine-more-btn, .refine-fewer, .or-pick button {
      border: 0; background: transparent; color: var(--mp-accent); font: inherit; font-size: 12px; cursor: pointer; padding: 4px 6px;
      min-height: 32px; border-radius: 4px;
    }
    .refine-more-btn:hover, .refine-fewer:hover, .or-pick button:hover { background: rgb(var(--mp-ink-rgb) / 0.08); }
    .refine-more-btn:focus-visible, .refine-fewer:focus-visible, .or-pick button:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 1px; }
    .refine-more-btn:disabled { color: var(--mp-text-muted); cursor: default; }
    /* An opened bucket can hold thousands of values: it scrolls inside its own box instead of pushing the results down. */
    .refine-chips.open { max-height: min(40vh, 320px); overflow-y: auto; overscroll-behavior: contain; padding: 2px; }
    .refine-chips.open .refine-chip { content-visibility: auto; contain-intrinsic-size: auto 32px; }
    .refine-error { margin: 4px 0 0; font-size: 12px; color: var(--mp-warn); }
    .or-pick {
      display: flex; flex-wrap: wrap; align-items: center; gap: 4px 6px; margin: 0 0 6px; padding: 4px 6px; max-width: 100%; min-width: 0;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); border-radius: 8px; background: var(--mp-surface-high);
    }
    .or-pick-title { font-size: 12px; color: var(--mp-text-muted); }
    .or-pick-target { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; border: 1px solid rgb(var(--mp-ink-rgb) / 0.16) !important; }
    .or-pick-cancel { color: var(--mp-text-muted) !important; }
    /* The filter chips get their own row inside the field; a full row scrolls sideways instead of growing the field (D1 2.6). */
    .filter-chips { display: block; width: 100%; min-width: 0; }
    .search-text { display: block; width: 100%; min-width: 0; }
    .has-filters .search-text { margin-top: 6px; }
    :host ::ng-deep .filter-chips .mdc-evolution-chip-set__chips { flex-wrap: wrap; min-width: 0; max-width: 100%; }
    :host ::ng-deep .has-filters .filter-chips .mdc-evolution-chip-set__chips {
      flex-wrap: nowrap; overflow-x: auto; overscroll-behavior-x: contain; scrollbar-width: thin;
    }
    :host ::ng-deep .filter-chips .mat-mdc-chip-row { flex: none; max-width: min(260px, 80vw); }
    .filter-label { display: inline-block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle; }
    .unknown .filter-label { color: var(--mp-text-muted); font-style: italic; }
    .result-count { color: var(--mp-text-muted); margin: 16px 0; }
    .results-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
    .result-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgb(var(--mp-ink-rgb) / 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: var(--mp-text-faint); position: absolute; z-index: 0; }
    /* Folder-vs-archive kind badge (1.12.0): a small, legible corner marker so the
       node kind reads at a glance even when a real cover image is showing (the
       cover-fallback icon above is hidden then). Mirrors the compact circular-chip
       sizing already used for browse's list-view badges. */
    .kind-badge {
      position: absolute; top: 4px; left: 4px; z-index: 2;
      display: flex; align-items: center; justify-content: center;
      width: 20px; height: 20px; border-radius: 50%;
      background: rgb(var(--mp-shade-rgb) / 0.65); color: var(--mp-on-scrim);
    }
    .kind-badge mat-icon { font-size: 14px; width: 14px; height: 14px; line-height: 14px; }
    .result-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .series-matches { margin-bottom: 8px; }
    .section-heading { margin: 16px 0 12px; font-size: 16px; font-weight: 500; }
    .result-aka {
      margin-top: 2px; font-size: 12px; color: var(--mp-text-muted);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .no-results { color: var(--mp-text-muted); padding: 32px; text-align: center; }
    .search-hint { color: var(--mp-text-muted); font-size: 13px; margin: -8px 0 16px; overflow-wrap: anywhere; }
    .result-inside { margin-top: 2px; font-size: 12px; color: var(--mp-text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    /* Read marks: the browse card badge (library-browse.component.ts), top-right - the kind badge has top-left, the star bottom-right. */
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgb(var(--mp-accent-strong-rgb) / 0.9); color: var(--mp-on-accent);
    }
    .badge.read { background: rgb(var(--mp-success-strong-rgb) / 0.95); }
    .read-filter { display: block; margin: 0 0 8px; }
    .work-card { min-width: 0; }
    .work-link { display: block; text-decoration: none; color: inherit; }
    .crumbs { margin-top: 2px; min-width: 0; }
    .crumb-line {
      display: block; font-size: 12px; color: var(--mp-text-muted);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .locations-toggle {
      display: inline-flex; align-items: center; gap: 2px; margin-top: 2px; padding: 4px 0; min-height: 32px;
      border: none; background: none; color: var(--mp-accent); font: inherit; font-size: 12px; cursor: pointer;
    }
    .locations-toggle mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .locations { margin: 0; padding: 0; list-style: none; font-size: 12px; }
    .locations li { margin: 2px 0; min-width: 0; }
    .more-places { color: var(--mp-text-muted); padding: 4px 0; }
    .locations a { display: block; color: var(--mp-accent); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding: 4px 0; }
    .load-more { display: flex; justify-content: center; margin: 24px 0 8px; }
  `],
})
export class SearchComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** 1.49.0 (lane O): the quick results under the field. 1.50.0 (lane U): on every width - the header box steps aside on this page. */
  readonly quick = new QuickResults();
  readonly phone = toSignal(
    inject(BreakpointObserver).observe('(max-width: 599.98px)').pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  private readonly quickFocused = signal(false);
  readonly quickDismissed = signal(false);
  readonly quickOpen = computed(() => this.quickFocused() && !this.quickDismissed() && this.quick.entries().length > 0);
  readonly quickActiveId = computed(() => (this.quickOpen() && this.quick.activeEntry() ? `sq-${this.quick.activeEntry()!.key}` : null));
  readonly quickStatus = computed(() => {
    if (!this.quickOpen()) return '';
    const n = this.quick.entries().length;
    return `${n} quick result${n === 1 ? '' : 's'}${this.phone() ? '' : ', use the up and down arrow keys'}`;
  });
  private quickBlurTimer: ReturnType<typeof setTimeout> | null = null;

  /** The text field (two-way bound); it becomes `params().q` when the debounce fires. */
  query = '';
  /** The searched state, mirrored in the URL (1.45.0). */
  readonly params = signal<SearchParams>({ q: '', read: 'all', f: [] });
  readonly results = signal<CatalogNodeDto[]>([]);
  /** With a tag filter (1.45.0): the matching works, instead of `results`. */
  readonly works = signal<FacetWorkDto[]>([]);
  /** The filters as the server named them in its last answer. */
  private readonly resolvedFilters = signal<SearchFilterDto[]>([]);
  /** Works whose locations list is open. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly seriesMatches = signal<SeriesMatchDto[]>([]);
  readonly totalCount = signal(0);
  /** 1.49.0: the plain search's counts by kind (null when the server sent none) and its close (typo) matches. */
  readonly folderCount = signal<number | null>(null);
  readonly archiveCount = signal<number | null>(null);
  readonly closeMatchCount = signal(0);
  /** 1.49.0 "+N inside" per folder card, merged over the loaded pages. */
  readonly insideCounts = signal<Readonly<Record<string, number>>>({});
  readonly searched = signal(false);
  /** Keyset paging (1.45.0): whether a next page exists, and a "Load more" request is running. */
  readonly hasMore = signal(false);
  readonly loadingMore = signal(false);

  /** The read-state filter chips: the browse Show filter's choices and order. */
  readonly readOptions: { value: LibraryReadStateFilter; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'reading', label: 'Reading' },
    { value: 'read', label: 'Read' },
    { value: 'unread', label: 'Unread' },
  ];

  /** There is something to search for: text, a tag filter, or both. */
  readonly active = computed(() => this.params().q.trim().length > 0 || this.params().f.length > 0);

  /**
   * The tag view (1.45.0): a filter in the URL, or the last answer had work cards (typed `creator:` ... in the text). 1.47.0: the read
   * chips apply there too (the server reads a work as one folder of its visible locations).
   */
  readonly tagView = computed(() => this.params().f.length > 0 || this.works().length > 0);

  /** The field's filter chips, in URL order, named once the server has answered. */
  readonly filterChips = computed<FilterChip[]>(() => {
    const key = (id: string, negated: boolean) => `${negated ? '-' : ''}${id}`;
    const byId = new Map(this.resolvedFilters().filter((r) => r.source === 'Chip' && r.id).map((r) => [key(r.id!, !!r.negated), r]));
    return this.params().f.map((param) => {
      const excluded = isExcludeParam(param);
      const r = byId.get(key(filterValueId(param), excluded));
      if (r?.matched && r.display) {
        return { param, label: `${excluded ? 'Not ' : ''}${bucketLabel(r.bucket)}: ${r.display}`, known: true, excluded };
      }
      return { param, label: this.searched() ? 'Unknown tag' : '…', known: !this.searched(), excluded };
    });
  });

  /** 1.49.0: the counts line - "1 folder, 65 archives" (a zero kind is left out); "N results" when the server sent no counts. */
  readonly countText = computed(() => {
    const folders = this.folderCount();
    const archives = this.archiveCount();
    if (folders === null || archives === null) return `${this.totalCount()} results`;
    const parts: string[] = [];
    if (folders > 0) parts.push(`${folders} folder${folders === 1 ? '' : 's'}`);
    if (archives > 0 || parts.length === 0) parts.push(`${archives} archive${archives === 1 ? '' : 's'}`);
    return parts.join(', ');
  });

  /** 1.49.0: the typo note. Close matches always follow the exact hits; with no exact hit at all, they are the whole answer. */
  readonly closeMatchText = computed(() => {
    const close = this.closeMatchCount();
    if (close <= 0) return null;
    const q = this.params().q.trim();
    return close >= this.totalCount() ? `No exact matches. Showing close matches for "${q}".` : `Also showing close matches for "${q}".`;
  });

  /** 1.49.0: a search with no word of 3+ characters (matched by word starts, no typo tolerance). */
  readonly shortSearch = computed(() => {
    const q = this.params().q;
    if (!q.trim() || this.params().f.length > 0) return false;
    const words = q.normalize('NFC').split(/[^\p{L}\p{N}\p{M}]+/u).filter((w) => w.length > 0);
    return words.length > 0 && words.every((w) => w.normalize('NFD').replace(/\p{M}/gu, '').length < 3);
  });

  readonly noResultsText = computed(() => {
    // 1.49.0 (lane T): also a tag search made only of typed fields (`-genre:action`, `year:..1999`) answers in works.
    if (this.params().f.length > 0 || this.tagAnswer()) {
      return this.params().read === 'all' ? 'No works with these tags.' : 'No works with these tags and this filter.';
    }
    return this.params().read === 'all' ? 'No results found.' : 'No results with this filter.';
  });

  /** 1.49.0 (lane T): the last answer was a tag search (it carried `works`, even an empty list). */
  private readonly tagAnswer = signal(false);
  /** 1.49.0 (lane T): the tag view's Refine list (first page's answer). */
  readonly refine = signal<FacetBucketDto[]>([]);
  /** 1.50.0 (lane U): Refine buckets opened with "+N more" - the complete chip list per bucket (cleared by every new search). */
  readonly expandedChips = signal<Readonly<Record<string, FacetChipDto[]>>>({});
  /** The bucket whose full list is being fetched, and the one whose last fetch failed. */
  readonly expanding = signal<string | null>(null);
  readonly expandError = signal<string | null>(null);
  /** 1.50.0 (lane U): the "or" menu - a chip with two or more included tags in its bucket asks which one to join. */
  readonly orPick = signal<{ bucket: string; filter: string; display: string; targets: { param: string; label: string }[] } | null>(null);
  /** 1.49.0 (lane T): the "?" syntax panel. */
  readonly syntaxOpen = signal(false);
  readonly syntaxExamples = SYNTAX_EXAMPLES;

  /** Per-user opt-in (1.21.0): show the favorite star; 1.49.0 the server lifts favourites to the top across pages when it is on. */
  readonly prominence = signal(false);

  private readonly readFilterBox = viewChild(MatChipListbox);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private nextCursor: string | null = null;
  /** Series-match anchors of the current search: a normal hit that is one shows only in "Series matches", on every page. */
  private matchedIds = new Set<string>();
  /** Bumped by every new search, so a late answer to an older one (or to its "Load more") is dropped. */
  private generation = 0;

  constructor() {
    // Leaving the page (a result opened, the browser's Back) cancels the timers: a late fire would write this page's URL back.
    inject(DestroyRef).onDestroy(() => {
      if (this.searchTimer) clearTimeout(this.searchTimer);
      if (this.quickBlurTimer) clearTimeout(this.quickBlurTimer);
    });

    // A link change (identify dialog, admin menu) updates the result in place (1.27.0).
    inject(MetadataStateService).changed$.pipe(takeUntilDestroyed()).subscribe(({ nodeId, hasSeriesInfo }) => {
      const patch = (n: CatalogNodeDto) => (n.id === nodeId ? { ...n, hasSeriesInfo } : n);
      this.results.update((list) => list.map(patch));
      this.seriesMatches.update((list) => list.map((m) => ({ ...m, node: patch(m.node) })));
    });

    // Favorites search prominence is a per-user preference in the library-view blob.
    this.api.getLibraryPreferences().subscribe({
      next: (prefs) => this.prominence.set(prefs.favoritesSearchProminence ?? false),
      error: () => this.prominence.set(false),
    });

    // The URL is the page's state (1.45.0): a shared link, a reload or Back opens the same search. Our own URL writes come back here
    // with the state we already hold and are ignored.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((map) => {
      const next = searchParamsFromQuery(map);
      if (sameParams(next, this.params())) return;
      if (this.searchTimer) clearTimeout(this.searchTimer);
      this.query = next.q;
      this.params.set(next);
      this.runSearch();
    });
  }

  onQuickFocus(): void {
    if (this.quickBlurTimer) clearTimeout(this.quickBlurTimer);
    this.quickFocused.set(true);
  }

  onQuickBlur(): void {
    // A tap on a result is handled before the list goes (it never takes focus; this is the margin for touch).
    this.quickBlurTimer = setTimeout(() => this.quickFocused.set(false), 120);
  }

  /**
   * 1.50.0 (lane U): the keyboard on the field - Up / Down move through the quick results, Enter opens the chosen one (with none chosen it
   * only closes the list: the search itself runs from the typed text), Escape closes the list, Tab leaves it.
   */
  onQuickKey(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        if (this.quick.entries().length === 0) return;
        if (this.quickDismissed()) this.quickDismissed.set(false);
        event.preventDefault();
        this.quick.move(event.key === 'ArrowDown' ? 1 : -1);
        break;
      case 'Enter': {
        const entry = this.quickOpen() ? this.quick.activeEntry() : null;
        if (entry) {
          event.preventDefault();
          this.pickQuick(entry);
        } else {
          this.quickDismissed.set(true);
        }
        break;
      }
      case 'Escape':
        if (this.quickOpen()) {
          event.preventDefault();
          this.quickDismissed.set(true);
          this.quick.active.set(-1);
        }
        break;
      case 'Tab':
        this.quickDismissed.set(true);
        break;
    }
  }

  /** 1.49.0 (lane O): a quick result was chosen - open it, add its tag view, or complete the typed field's token. */
  pickQuick(entry: QuickEntry): void {
    const action = entry.action;
    if (action.type === 'complete') {
      this.query = completeToken(this.query, action.start, action.insert);
      this.onSearch();
      return;
    }
    this.quick.clear();
    if (action.type === 'open') {
      // The page is being left: a search still waiting on its 300 ms must not write this page's URL back over the navigation.
      if (this.searchTimer) clearTimeout(this.searchTimer);
      void this.router.navigate(action.commands);
      return;
    }
    this.query = '';
    this.setFilters(addFilterParam(this.params().f, action.filter));
  }

  onSearch(): void {
    this.quickDismissed.set(false);
    this.quick.ask(this.query);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.params.update((p) => ({ ...p, q: this.query }));
      this.writeUrl();
      this.runSearch();
    }, 300);
  }

  /**
   * A filter chip was clicked (1.45.0). Clicking the selected chip again would leave the row with no choice: it is selected again
   * instead (one choice is always shown - and announced - as selected).
   */
  onReadChip(value: LibraryReadStateFilter | null | undefined): void {
    if (value == null) {
      const box = this.readFilterBox();
      if (box) box.value = this.params().read;
      return;
    }
    this.setReadFilter(value);
  }

  /** Removes one tag filter (its chip's remove button, or Backspace in an empty field) and searches again. */
  removeFilter(param: string): void {
    if (!this.params().f.includes(param)) return;
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.params.update((p) => ({ ...p, q: this.query, f: p.f.filter((v) => v !== param) }));
    this.writeUrl();
    this.runSearch();
  }

  /** 1.49.0 (lane T): a filter chip's include / exclude toggle. */
  toggleExclude(param: string): void {
    if (!this.params().f.includes(param)) return;
    const flipped = toggleExcludeParam(param);
    this.setFilters(this.params().f.map((v) => (v === param ? flipped : v)));
  }

  /**
   * 1.49.0 (lane T): a Refine chip - `all` adds it (must carry), `not` adds it as an exclusion, `any` joins it to an included tag of its
   * bucket in the search (`genre:fv_a|fv_b`: either). 1.50.0 (lane U): `target` names which included tag to join (the "or" menu); without
   * it the only one is joined (with several, the caller asks first - see {@link onOr}).
   */
  addRefine(filter: string, mode: 'all' | 'any' | 'not', target?: string): void {
    const f = this.params().f;
    if (mode === 'any') {
      const joinTo = target ?? this.orTargets(filter)[0];
      if (joinTo && f.includes(joinTo)) {
        const id = filterValueId(filter);
        this.setFilters(f.map((v) => (v === joinTo ? `${v}|${id}` : v)));
        return;
      }
    }
    this.setFilters(addFilterParam(f, mode === 'not' ? `-${filter}` : filter));
  }

  /** 1.49.0: whether a Refine chip can join an included tag of its bucket already in the search ("or"). */
  canOr(filter: string): boolean {
    return this.orTargets(filter).length > 0;
  }

  /**
   * 1.50.0 (lane U): the "or" button of a Refine chip. One included tag in the bucket: it is joined at once. Two or more: a small menu
   * under the bucket's title asks which one (named as its filter chip in the field is).
   */
  onOr(chip: FacetChipDto, bucket: string): void {
    const targets = this.orTargets(chip.filter);
    if (targets.length <= 1) {
      this.orPick.set(null);
      this.addRefine(chip.filter, 'any');
      return;
    }
    const labels = new Map(this.filterChips().map((c) => [c.param, c.label]));
    this.orPick.set({
      bucket,
      filter: chip.filter,
      display: chip.display,
      targets: targets.map((param) => ({ param, label: labels.get(param) ?? 'Tag' })),
    });
    // The menu opens under the bucket's title: its first choice takes the focus (Escape closes it).
    setTimeout(() => (document.querySelector('[data-testid="refine-or-target"]') as HTMLElement | null)?.focus());
  }

  /** The "or" menu's choice. */
  chooseOr(target: string): void {
    const pick = this.orPick();
    this.orPick.set(null);
    if (pick) this.addRefine(pick.filter, 'any', target);
  }

  /** 1.50.0 (lane U): a bucket's chips as shown - the whole list once "+N more" opened it, else the server's top few. */
  shownChips(bucket: FacetBucketDto): FacetChipDto[] {
    return this.expandedChips()[bucket.bucket] ?? bucket.chips;
  }

  /**
   * 1.50.0 (lane U): "+N more" - asks the server for the same search with the bucket complete (`expand=<token>`, a one-work page: only
   * the Refine list is wanted) and shows every chip in place. Dropped when a newer search started meanwhile.
   */
  expandBucket(bucket: FacetBucketDto['bucket']): void {
    if (this.expanding()) return;
    const { q, read, f } = this.params();
    const generation = this.generation;
    this.expanding.set(bucket);
    this.expandError.set(null);
    this.api.search(q, { readState: read, pageSize: 1, expand: bucketToken(bucket), ...filterOption(f) }).subscribe({
      next: (answer) => {
        if (generation !== this.generation) return;
        const full = (answer as SearchAnswer).refine?.find((b) => b.bucket === bucket);
        if (full) this.expandedChips.update((map) => ({ ...map, [bucket]: full.chips }));
        else this.expandError.set(bucket);
        this.expanding.set(null);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.expanding.set(null);
        this.expandError.set(bucket);
      },
    });
  }

  /** 1.50.0 (lane U): "Show fewer" - the bucket goes back to its top few. */
  collapseBucket(bucket: FacetBucketDto['bucket']): void {
    this.expandedChips.update((map) => {
      const next = { ...map };
      delete next[bucket];
      return next;
    });
  }

  label(bucket: FacetBucketDto['bucket']): string {
    return bucketLabel(bucket);
  }

  /** 1.49.0 (S4): an example of the "?" panel goes into the field (added to what is typed) and searches. */
  useExample(example: string): void {
    this.query = this.query.trim() ? `${this.query.trim()} ${example}` : example;
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.params.update((p) => ({ ...p, q: this.query }));
    this.writeUrl();
    this.runSearch();
  }

  toggleLocations(workId: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(workId)) next.delete(workId);
      else next.add(workId);
      return next;
    });
  }

  /** A work card has no id of its own (lane Q): its first location and its place in the list name it. */
  workKey(work: FacetWorkDto, index: number): string {
    return `${work.node.id}:${index}`;
  }

  /**
   * Where a location opens: the first is the card's own node (a folder in browse, an archive in the reader); the others are the further
   * folders of a split series (lane Q sends their ids and names, not their kind), opened in browse.
   */
  locationLink(work: FacetWorkDto, loc: FacetLocationDto): string[] {
    return loc.nodeId === work.node.id ? this.getNodeLink(work.node) : ['/libraries', loc.libraryId, 'browse', loc.nodeId];
  }

  /** "Library › Folder › Work": the location's place as display names (never a path). */
  trailText(loc: FacetLocationDto): string {
    return locationText(loc);
  }

  /** Sets the read-state filter and runs the search again from its first page. */
  setReadFilter(read: LibraryReadStateFilter): void {
    if (read === this.params().read) return;
    this.params.update((p) => ({ ...p, read }));
    this.writeUrl();
    this.runSearch();
  }

  /** "Load more" (1.45.0): the page after the last result, same text and filter, appended. */
  loadMore(): void {
    const cursor = this.nextCursor;
    if (!cursor || this.loadingMore()) return;
    const { q, read, f } = this.params();
    const generation = this.generation;
    this.loadingMore.set(true);
    this.api.search(q, { cursor, readState: read, ...filterOption(f) }).subscribe({
      next: (answer) => {
        if (generation !== this.generation) return;
        const response = answer as SearchAnswer;
        if (response.works) {
          this.works.update((list) => [...list, ...response.works!]);
          this.takePaging(response);
          this.loadingMore.set(false);
          return;
        }
        const shown = new Set(this.results().map((n) => n.id));
        const rest = response.items.filter((n) => !this.matchedIds.has(n.id) && !shown.has(n.id));
        const anchors = response.items.filter((n) => this.matchedIds.has(n.id));
        this.results.update((list) => [...list, ...rest]);
        this.totalCount.update((total) => total - anchors.length);
        this.dropAnchorCounts(anchors);
        this.insideCounts.update((map) => ({ ...map, ...(response.insideCounts ?? {}) }));
        this.takePaging(response);
        this.loadingMore.set(false);
      },
      error: () => {
        if (generation === this.generation) this.loadingMore.set(false);
      },
    });
  }

  getNodeLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') {
      return ['/libraries', node.libraryId, 'browse', node.id];
    }
    return ['/reader', node.id];
  }

  /**
   * Resolved cover URL for a search result. Folders use the backend-provided
   * `coverUrl` (resolved from the first descendant archive by SortKey — parity
   * with browse, added for search in 1.8.0); archives fall back to their own
   * cover endpoint (the search projection does not set coverUrl for archives).
   * Folders with no readable descendant stay coverless and render the icon.
   */
  coverSrc(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }

  /** Material-symbol icon for a result's kind (1.12.0) - matches the browse grid's iconography. */
  kindIcon(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'folder' : 'menu_book';
  }

  /** Accessible label for the folder/archive kind badge (1.12.0). */
  kindLabel(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'Folder' : 'Archive';
  }

  /** The kind counts without the series-match anchors that are shown in their own row (1.49.0). */
  private dropAnchorCounts(anchors: CatalogNodeDto[]): void {
    const folders = anchors.filter((n) => n.kind === 'Folder').length;
    if (this.folderCount() !== null) this.folderCount.update((n) => Math.max(0, n! - folders));
    if (this.archiveCount() !== null) this.archiveCount.update((n) => Math.max(0, n! - (anchors.length - folders)));
  }

  /** Takes a first page's plain-search counts (1.49.0). */
  private takeCounts(response: SearchResultsDto): void {
    this.folderCount.set(response.folderCount ?? null);
    this.archiveCount.set(response.archiveCount ?? null);
    this.closeMatchCount.set(response.closeMatchCount ?? 0);
    this.insideCounts.set(response.insideCounts ?? {});
  }

  /** 1.50.0: the included filters of the chip's bucket that "or" can join it to (in URL order; none when it is already in one). */
  orTargets(filter: string): string[] {
    const token = filterToken(filter);
    const id = filterValueId(filter);
    return this.params().f.filter((v) => !isExcludeParam(v) && filterToken(v) === token && !filterValueId(v).split('|').includes(id));
  }

  /** Replaces the tag filters (keeping the typed text) and searches again from the first page. */
  private setFilters(f: string[]): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.params.update((p) => ({ ...p, q: this.query, f }));
    this.writeUrl();
    this.runSearch();
  }

  private writeUrl(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: searchParamsToQuery(this.params()), replaceUrl: true });
  }

  /**
   * The filters as the server resolved them. A merged creator's old value answers with its target and `redirectedFrom`
   * (D1 2.2): the URL then names the target, so a bookmark keeps working and the field shows the creator it opens.
   */
  private takeFilters(filters: SearchFilterDto[]): void {
    this.resolvedFilters.set(filters);
    const to = new Map<string, string>();
    for (const r of filters) {
      const target = r.valueIds[0];
      if (r.source === 'Chip' && r.matched && r.redirectedFrom && target && target !== r.redirectedFrom) {
        to.set(r.redirectedFrom, `${bucketToken(r.bucket)}:${target}`);
      }
    }
    if (to.size === 0) return;
    // 1.49.0: an excluded old id stays excluded under its new id.
    const redirect = (v: string) => {
      const target = to.get(filterValueId(v));
      return target ? `${isExcludeParam(v) ? '-' : ''}${target}` : v;
    };
    this.params.update((p) => ({ ...p, f: [...new Set(p.f.map(redirect))] }));
    // The answer is for the old id: the new id names the same value, so the chips keep their names.
    this.resolvedFilters.set(filters.map((r) => (r.redirectedFrom && to.has(r.redirectedFrom) ? { ...r, id: r.valueIds[0] } : r)));
    this.writeUrl();
  }

  private takePaging(response: SearchResultsDto): void {
    this.nextCursor = response.hasMore ? (response.nextCursor ?? null) : null;
    this.hasMore.set(this.nextCursor !== null);
  }

  /** The first page for `params()`. */
  private runSearch(): void {
    const generation = ++this.generation;
    const { q, read, f } = this.params();
    this.nextCursor = null;
    this.hasMore.set(false);
    this.loadingMore.set(false);
    this.expanded.set(new Set());
    this.expandedChips.set({});
    this.expanding.set(null);
    this.expandError.set(null);
    this.orPick.set(null);
    if (!q.trim() && f.length === 0) {
      this.refine.set([]);
      this.tagAnswer.set(false);
      this.results.set([]);
      this.works.set([]);
      this.seriesMatches.set([]);
      this.resolvedFilters.set([]);
      this.totalCount.set(0);
      this.takeCounts({ query: '', items: [], totalCount: 0, nextCursor: null, hasMore: false });
      this.searched.set(false);
      return;
    }
    // Until the answer names the filters, their chips show a placeholder (not "Unknown tag").
    this.searched.set(false);

    this.api.search(q, { readState: read, ...filterOption(f) }).subscribe({
      next: (answer) => {
        if (generation !== this.generation) return;
        const response = answer as SearchAnswer;
        this.takeFilters(response.filters ?? []);
        this.refine.set([...(response.refine ?? [])].sort((a, b) => bucketRank(a.bucket) - bucketRank(b.bucket)));
        this.tagAnswer.set(!!response.works);
        if (response.works) {
          this.works.set(response.works);
          this.takeCounts({ query: '', items: [], totalCount: 0, nextCursor: null, hasMore: false });
          this.results.set([]);
          this.seriesMatches.set([]);
          this.matchedIds = new Set();
          this.totalCount.set(response.totalCount);
          this.takePaging(response);
          this.searched.set(true);
          return;
        }
        this.works.set([]);
        // An anchor that is also a normal hit shows once, in "Series matches".
        const matches = response.seriesMatches ?? [];
        this.matchedIds = new Set(matches.map((m) => m.node.id));
        const rest = response.items.filter((n) => !this.matchedIds.has(n.id));
        this.seriesMatches.set(matches);
        this.results.set(rest);
        this.totalCount.set(response.totalCount - (response.items.length - rest.length));
        this.takeCounts(response);
        this.dropAnchorCounts(response.items.filter((n) => this.matchedIds.has(n.id)));
        this.takePaging(response);
        this.searched.set(true);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.takeCounts({ query: '', items: [], totalCount: 0, nextCursor: null, hasMore: false });
        this.results.set([]);
        this.works.set([]);
        this.refine.set([]);
        this.tagAnswer.set(false);
        this.seriesMatches.set([]);
        this.totalCount.set(0);
        this.searched.set(true);
      },
    });
  }
}

/** The `filters` search option, only when there are filters (a plain search sends exactly what it sent before). */
function filterOption(f: readonly string[]): { filters?: readonly string[] } {
  return f.length > 0 ? { filters: f } : {};
}
