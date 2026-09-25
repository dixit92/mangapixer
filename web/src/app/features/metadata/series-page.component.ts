import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { catchError, of } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { SeriesAdminActionsComponent } from './series-admin-actions.component';
import { ageLabel, precedenceLabel, roleLabel } from './series-info-labels';
import { SeriesInfoSummaryComponent } from './series-info-summary.component';

/**
 * The series page `/series/:nodeId` (1.24.0): the canonical, bookmarkable home of a
 * series. `nodeId` is the ANCHOR (the folder holding the link, or the ComicInfo
 * folder); opening it with any other node id redirects to the anchor (replaceUrl,
 * so Back does not bounce). Same DTO and summary component as the overlay, so the
 * two never disagree. Sections: About, Details, In your library (ComicInfo items),
 * Sources, Admin. On phone the sections are collapsible (About open) and everything
 * stacks in one column.
 */
@Component({
  selector: 'app-series-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule, MatProgressSpinnerModule, SeriesInfoSummaryComponent, SeriesAdminActionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page" data-testid="series-page">
      @if (loading()) {
        <div class="state"><mat-spinner diameter="32" /></div>
      } @else if (notFound()) {
        <p class="state">This series page is not available.</p>
      } @else if (info(); as i) {
        <nav class="back">
          <a [routerLink]="browseLink()"><mat-icon>arrow_back</mat-icon> Back to folder</a>
        </nav>

        <header class="hero">
          <app-series-info-summary [info]="i" />
          <div class="hero-actions">
            <a mat-stroked-button [routerLink]="browseLink()" data-testid="browse-folder">
              <mat-icon>folder_open</mat-icon> Browse folder
            </a>
            @if (continueTarget(); as next) {
              <a mat-flat-button [routerLink]="['/reader', next.id]" data-testid="continue-reading">
                <mat-icon>play_arrow</mat-icon> Continue reading
              </a>
            }
          </div>
        </header>

        @if (i.description) {
          <details class="section" open>
            <summary>About</summary>
            <p class="about">{{ i.description }}</p>
          </details>
        }

        @if (detailRows().length > 0) {
          <details class="section" [open]="!phone">
            <summary>Details</summary>
            <dl class="details">
              @for (row of detailRows(); track row.label) {
                <dt>{{ row.label }}</dt><dd>{{ row.value }}</dd>
              }
            </dl>
          </details>
        }

        @if ((i.items ?? []).length > 0) {
          <details class="section" [open]="!phone">
            <summary>In your library</summary>
            <p class="muted">{{ i.comicInfo?.itemsWithComicInfo }} of {{ i.comicInfo?.itemsTotal }} items carry ComicInfo</p>
            <table class="items" data-testid="series-items">
              <thead><tr><th>#</th><th>Vol</th><th>Title</th><th>Year</th></tr></thead>
              <tbody>
                @for (row of i.items; track row.nodeId) {
                  <tr>
                    <td>{{ row.number }}</td>
                    <td>{{ row.volume }}</td>
                    <td><a [routerLink]="['/reader', row.nodeId]">{{ row.title || row.displayName }}</a></td>
                    <td>{{ row.year }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </details>
        }

        <details class="section" [open]="!phone">
          <summary>Sources</summary>
          @if (i.web) {
            <p>
              Web:
              @if (i.web.siteUrl) {
                <a [href]="i.web.siteUrl" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">{{ i.web.providerName }}</a>
              } @else {
                {{ i.web.providerName }}
              }
              <span class="muted"> · fetched {{ fetchedAge() }}</span>
            </p>
          }
          @if (i.comicInfo) {
            <p>ComicInfo: {{ i.comicInfo.itemsWithComicInfo }} of {{ i.comicInfo.itemsTotal }} items</p>
            @for (url of i.comicInfo.webLinks ?? []; track url) {
              <p class="muted small"><a [href]="url" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">{{ url }}</a></p>
            }
          }
          <p class="muted">Precedence: {{ precedence() }}</p>
        </details>

        @if (auth.isAdmin()) {
          <section class="section admin">
            <h3>Admin</h3>
            <app-series-admin-actions [info]="i" (changed)="load(i.anchorNodeId)" />
          </section>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .page { max-width: 920px; margin: 0 auto; padding: 8px 4px 32px; color: #e6e6ee; }
    .state { display: flex; justify-content: center; padding: 48px 0; color: #b0b0c0; }
    .back a { display: inline-flex; align-items: center; gap: 4px; color: #b39dff; text-decoration: none; font-size: 14px; }
    .back mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .hero { margin: 12px 0 8px; padding: 16px; border-radius: 12px; background: #14141c; border: 1px solid rgba(255, 255, 255, 0.08); }
    .hero ::ng-deep .poster { width: 200px; height: 284px; }
    .hero ::ng-deep .title { font-size: 26px; }
    .hero-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 14px; clear: both; }
    .hero-actions mat-icon { margin-right: 4px; }
    .section { margin: 10px 0; padding: 10px 16px; border-radius: 10px; background: rgba(255, 255, 255, 0.03); }
    .section summary { cursor: pointer; font-weight: 600; padding: 4px 0; }
    .section h3 { margin: 4px 0 8px; font-size: 15px; }
    .about { white-space: pre-line; line-height: 1.55; }
    .details { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 8px 0; }
    .details dt { color: #8a8a99; }
    .details dd { margin: 0; }
    .items { width: 100%; border-collapse: collapse; font-size: 13px; }
    .items th, .items td { text-align: left; padding: 4px 8px; border-bottom: 1px solid rgba(255, 255, 255, 0.06); }
    .items a { color: #d8ccff; text-decoration: none; }
    .muted { color: #8a8a99; }
    .small { font-size: 12px; word-break: break-all; }
    a { color: #b39dff; }
    @media (max-width: 599.98px) {
      .hero { padding: 12px; }
      .hero ::ng-deep .poster { width: 96px; height: 136px; }
      .hero ::ng-deep .title { font-size: 20px; }
      .details { grid-template-columns: 1fr; gap: 2px; }
      .details dd { margin-bottom: 6px; }
    }
  `],
})
export class SeriesPageComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly metadata = inject(MetadataApiService);
  private readonly destroyRef = inject(DestroyRef);
  readonly auth = inject(AuthService);

  readonly info = signal<SeriesInfoDto | null>(null);
  readonly loading = signal(true);
  readonly notFound = signal(false);
  readonly continueTarget = signal<CatalogNodeDto | null>(null);

  /** Phone layout: sections start collapsed except About. Read once at creation. */
  readonly phone = typeof matchMedia === 'function' && matchMedia('(max-width: 599.98px)').matches;

  readonly fetchedAge = computed(() => ageLabel(this.info()?.web?.fetchedAt));
  readonly precedence = computed(() => {
    const i = this.info();
    return i ? precedenceLabel(i) : '';
  });

  /** "Browse folder": the anchor folder itself, or an archive anchor's reader. */
  readonly browseLink = computed(() => {
    const i = this.info();
    if (!i) return ['/libraries'];
    return i.anchorKind === 'Folder'
      ? ['/libraries', i.libraryId, 'browse', i.anchorNodeId]
      : ['/reader', i.anchorNodeId];
  });

  readonly detailRows = computed(() => {
    const i = this.info();
    if (!i) return [];
    const rows: { label: string; value: string }[] = [];
    const byRole = new Map<string, string[]>();
    for (const c of i.creators ?? []) {
      const label = roleLabel(c.role);
      byRole.set(label, [...(byRole.get(label) ?? []), c.name]);
    }
    for (const [label, names] of byRole) rows.push({ label, value: names.join(', ') });
    if ((i.genres ?? []).length > 0) rows.push({ label: 'Genres', value: i.genres!.join(', ') });
    for (const p of i.publishers ?? []) {
      const label = p.kind === 'original' ? 'Original publisher' : p.kind === 'english' ? 'English publisher' : 'Publisher';
      rows.push({ label, value: p.name });
    }
    if (i.latestChapter) rows.push({ label: 'Latest chapter', value: String(i.latestChapter) });
    if (i.startYear) rows.push({ label: 'Year', value: String(i.startYear) });
    if ((i.altTitles ?? []).length > 0) rows.push({ label: 'Also known as', value: i.altTitles!.join(' · ') });
    return rows;
  });

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const nodeId = params.get('nodeId');
      if (nodeId) this.load(nodeId);
    });
  }

  load(nodeId: string): void {
    this.loading.set(this.info()?.anchorNodeId !== nodeId);
    this.metadata.getSeriesInfo(nodeId, true).subscribe({
      next: (info) => {
        // Canonical home: any other node id redirects to the anchor.
        if (info.anchorNodeId !== nodeId) {
          void this.router.navigate(['/series', info.anchorNodeId], { replaceUrl: true });
          return;
        }
        this.info.set(info);
        this.notFound.set(false);
        this.loading.set(false);
        this.loadContinue(info);
      },
      error: () => {
        this.notFound.set(true);
        this.loading.set(false);
      },
    });
  }

  /** "Continue reading": the folder's existing next-to-read archive (no new API). */
  private loadContinue(info: SeriesInfoDto): void {
    this.continueTarget.set(null);
    if (info.anchorKind !== 'Folder' || info.state === 'None' || info.state === 'DontMatch') return;
    this.api.browseLibrary(info.libraryId, info.anchorNodeId, null, 1)
      .pipe(catchError(() => of(null)))
      .subscribe((page) => this.continueTarget.set(page?.nextUnread ?? null));
  }
}
