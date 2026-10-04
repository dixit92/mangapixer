import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable } from 'rxjs';

import {
  ApiError,
  IdentifyCandidateDto,
  IdentifyContextDto,
  IdentifyPreviewDto,
  IdentifySiteDto,
  MetadataMatchMethod,
  NodeSeriesLinkChangeDto,
  NodeSeriesLinkDto,
} from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { MetadataStateService } from '../metadata-state.service';
import { creditGroups } from '../series-info-labels';
import { collectionResultMessage } from '../collection-labels';
import { IdentifyDialogData, IdentifyDialogResult } from './identify-dialog.service';
import { STRENGTH_LABELS, candidateLine, previewLine, retryLabel, scorePercent, tallStripsLabel } from './identify-labels';

type Step = 'search' | 'preview';

/**
 * Admin identify dialog (1.24.0, lane B2) - the design lock's three steps:
 * 1. search text (prefilled from suggestions, editable, sent ONLY on Search) or a pasted
 *    MangaUpdates URL / `mu:` shortcode (parsed by the server, only the id is sent), or
 *    "Use it" when ComicInfo already points to a series;
 * 2. ranked results with server-proxied thumbnails;
 * 3. preview beside the local folder, warnings, then Link (with Undo).
 * "Hide doujinshi & novels" (on by default, 1.24.0 polish; off below a folder whose Content
 * is "Doujinshi & adult one-shots", 1.26.0) adds a FIXED type filter to the search; it
 * carries no user data. Link and Undo are announced through
 * `MetadataStateService`, so the card (i) and the top-bar button update in place.
 * When the switches are off it shows why and makes no call. Every request goes to
 * MangaPixer; the browser never contacts a provider.
 * 1.32.0: "Search on: MangaUpdates | Grand Comics Database" - the server picks GCD first for a
 * comics-signalled folder; GCD searches may carry the (YYYY) of the folder name ("Only series
 * that began in ..."), and its data is shown with its CC BY-SA credit.
 * 1.34.0, `mode: 'collection'`: "Collection about" - the admin picks the series a folder of works is ABOUT (fan works); the action
 * marks the folder instead of linking it, by default also sets its Content to "Doujinshi & adult one-shots" (so the works inside can
 * find their doujinshi records), and says how many works inside will be matched. Doujinshi stay hidden by default: it is the series.
 */
