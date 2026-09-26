import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';

import { ApiError, MetadataFlagDto, MetadataFlagState } from '../../../../core/api/api-types';
import { IdentifyDialogService } from '../../identify-dialog/identify-dialog.service';
import { MetadataApiService } from '../../metadata-api.service';
import { MetadataReviewStateService } from '../../metadata-review-state.service';
import { MetadataStateService } from '../../metadata-state.service';
import { ReviewLibraryOption } from '../../review/review-dashboard.component';
import { FLAG_REASON_LABELS, FLAG_STATE_LABELS } from '../metadata-admin-labels';

type FlagFilter = 'open' | 'resolved' | 'all';
type Outcome = Exclude<MetadataFlagState, 'Open'>;

/**
 * Flags tab of `/admin/metadata` (stage 2, design section 5 + decision 12): readers'
 * "Wrong series?" reports. Open flags first, and among them flags on AUTOMATIC links
 * first (the server's order). Each shows the node, the current link and how it was made,
 * the reporter's display name, the reason and the note (user content, admin-only).
 *
 * Actions resolve the flag with an outcome: Re-identify opens the identify dialog and,
 * after a link, records `Relinked`; Unlink / Don't match / Dismiss send the outcome and
 * the server applies it. "This folder is not one series" makes Don't match the primary
 * action.
 */
