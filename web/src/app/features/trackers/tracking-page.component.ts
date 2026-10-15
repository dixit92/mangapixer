import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';

import { TrackerSummaryDto } from '../../core/api/api-types';
import { TrackerAccountPanelComponent } from './tracker-account-panel.component';
import { TrackerStateService } from './tracker-state.service';
import { TrackersApiService } from './trackers-api.service';
import { TRACKER_SLUGS, TRACKER_SYNC_DOCS_URL, failureText } from './tracker-texts';
import { TrackingListMemory } from './tracking-list-memory.service';
import { TrackingSeriesListComponent } from './tracking-series-list.component';

/**
 * The Tracking page (1.43.0 owner redesign; account menu > Tracking): everything about the user's own tracker lists in one place.
 * - the account cards, one per tracker an admin allows (or the user still has an account on), equal in size: connect (consent, then
 *   MyAnimeList's sign-in or the MangaUpdates form), "Connected as", "Add series I start reading", Disconnect;
 * - the series list, one tab per connected tracker (`app-tracking-series-list`: paging, chips, search, the two "Set progress"
 *   actions and the "⋯" menu).
 * `?tracker=<slug>&series=<seriesId>` (the series header's "Open in Tracking") opens that tracker's tab with the series highlighted;
 * the title travels in the navigation state (never in the address) and seeds the search. The address is followed while the page is
 * shown (another `?tracker=` / `?series=` - UX review UX8). Settings keeps a one-line link here.
 * Nothing on this page contacts a tracker unless the user presses an action that says so.
 */