@Component({
  selector: 'app-identify-dialog',
  standalone: true,
  imports: [
    FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatProgressSpinnerModule, MatTooltipModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      @if (collectionMode) {
        <h2 mat-dialog-title class="title" [title]="context()?.displayName ?? ''" data-testid="identify-collection-title">
          Collection about…: “{{ context()?.displayName ?? '…' }}” - pick the series</h2>
      } @else {
        <h2 mat-dialog-title class="title" [title]="context()?.displayName ?? ''">Identify “{{ context()?.displayName ?? '…' }}”</h2>
      }
      <button mat-icon-button mat-dialog-close aria-label="Close" data-testid="identify-close"><mat-icon>close</mat-icon></button>
    </div>
    <mat-dialog-content class="body">
      @if (loading()) {
        <div class="center"><mat-spinner diameter="28" /></div>
      } @else if (context(); as ctx) {
        @if (!anyAvailable()) {
          <div class="unavailable" role="status" data-testid="identify-unavailable">
            <mat-icon>cloud_off</mat-icon>
            <div>
              <p>{{ ctx.unavailableMessage }}</p>
              @if (ctx.unavailableCode !== 'metadata_network_disabled') {
                <a mat-stroked-button routerLink="/admin" mat-dialog-close>Open Admin settings</a>
              }
            </div>
          </div>
        } @else if (step() === 'search') {
          @if ((ctx.sites?.length ?? 0) > 1) {
            <div class="sites">
              <span class="muted">Search on:</span>
              <mat-button-toggle-group [value]="site()" (change)="chooseSite($event.value)" hideSingleSelectionIndicator
                                       aria-label="Search on" data-testid="identify-site">
                @for (s of ctx.sites; track s.id) {
                  <mat-button-toggle [value]="s.id" [disabled]="!s.available" [attr.data-site]="s.id"
                                     [matTooltip]="s.available ? '' : siteUnavailable(s)">{{ s.name }}</mat-button-toggle>
                }
              </mat-button-toggle-group>
            </div>
            @if (siteNote(); as note) { <p class="note" data-testid="identify-site-note"><mat-icon inline>schedule</mat-icon> {{ note }}</p> }
          }
          <form class="row" (ngSubmit)="runSearch()">
            <mat-form-field appearance="outline" class="grow" subscriptSizing="dynamic">
              <mat-label>Search {{ siteName() }}</mat-label>
              <input matInput name="query" [ngModel]="query()" (ngModelChange)="query.set($event)" maxlength="200"
                     autocomplete="off" data-testid="identify-query">
            </mat-form-field>
            <button mat-flat-button type="submit" [disabled]="busy() || !queryValid()" data-testid="identify-search">Search</button>
          </form>
          @if (ctx.suggestions?.length) {
            <div class="suggestions">
              <span class="muted">Suggestions:</span>
              @for (s of ctx.suggestions; track s) {
                <button mat-stroked-button type="button" class="chip" [title]="s" (click)="query.set(s)"><span class="chip-text">{{ s }}</span></button>
              }
            </div>
          }
          @if (site() === 'gcd') {
            @if (ctx.local.yearHint; as year) {
              <mat-checkbox class="hide-types" [checked]="useStartYear()" (change)="useStartYear.set($event.checked)"
                            data-testid="identify-start-year">Only series that began in {{ year }}</mat-checkbox>
            }
          } @else {
            <mat-checkbox class="hide-types" [checked]="hideDoujinshi()" (change)="hideDoujinshi.set($event.checked)"
                          data-testid="identify-hide-doujinshi">Hide doujinshi &amp; novels</mat-checkbox>
          }
          <p class="note"><mat-icon inline>info_outline</mat-icon>
            The search text{{ site() === 'gcd' && ctx.local.yearHint && useStartYear() ? ' and the start year' : '' }} is sent to
            {{ siteName() }}. Nothing else about your library is.</p>

          <form class="row" (ngSubmit)="runLookup()">
            <mat-form-field appearance="outline" class="grow" subscriptSizing="dynamic">
              <mat-label>{{ pasteLabel() }}</mat-label>
              <input matInput name="reference" [ngModel]="reference()" (ngModelChange)="reference.set($event)" maxlength="512"
                     autocomplete="off" data-testid="identify-reference">
            </mat-form-field>
            <button mat-stroked-button type="submit" [disabled]="busy() || !reference().trim()" data-testid="identify-lookup">Look up</button>
          </form>
          @if (ctx.comicInfoHint; as hint) {
            <button mat-stroked-button type="button" class="hint" (click)="usePreview(hint.provider, hint.externalId, 'ComicInfoWebHint')" data-testid="identify-hint">
              <mat-icon>description</mat-icon> ComicInfo points to a {{ nameOf(hint.provider) }} series – Use it
            </button>
          }

          @if (candidates().length) {
            <div class="results-head">
              <span>Results ({{ totalHits() }})</span>
              <span class="muted">budget: {{ budgetUsed() }}/{{ budgetLimit() }} today</span>
            </div>
            <ul class="results" data-testid="identify-results">
              @for (c of candidates(); track c.externalId) {
                <li>
                  @if (c.imageToken && !failedImages().has(c.imageToken)) {
                    <img class="thumb" [src]="imageUrl(c.imageToken)" alt="" loading="lazy" (error)="imageFailed(c.imageToken)">
                  } @else {
                    <div class="thumb empty"><mat-icon>image_not_supported</mat-icon></div>
                  }
                  <div class="grow">
                    <div class="c-title">{{ c.title }}</div>
                    <div class="muted">{{ line(c) }}</div>
                    @if (c.hitTitle) { <div class="muted small">matched as “{{ c.hitTitle }}”</div> }
                    @if (c.format === 'Novel' || c.format === 'Artbook') { <div class="warn small">{{ c.format === 'Novel' ? 'Novel' : 'Artbook' }}, not a comic</div> }
                  </div>
                  <span class="strength" [attr.data-strength]="c.strength" matTooltip="Title match score">
                    {{ strength(c) }} {{ percent(c.score) }}%</span>
                  <button mat-stroked-button type="button" [disabled]="busy()" (click)="usePreview(resultsSite(), c.externalId, 'Search', c)">Preview</button>
                </li>
              }
            </ul>
            @if (candidates().length < totalHits()) {
              <button mat-button type="button" [disabled]="busy()" (click)="moreResults()">More results</button>
            }
          } @else if (searched() && !busy()) {
            <p class="muted" data-testid="identify-no-results">No results. Try another spelling, the original title, or paste the series URL.
              @if (resultsSite() === 'gcd') { The Grand Comics Database finds names spelled exactly as it writes them: try a shorter part of the name (without “ - ” or “:”). }</p>
          } @else {
            <p class="muted small">Budget: {{ budgetUsed() }}/{{ budgetLimit() }} requests today.</p>
          }
        } @else if (preview(); as p) {
          <div class="compare">
            <section>
              <h3>Your {{ ctx.nodeKind === 'Folder' ? 'folder' : 'item' }}</h3>
              <div class="pv">
                @if (ctx.local.coverUrl && !localCoverFailed()) {
                  <!-- The local thumbnail next to the record's cover, to compare them (owner, 1.26.x). -->
                  <img class="poster" [src]="ctx.local.coverUrl" alt="" (error)="localCoverFailed.set(true)" data-testid="identify-local-cover">
                }
                <div>
                  <div class="c-title">{{ ctx.local.displayName }}</div>
                  <div class="muted">{{ ctx.local.itemCount }} item{{ ctx.local.itemCount === 1 ? '' : 's' }}</div>
                  @if (ctx.local.comicInfoSeries) { <div class="muted">ComicInfo: “{{ ctx.local.comicInfoSeries }}”</div> }
                  <div class="muted">Pages: {{ tall(ctx.local.tallStrips) }}</div>
                </div>
              </div>
            </section>
            <section>
              <h3>{{ p.providerName }}</h3>
              <div class="pv">
                @if (p.imageToken && !failedImages().has(p.imageToken)) {
                  <img class="poster" [src]="imageUrl(p.imageToken)" alt="" (error)="imageFailed(p.imageToken)">
                }
                <div>
                  <div class="c-title">{{ p.title }}</div>
                  @if (p.altTitles?.length) {
                    <!-- The titles the record is known by: what an admin compares with the folder name (owner, 1.26.x). -->
                    <div class="alt small" data-testid="identify-alt-titles">also: {{ shownAltTitles(p).join(', ') }}
                      @if (!altTitlesExpanded() && p.altTitles!.length > ALT_TITLE_LIMIT) {
                        <button type="button" class="more-alts" (click)="altTitlesExpanded.set(true)"
                                data-testid="identify-alt-more">+{{ p.altTitles!.length - ALT_TITLE_LIMIT }} more</button>
                      }
                    </div>
                  }
                  <div class="muted">{{ pLine(p) }}</div>
                  @if (p.webtoon) { <div class="muted" data-testid="identify-webtoon">{{ p.providerName }}: webtoon</div> }
                  @for (g of credits(p); track g.label) { <div class="small">{{ g.label }}: {{ g.names.join(', ') }}</div> }
                  @if (p.genres?.length) { <div class="small muted">{{ p.genres!.slice(0, 6).join(', ') }}</div> }
                </div>
              </div>
            </section>
          </div>
          @if (p.description) { <p class="desc">{{ p.description }}</p> }
          @for (w of p.warnings ?? []; track w.code) {
            <p class="warn" [attr.data-warning]="w.code"><mat-icon inline>warning_amber</mat-icon> {{ w.message }}</p>
          }
          <p class="muted small" matTooltip="Title similarity only - not adjusted for item count, year, type or origin">
            Title match: {{ STRENGTH[match().strength] }} {{ percent(match().score) }}% · Applies to
            {{ ctx.nodeKind === 'Folder' ? 'this folder and everything inside' : 'this item only' }}.
          </p>
          @if (p.siteUrl) {
            <p class="small">Series data from
              <a [href]="p.siteUrl" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">{{ p.providerName }}</a></p>
          }
          @if (p.credit) {
            <!-- CC BY-SA 4.0 (Grand Comics Database, 1.32.0): credit and a link back to the series page. -->
            <p class="small muted" data-testid="identify-credit">{{ p.credit }}@if (p.provider === 'gcd') {. The cover thumbnail is shown only to help you choose; the folder keeps its own cover.}</p>
          }
        }
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (busy()) { <mat-spinner diameter="20" /> }
      @if (step() === 'preview' && preview()) {
        @if (collectionMode && !context()?.doujinshiContent) {
          <mat-checkbox class="content-box" [checked]="setDoujinContent()" (change)="setDoujinContent.set($event.checked)"
                        data-testid="identify-collection-content">
            Also set Content: Doujinshi &amp; adult one-shots (so the works inside are searched as doujinshi)
          </mat-checkbox>
        }
        <button mat-button type="button" [disabled]="busy()" (click)="back()">Back</button>
        @if (collectionMode) {
          <button mat-flat-button type="button" [disabled]="busy()" (click)="setCollection()" data-testid="identify-set-collection">Set as collection</button>
        } @else {
          <button mat-flat-button type="button" [disabled]="busy()" (click)="link()" data-testid="identify-link">Link</button>
        }
      } @else {
        <button mat-button mat-dialog-close type="button">Cancel</button>
      }
    </mat-dialog-actions>
  `,
  styles: [`
    .head { display: flex; align-items: center; justify-content: space-between; padding-right: 8px; }
    .content-box { flex: 1 1 100%; font-size: 12px; }
    .title { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .head button { flex: none; }
    /* A long archive name never widens the dialog: long words wrap, chips ellipsize. */
    .body { min-height: 180px; overflow-x: hidden; overflow-wrap: anywhere; }
    .center { display: flex; justify-content: center; padding: 32px; }
    .row { display: flex; gap: 8px; align-items: center; margin-top: 8px; }
    .grow { flex: 1; min-width: 0; }
    .suggestions { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: 6px 0; }
    .chip { font-size: 12px; line-height: 26px; max-width: 100%; min-width: 0; }
    .chip-text { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .hide-types { display: block; margin: 0 0 0 -8px; }
    .sites { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin: 4px 0; }
    .sites mat-button-toggle-group { max-width: 100%; }
    .note { color: #9a9aa8; font-size: 12px; margin: 4px 0 8px; }
    .muted { color: #9a9aa8; }
    .small { font-size: 12px; }
    .alt { color: #b8b8c4; margin: 2px 0 4px; overflow-wrap: anywhere; }
    .more-alts { background: none; border: none; padding: 0 0 0 4px; font: inherit; color: #b39dff; cursor: pointer; text-decoration: underline; }
    .warn { color: #ffb300; font-size: 13px; }
    .error { color: #f44336; }
    .hint { margin: 8px 0; }
    .unavailable { display: flex; gap: 12px; align-items: flex-start; padding: 16px 0; }
    .results-head { display: flex; justify-content: space-between; margin: 12px 0 4px; font-weight: 500; }
    .results { list-style: none; margin: 0; padding: 0; }
    .results li { display: flex; gap: 10px; align-items: center; padding: 6px 0; border-bottom: 1px solid rgba(255,255,255,0.08); }
    .thumb { width: 42px; height: 60px; object-fit: cover; border-radius: 3px; flex: none; background: rgba(255,255,255,0.06); }
    .thumb.empty { display: flex; align-items: center; justify-content: center; color: #666; }
    .c-title { font-weight: 500; }
    .strength { font-size: 12px; white-space: nowrap; color: #9a9aa8; }
    .strength[data-strength="Strong"] { color: #4caf50; }
    .strength[data-strength="Possible"] { color: #ffb300; }
    .compare { display: grid; grid-template-columns: 1fr 1.4fr; gap: 16px; }
    .compare h3 { margin: 4px 0; font-size: 13px; text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99; }
    .pv { display: flex; gap: 10px; }
    .poster { width: 84px; height: 120px; object-fit: cover; border-radius: 4px; flex: none; }
    .desc { white-space: pre-line; max-height: 9em; overflow: auto; font-size: 13px; }
    @media (max-width: 599.98px) {
      .compare { grid-template-columns: 1fr; }
      .results li { flex-wrap: wrap; }
    }
  `],
})
export class IdentifyDialogComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly dialogRef = inject<MatDialogRef<IdentifyDialogComponent, IdentifyDialogResult>>(MatDialogRef);
  private readonly snackBar = inject(MatSnackBar);
  private readonly metadataState = inject(MetadataStateService);
  private readonly data = inject<IdentifyDialogData>(MAT_DIALOG_DATA);

  readonly STRENGTH = STRENGTH_LABELS;

  /** 1.34.0: "pick the series these works are about" - the action marks the folder "Collection about" it. */
  readonly collectionMode = this.data.mode === 'collection';
  /** Collection mode: also set the folder's Content to "Doujinshi & adult one-shots" (on by default). */
  readonly setDoujinContent = signal(true);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly context = signal<IdentifyContextDto | null>(null);
  readonly step = signal<Step>('search');
  readonly query = signal('');
  readonly reference = signal('');
  /** "Hide doujinshi & novels": on by default, off in a doujinshi folder; applies to the next Search. */
  readonly hideDoujinshi = signal(true);
  /** The site searched (1.32.0): the server's choice first (GCD for a comics-signalled folder), then the admin's. */
  readonly site = signal('mangaupdates');
  /** The site the shown results came from (Preview asks that one). */
  readonly resultsSite = signal('mangaupdates');
  /** GCD: narrow to series that began in the year of the folder name (on by default when the name has one). */
  readonly useStartYear = signal(true);
  readonly searched = signal(false);
  readonly candidates = signal<IdentifyCandidateDto[]>([]);
  readonly totalHits = signal(0);
  readonly page = signal(1);
  readonly budgetUsed = signal(0);
  readonly budgetLimit = signal(0);
  readonly preview = signal<IdentifyPreviewDto | null>(null);
  /** Alternative titles shown before "+N more" in the preview. */
  readonly ALT_TITLE_LIMIT = 6;
  readonly altTitlesExpanded = signal(false);
  readonly localCoverFailed = signal(false);
  private readonly previewMethod = signal<MetadataMatchMethod>('Search');
  /** The search result a preview came from: its score (against the confirmed query) is the one shown and stored. */
  private readonly previewCandidate = signal<IdentifyCandidateDto | null>(null);
  /** Candidate image tokens whose image did not load (budget, backoff, expired): shown as a placeholder. */
  readonly failedImages = signal<ReadonlySet<string>>(new Set());

  /** Match strength shown in the preview and stored with the link. */
  readonly match = computed(() => {
    const c = this.previewCandidate();
    const p = this.preview();
    return c ? { score: c.score, strength: c.strength } : { score: p?.score ?? 0, strength: p?.strength ?? 'Weak' };
  });
  private readonly lastSubmittedQuery = signal('');
  private readonly lastSubmittedHide = signal(true);

  /** Any site can be searched (the server's default site may be off while the other is on). */
  readonly anyAvailable = computed(() => {
    const ctx = this.context();
    return !!ctx && (ctx.fetchAvailable || (ctx.sites ?? []).some((x) => x.available));
  });

  readonly siteName = computed(() => this.nameOf(this.site()));

  readonly siteNote = computed(() => this.context()?.sites?.find((x) => x.id === this.site())?.note ?? null);

  readonly pasteLabel = computed(() => {
    const sites = this.context()?.sites ?? [];
    return sites.some((x) => x.id === 'gcd')
      ? 'or paste a MangaUpdates or comics.org series URL, or mu:12345'
      : `or paste a ${this.context()?.providerName ?? 'MangaUpdates'} URL, or mu:12345`;
  });

  readonly queryValid = computed(() => {
    const q = this.query().trim();
    return q.length > 0 && q.length <= 200;
  });

  ngOnInit(): void {
    this.api.getIdentifyContext(this.data.nodeId).subscribe({
      next: (ctx) => {
        this.context.set(ctx);
        const first = ctx.fetchAvailable ? ctx.provider : (ctx.sites ?? []).find((x) => x.available)?.id ?? ctx.provider;
        this.site.set(first);
        this.resultsSite.set(first);
        this.query.set(ctx.suggestions?.[0] ?? '');
        // The series a collection is about is no doujinshi: hidden by default there whatever the folder's Content.
        this.hideDoujinshi.set(this.collectionMode || !ctx.doujinshiContent);
        this.budgetUsed.set(ctx.budgetUsedToday);
        this.budgetLimit.set(ctx.dailyBudget);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Could not load the item.');
        this.loading.set(false);
      },
    });
  }

  /** A site's display name (the context's list, else the default provider's name). */
  nameOf(id: string): string {
    const ctx = this.context();
    return ctx?.sites?.find((x) => x.id === id)?.name ?? ctx?.providerName ?? id;
  }

  siteUnavailable(s: IdentifySiteDto): string {
    return s.unavailableCode === 'provider_not_allowed'
      ? `${s.name} is off the provider allowlist (Metadata Manager > Settings).`
      : `${s.name} is not available now.`;
  }

  /** Switching sites clears the results (they belong to the other site). */
  chooseSite(id: string): void {
    if (id === this.site()) return;
    this.site.set(id);
    this.candidates.set([]);
    this.totalHits.set(0);
    this.searched.set(false);
    this.error.set(null);
  }

  shownAltTitles(p: IdentifyPreviewDto): string[] {
    const all = p.altTitles ?? [];
    return this.altTitlesExpanded() ? all : all.slice(0, this.ALT_TITLE_LIMIT);
  }

  runSearch(): void {
    if (!this.queryValid() || this.busy()) return;
    const q = this.query().trim();
    this.lastSubmittedQuery.set(q);
    this.lastSubmittedHide.set(this.hideDoujinshi());
    this.page.set(1);
    this.fetchPage(q, 1, false);
  }

  moreResults(): void {
    const next = this.page() + 1;
    this.page.set(next);
    this.fetchPage(this.lastSubmittedQuery(), next, true);
  }

  runLookup(): void {
    const ref = this.reference().trim();
    if (!ref || this.busy()) return;
    this.showPreview(this.api.lookup(this.data.nodeId, ref), 'Reference', null);
  }

  usePreview(provider: string, externalId: string, method: MetadataMatchMethod, candidate?: IdentifyCandidateDto): void {
    this.showPreview(this.api.preview(this.data.nodeId, { provider, externalId }), method, candidate ?? null);
  }

  imageFailed(token: string): void {
    this.failedImages.set(new Set([...this.failedImages(), token]));
  }

  back(): void {
    this.step.set('search');
    this.preview.set(null);
    this.error.set(null);
  }

  link(): void {
    const p = this.preview();
    const ctx = this.context();
    if (!p || !ctx || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .link(this.data.nodeId, { provider: p.provider, externalId: p.externalId, matchMethod: this.previewMethod(), matchScore: this.match().score })
      .subscribe({
        next: (change) => {
          this.busy.set(false);
          this.metadataState.announce(change.nodeId, true);
          this.dialogRef.close(true);
          this.offerUndo(change, `Linked to ${p.title}`, 'Link undone');
        },
        error: (err: ApiError) => this.fail(err),
      });
  }

  /** 1.34.0: marks the folder "Collection about" the previewed series (with Undo). */
  setCollection(): void {
    const p = this.preview();
    if (!p || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .setCollection(this.data.nodeId, {
        provider: p.provider,
        externalId: p.externalId,
        matchMethod: this.previewMethod(),
        setDoujinContent: !this.context()?.doujinshiContent && this.setDoujinContent(),
      })
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          this.metadataState.announce(result.change.nodeId, true);
          this.dialogRef.close(true);
          this.offerUndo(result.change, collectionResultMessage(p.title, result), 'Collection undone');
        },
        error: (err: ApiError) => this.fail(err),
      });
  }

  imageUrl(token: string): string {
    return this.api.candidateImageUrl(token);
  }

  line(c: IdentifyCandidateDto): string {
    return candidateLine(c);
  }

  pLine(p: IdentifyPreviewDto): string {
    return previewLine(p);
  }

  credits(p: IdentifyPreviewDto): { label: string; names: string[] }[] {
    return creditGroups(p.creators);
  }

  strength(c: IdentifyCandidateDto): string {
    return STRENGTH_LABELS[c.strength];
  }

  percent(score: number): number {
    return scorePercent(score);
  }

  tall(value: boolean | null | undefined): string {
    return tallStripsLabel(value);
  }

  private fetchPage(query: string, page: number, append: boolean): void {
    this.busy.set(true);
    this.error.set(null);
    const site = append ? this.resultsSite() : this.site();
    const year = site === 'gcd' && this.useStartYear() ? this.context()?.local.yearHint ?? null : null;
    const call = site === 'mangaupdates'
      ? this.api.search(this.data.nodeId, query, page, this.lastSubmittedHide())
      : this.api.search(this.data.nodeId, query, page, false, site, year);
    call.subscribe({
      next: (result) => {
        this.resultsSite.set(result.provider || site);
        this.candidates.set(append ? [...this.candidates(), ...result.candidates] : result.candidates);
        this.totalHits.set(result.totalHits);
        this.budgetUsed.set(result.budgetUsedToday);
        this.budgetLimit.set(result.dailyBudget);
        this.searched.set(true);
        this.busy.set(false);
      },
      error: (err: ApiError) => this.fail(err),
    });
  }

  private showPreview(call: Observable<IdentifyPreviewDto>, method: MetadataMatchMethod, candidate: IdentifyCandidateDto | null): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (p) => {
        this.altTitlesExpanded.set(false);
        this.preview.set(p);
        this.previewMethod.set(method);
        this.previewCandidate.set(candidate);
        this.step.set('preview');
        this.busy.set(false);
      },
      error: (err: ApiError) => this.fail(err),
    });
  }

  private fail(err: ApiError): void {
    this.busy.set(false);
    const retry = err?.error === 'provider_backoff' || err?.error === 'provider_busy' ? ' ' + retryLabel(err.detail) : '';
    this.error.set(`${err?.message || 'The request failed.'}${retry}`.trim());
  }

  /** "Linked to X - Undo": restores the node's previous own row (none, Don't match, a collection, or another record). */
  private offerUndo(change: NodeSeriesLinkChangeDto, message: string, undone: string): void {
    const ref = this.snackBar.open(message, 'Undo', { duration: 8000 });
    ref.onAction().subscribe(() => {
      restorePrevious(this.api, change.nodeId, change.previous ?? null).subscribe({
        next: () => {
          this.metadataState.refresh(change.nodeId);
          this.snackBar.open(undone, 'Close', { duration: 2500 });
        },
        error: (err: ApiError) => this.snackBar.open(`Undo failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 }),
      });
    });
  }
}

/** The call that puts a node's own link row back to `previous`. */
export function restorePrevious(api: MetadataApiService, nodeId: string, previous: NodeSeriesLinkDto | null): Observable<unknown> {
  if (!previous) return api.unlink(nodeId);
  if (previous.state === 'DontMatch') return api.setDontMatch(nodeId);
  if (previous.state === 'CollectionAbout') {
    return api.setCollection(nodeId, {
      provider: previous.provider ?? '',
      externalId: previous.externalId ?? '',
      matchMethod: previous.matchMethod ?? null,
      setDoujinContent: false,
    });
  }
  return api.link(nodeId, {
    provider: previous.provider ?? '',
    externalId: previous.externalId ?? '',
    matchMethod: previous.matchMethod ?? null,
  });
}
