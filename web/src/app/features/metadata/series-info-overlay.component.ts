import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../core/auth/auth.service';
import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { SeriesAdminActionsComponent } from './series-admin-actions.component';
import { ageLabel, precedenceLabel, showsPrecedence } from './series-info-labels';
import { SeriesInfoOverlayData } from './series-info-overlay.service';
import { SeriesInfoSummaryComponent } from './series-info-summary.component';

/**
 * The series-info overlay (1.24.0): a glance at a node's series information without
 * leaving browse. Hosted by a right-side MatDialog (desktop/tablet) or a
 * MatBottomSheet (phone) - see SeriesInfoOverlayService - so it works with either
 * ref/data token. Content = the shared summary + an attribution/sources footer +
 * "Open series page" + the admin menu (with lane B2's disabled Identify slot).
 */
@Component({
  selector: 'app-series-info-overlay',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, SeriesInfoSummaryComponent, SeriesAdminActionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="sheet" data-testid="series-overlay">
      <div class="head">
        <span class="kicker">Series info</span>
        <button mat-icon-button type="button" class="close" aria-label="Close" (click)="close()">
          <mat-icon>close</mat-icon>
        </button>
      </div>

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error">{{ error() }}</p>
      } @else if (info(); as i) {
        <div class="body">
          <app-series-info-summary [info]="i" [compact]="true" />
        </div>

        <footer class="foot">
          @if (i.web) {
            <p class="source">
              Series data from
              @if (i.web.siteUrl) {
                <a [href]="i.web.siteUrl" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">{{ i.web.providerName }}</a>
              } @else {
                {{ i.web.providerName }}
              }
              · {{ fetchedAge() }}
            </p>
          }
          @if (i.comicInfo; as ci) {
            <p class="source" data-testid="series-comicinfo-source">
              @if (i.state === 'ComicInfo' && i.nodeKind === 'Archive') {
                From ComicInfo.xml in your file
              } @else {
                ComicInfo in {{ ci.itemsWithComicInfo }} of {{ ci.itemsTotal }} items
              }
            </p>
          }
          @if (showPrecedence()) {
            <p class="source muted">Precedence: {{ precedence() }}</p>
          }
          @if (i.state === 'Mixed' && auth.isAdmin()) {
            <p class="source muted">Mark it "Don't match", or link its items individually.</p>
          }
          <div class="actions">
            <!-- Not for a multi-series folder (owner, 1.24.0): the overlay already lists its series. -->
            @if (i.state !== 'None' && i.state !== 'DontMatch' && i.state !== 'Mixed') {
              <button mat-flat-button type="button" (click)="openPage()" data-testid="open-series-page">
                <mat-icon>open_in_full</mat-icon> Open series page
              </button>
            }
            @if (auth.isAdmin()) {
              <app-series-admin-actions [info]="i" (changed)="reload()" />
            }
          </div>
        </footer>
      }
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .sheet { display: flex; flex-direction: column; height: 100%; max-height: 100%; background: #16161f; color: #e6e6ee; }
    .head { display: flex; align-items: center; justify-content: space-between; padding: 8px 8px 0 16px; }
    .kicker { font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: #8a8a99; }
    .body { flex: 1 1 auto; overflow-y: auto; padding: 4px 16px 12px; }
    .foot { flex: 0 0 auto; padding: 10px 16px 14px; border-top: 1px solid rgba(255, 255, 255, 0.08); }
    .source { margin: 0 0 4px; font-size: 12px; color: #b0b0c0; }
    .source a { color: #b39dff; }
    .muted { color: #8a8a99; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .actions mat-icon { margin-right: 4px; }
    .state { padding: 24px 16px; display: flex; justify-content: center; }
    .error { color: #ff8a80; }
    /* Hosts: the dialog is a full-height right sheet, the bottom sheet grows to 90vh. */
    ::ng-deep .series-info-side-sheet .mat-mdc-dialog-surface { border-radius: 0; background: #16161f; }
    ::ng-deep .series-info-side-sheet .mat-mdc-dialog-container { max-height: 100vh; }
    ::ng-deep .series-info-bottom-sheet.mat-bottom-sheet-container,
    ::ng-deep .series-info-bottom-sheet .mat-bottom-sheet-container {
      max-height: 90vh; padding: 0; background: #16161f; border-radius: 12px 12px 0 0;
    }
  `],
})
export class SeriesInfoOverlayComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);
  private readonly dialogRef = inject<MatDialogRef<SeriesInfoOverlayComponent> | null>(MatDialogRef, { optional: true });
  private readonly sheetRef = inject<MatBottomSheetRef<SeriesInfoOverlayComponent> | null>(MatBottomSheetRef, { optional: true });
  private readonly data: SeriesInfoOverlayData =
    inject<SeriesInfoOverlayData | null>(MAT_DIALOG_DATA, { optional: true })
    ?? inject<SeriesInfoOverlayData>(MAT_BOTTOM_SHEET_DATA);

  readonly info = signal<SeriesInfoDto | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly fetchedAge = computed(() => ageLabel(this.info()?.web?.fetchedAt));
  readonly precedence = computed(() => {
    const i = this.info();
    return i ? precedenceLabel(i) : '';
  });

  /** Precedence only matters when both sources exist (web data and ComicInfo). */
  readonly showPrecedence = computed(() => showsPrecedence(this.info()));

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.loading.set(this.info() === null);
    this.api.getSeriesInfo(this.data.nodeId).subscribe({
      next: (info) => {
        this.info.set(info);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Series information is not available.');
        this.loading.set(false);
      },
    });
  }

  openPage(): void {
    const anchor = this.info()?.anchorNodeId;
    if (!anchor) return;
    this.close();
    void this.router.navigate(['/series', anchor]);
  }

  close(): void {
    this.dialogRef?.close();
    this.sheetRef?.dismiss();
  }
}