@Component({
  selector: 'app-tracking-page',
  standalone: true,
  imports: [TrackerAccountPanelComponent, TrackingSeriesListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page" data-testid="tracking-page">
      <header class="intro">
        <h1>Tracking</h1>
        <p>Your reading progress on your own tracker lists. MangaPixer only ever raises a number on a tracker; lowering one is always
          your click. Private libraries and doujinshi are not synced unless you turn sync on for a folder or series (Select in a
          library, then "Sync").
          <a [href]="docsUrl" target="_blank" rel="noopener noreferrer" data-testid="tracking-help">How tracker sync works</a></p>
      </header>

      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (shown().length === 0) {
        <p class="muted" data-testid="tracking-none">
          No tracker is allowed on this server yet. An admin can allow one in Administration > Trackers.
        </p>
      } @else {
        <section class="accounts" aria-label="Connected accounts" data-testid="tracking-accounts">
          @for (t of shown(); track t.slug) {
            <app-tracker-account-panel [tracker]="t" (changed)="reload()" />
          }
        </section>
      }
      @if (error(); as e) { <p class="error" role="alert">{{ e }}</p> }

      @if (tabs().length > 0) {
        <section class="series" aria-label="Tracked series" data-testid="tracking-series">
          <div class="tabs" role="tablist" aria-label="Tracker">
            @for (t of tabs(); track t.slug) {
              <button type="button" role="tab" class="tab" [class.on]="active() === t.slug" [attr.aria-selected]="active() === t.slug"
                      [attr.aria-controls]="'tracking-panel-' + t.slug" [id]="'tracking-tab-' + t.slug"
                      (click)="select(t.slug)" [attr.data-testid]="'tracking-tab-' + t.slug">
                {{ t.name }}
                @if (attentionOf(t.slug); as n) { <span class="count" [attr.aria-label]="n + ' need a look'">{{ n }}</span> }
              </button>
            }
          </div>
          @for (t of tabs(); track t.slug) {
            @if (active() === t.slug) {
              <div role="tabpanel" [id]="'tracking-panel-' + t.slug" [attr.aria-labelledby]="'tracking-tab-' + t.slug">
                <app-tracking-series-list [slug]="t.slug" [name]="t.name" [canSync]="canSync(t)"
                                          [focusSeries]="focusSeries()" [initialSearch]="initialSearch()"
                                          (changed)="state.refreshAttention()" />
              </div>
            }
          }
        </section>
      } @else if (!loading() && shown().length > 0) {
        <p class="muted" data-testid="tracking-no-account">Connect an account above to see your series here.</p>
      }
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .page { max-width: 1240px; margin: 0 auto; display: flex; flex-direction: column; gap: 20px; min-width: 0; }
    .intro { display: flex; flex-direction: column; gap: 6px; }
    h1 { margin: 0; font-size: 26px; font-weight: 500; color: var(--mp-text-strong); }
    .intro p { margin: 0; color: var(--mp-text-muted); overflow-wrap: anywhere; }
    .intro a { color: var(--mp-accent); }
    .accounts { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 16px; align-items: stretch; }
    .series { background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.12); border-radius: 12px; min-width: 0; overflow: hidden; }
    .tabs { display: flex; flex-wrap: wrap; gap: 6px; padding: 14px 16px 2px; }
    .tab { min-height: 36px; padding: 0 14px; border-radius: 18px; border: 0; background: transparent; color: var(--mp-text-muted);
           font: inherit; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; }
    .tab:hover { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .tab.on { background: var(--mp-surface-accent); color: var(--mp-accent-soft); font-weight: 500; }
    .count { font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: 9px; background: var(--mp-warn-strong); color: var(--mp-on-warn); }
    .muted { color: var(--mp-text-muted); font-size: 14px; margin: 0; }
    .error { color: var(--mp-error); font-size: 14px; margin: 0; }
  `],
})
export class TrackingPageComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  readonly state = inject(TrackerStateService);

  readonly docsUrl = TRACKER_SYNC_DOCS_URL;
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  /** The tab picked by the user or the address. */
  readonly picked = signal<string | null>(null);
  readonly focusSeries = signal<string | null>(null);
  readonly initialSearch = signal('');

  /** Allowed trackers, and trackers the user still has an account on - in the fixed order. */
  readonly shown = computed(() => this.ordered((this.state.overview()?.trackers ?? []).filter((t) => t.allowed || !!t.account)));
  /** One series tab per tracker the user has an account on. */
  readonly tabs = computed(() => this.shown().filter((t) => !!t.account));
  readonly active = computed(() => {
    const tabs = this.tabs();
    const picked = this.picked();
    return tabs.some((t) => t.slug === picked) ? picked : (tabs[0]?.slug ?? null);
  });

  constructor() {
    // Created by a Back / Forward: the series list comes back with its chip, search and pages (UX11).
    inject(TrackingListMemory).arm(this.router.currentNavigation()?.trigger === 'popstate');
  }

  ngOnInit(): void {
    // The address is read for every change while the page is shown, not once (UX8): the route is reused for another query.
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => this.applyQuery(params));
    this.reload();
    this.state.refreshAttention();
  }

  private applyQuery(params: ParamMap): void {
    this.picked.set(params.get('tracker'));
    const series = params.get('series');
    // "Open in Tracking": the title comes with the navigation (its state, also kept in the history entry), never in the address.
    // During a navigation its own state only (the history entry is still the previous page's); after it, the current entry's.
    const nav = this.router.currentNavigation();
    const state = nav ? nav.extras.state : (globalThis.history?.state as Record<string, unknown> | null | undefined);
    const title = state?.['title'];
    this.initialSearch.set(series && typeof title === 'string' ? title : '');
    this.focusSeries.set(series);
  }

  reload(): void {
    this.api.overview().subscribe({
      next: (o) => { this.state.set(o); this.loading.set(false); this.state.refreshAttention(); },
      error: (err: unknown) => { this.loading.set(false); this.error.set(failureText(err)); },
    });
  }

  select(slug: string): void {
    this.picked.set(slug);
    // A tab change leaves the focused series behind.
    this.focusSeries.set(null);
    this.initialSearch.set('');
    void this.router.navigate([], { relativeTo: this.route, queryParams: { tracker: slug }, replaceUrl: true });
  }

  attentionOf(slug: string): number {
    return this.state.attentionDto()?.trackers.find((t) => t.tracker === slug)?.count ?? 0;
  }

  /** Allowed, connected and on the current consent: the actions that reach the tracker are on. */
  canSync(t: TrackerSummaryDto): boolean {
    const a = t.account;
    return t.allowed && !!a && a.state === 'Ok' && !a.consentRenewalNeeded;
  }

  private ordered(list: TrackerSummaryDto[]): TrackerSummaryDto[] {
    const rank = (slug: string) => {
      const i = (TRACKER_SLUGS as readonly string[]).indexOf(slug);
      return i < 0 ? TRACKER_SLUGS.length : i;
    };
    return [...list].sort((a, b) => rank(a.slug) - rank(b.slug));
  }
}