@Component({
  selector: 'app-metadata-flags',
  standalone: true,
  imports: [
    DatePipe, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressSpinnerModule, MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flags" data-testid="metadata-flags">
      <div class="toolbar">
        <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" aria-label="Which flags" hideSingleSelectionIndicator>
          <mat-button-toggle value="open" data-testid="flags-open">Open</mat-button-toggle>
          <mat-button-toggle value="resolved">Resolved</mat-button-toggle>
          <mat-button-toggle value="all">All</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (f of items(); track f.flagId) {
            <article class="flag" [class.auto]="f.currentLink?.state === 'Auto'" [class.resolved]="f.state !== 'Open'"
                     data-testid="flag-row">
              <header>
                <mat-icon class="kind">{{ f.nodeKind === 'Archive' ? 'description' : 'folder' }}</mat-icon>
                <span class="name">{{ f.nodeDisplayName }}</span>
                <span class="reason" data-testid="flag-reason">{{ reasonLabels[f.reason] }}</span>
                @if (f.state !== 'Open') { <span class="state-chip">{{ stateLabels[f.state] }}</span> }
              </header>
              <p class="link">
                @if (f.currentLink; as l) {
                  @if (l.state === 'DontMatch') {
                    Now: Don't match
                  } @else {
                    Linked to <strong>{{ l.title || l.externalId }}</strong>
                    <span class="method" [class.auto]="l.state === 'Auto'">{{ l.state === 'Auto' ? 'automatic' : 'by an admin' }}</span>
                  }
                } @else {
                  No link now
                }
              </p>
              @if (f.note) {
                <blockquote class="note" data-testid="flag-note">{{ f.note }}</blockquote>
              }
              <p class="meta">Reported by {{ f.reporterDisplayName }} · {{ f.createdAt | date: 'medium' }}
                @if (f.resolvedAt) { · resolved {{ f.resolvedAt | date: 'mediumDate' }}@if (f.resolvedByDisplayName) { by {{ f.resolvedByDisplayName }} } }
              </p>
              @if (f.state === 'Open') {
                <div class="actions">
                  @if (f.reason === 'NotOneSeries') {
                    <button mat-flat-button type="button" [disabled]="busy() === f.flagId" (click)="resolve(f, 'DontMatch')"
                            data-testid="flag-dontmatch">
                      <mat-icon>block</mat-icon> Don't match <span class="suggested">suggested</span></button>
                    <button mat-stroked-button type="button" [disabled]="busy() === f.flagId" (click)="reidentify(f)" data-testid="flag-reidentify">
                      <mat-icon>travel_explore</mat-icon> Re-identify…</button>
                  } @else {
                    <button mat-flat-button type="button" [disabled]="busy() === f.flagId" (click)="reidentify(f)" data-testid="flag-reidentify">
                      <mat-icon>travel_explore</mat-icon> Re-identify…</button>
                    <button mat-stroked-button type="button" [disabled]="busy() === f.flagId" (click)="resolve(f, 'DontMatch')"
                            data-testid="flag-dontmatch">
                      <mat-icon>block</mat-icon> Don't match</button>
                  }
                  @if (f.currentLink && f.currentLink.state !== 'DontMatch') {
                    <button mat-stroked-button type="button" [disabled]="busy() === f.flagId" (click)="resolve(f, 'Unlinked')" data-testid="flag-unlink">
                      <mat-icon>link_off</mat-icon> Unlink</button>
                  }
                  <button mat-button type="button" [disabled]="busy() === f.flagId" (click)="resolve(f, 'Dismissed')" data-testid="flag-dismiss">
                    Dismiss</button>
                </div>
              }
            </article>
          } @empty {
            <div class="empty" data-testid="flags-empty">
              <mat-icon>outlined_flag</mat-icon>
              <p>{{ filter() === 'open' ? 'No open flags. Readers can report a wrong series from its panel or page.' : 'No flags.' }}</p>
            </div>
          }
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    .toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; margin-bottom: 12px; }
    .lib-filter { width: 220px; }
    .list { display: flex; flex-direction: column; gap: 10px; }
    .flag { padding: 12px 14px; border-radius: 10px; background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.06);
      border-left: 3px solid #ff8a80; }
    .flag.auto { border-left-color: #ffb74d; }
    .flag.resolved { border-left-color: #555; opacity: 0.85; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .kind { color: #8a8a99; }
    .name { font-weight: 500; font-size: 15px; overflow-wrap: anywhere; }
    .reason { padding: 0 8px; border-radius: 10px; background: rgba(244, 67, 54, 0.18); color: #ff8a80; font-size: 12px; line-height: 20px; }
    .state-chip { padding: 0 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.08); font-size: 12px; line-height: 20px; }
    .link { margin: 6px 0; font-size: 13px; }
    .method { margin-left: 6px; font-size: 11px; padding: 0 6px; border-radius: 4px; background: rgba(255, 255, 255, 0.08); }
    .method.auto { background: rgba(255, 183, 77, 0.18); color: #ffcc80; }
    .note { margin: 6px 0; padding: 6px 10px; border-left: 3px solid #555; color: #d0d0dc; white-space: pre-line; font-size: 13px;
      overflow-wrap: anywhere; }
    .meta { margin: 4px 0; font-size: 12px; color: #9a9aa8; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .actions mat-icon { margin-right: 2px; }
    .suggested { margin-left: 6px; font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px; opacity: 0.8; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 40px 0; color: #9a9aa8; text-align: center; }
    .empty mat-icon { font-size: 40px; width: 40px; height: 40px; }
    .more { display: flex; justify-content: center; margin: 12px 0; }
    @media (max-width: 599.98px) { .lib-filter { width: 100%; } }
  `],
})
export class MetadataFlagsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly reviewState = inject(MetadataReviewStateService);
  private readonly metadataState = inject(MetadataStateService);
  private readonly identifyDialog = inject(IdentifyDialogService);
  private readonly snackBar = inject(MatSnackBar);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialLibrary = input<string | null>(null);

  readonly filter = signal<FlagFilter>('open');
  readonly library = signal<string | null>(null);
  readonly items = signal<MetadataFlagDto[]>([]);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal<string | null>(null);

  readonly reasonLabels = FLAG_REASON_LABELS;
  readonly stateLabels = FLAG_STATE_LABELS;

  ngOnInit(): void {
    this.library.set(this.initialLibrary());
    this.load();
  }

  setFilter(filter: FlagFilter): void {
    this.filter.set(filter);
    this.load();
  }

  setLibrary(library: string | null): void {
    this.library.set(library);
    this.load();
  }

  load(more = false): void {
    if (!more) {
      this.loading.set(true);
      this.cursor.set(null);
    }
    this.error.set(null);
    this.api.getFlags(this.filter(), this.library(), more ? this.cursor() : null).subscribe({
      next: (page) => {
        this.items.update((prev) => (more ? [...prev, ...page.items] : page.items));
        this.cursor.set(page.hasMore || page.nextCursor ? page.nextCursor ?? null : null);
        this.loading.set(false);
      },
      error: (err: ApiError & { status?: number }) => {
        this.loading.set(false);
        this.error.set(err?.status === 501 ? 'Flags are not available on this server yet.' : err?.message || 'Flags could not be loaded.');
      },
    });
  }

  /** Re-identify: the identify dialog makes the link; the flag is then resolved as Relinked. */
  reidentify(flag: MetadataFlagDto): void {
    void this.identifyDialog.open(flag.nodeId).then((linked) => {
      if (linked) this.resolve(flag, 'Relinked');
    });
  }

  resolve(flag: MetadataFlagDto, outcome: Outcome): void {
    this.busy.set(flag.flagId);
    this.api.resolveFlag(flag.flagId, outcome).subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.items.update((list) => (this.filter() === 'open'
          ? list.filter((f) => f.flagId !== flag.flagId)
          : list.map((f) => (f.flagId === flag.flagId ? updated : f))));
        if (outcome !== 'Dismissed') this.metadataState.refresh(flag.nodeId);
        this.reviewState.refresh();
        this.snackBar.open(`Flag resolved: ${FLAG_STATE_LABELS[outcome]}`, 'Close', { duration: 3000 });
      },
      error: (err: ApiError) => {
        this.busy.set(null);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 5000 });
      },
    });
  }
}
