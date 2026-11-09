import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto } from '../../core/api/api-types';
import { NodeRatingComponent } from './node-rating.component';

/** What the Rate sheet rates: the open archive (its folder - the reader's breadcrumb parent - is read from the node). */
export interface RatingSheetData {
  archiveId: string;
}

/**
 * The reader's "Rate ..." sheet (1.52.0, integrator decision: no new end screen): two rows, this archive and its folder, each an
 * editable rating. A bottom sheet on a phone, a small dialog elsewhere (`RatingSheetService`). An archive at a library root has no
 * folder row.
 */
@Component({
  selector: 'app-rating-sheet',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, NodeRatingComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="sheet" data-testid="rating-sheet">
      <div class="head">
        <h2 class="title">Rate</h2>
        <button mat-icon-button type="button" class="close" aria-label="Close" (click)="close()">
          <mat-icon>close</mat-icon>
        </button>
      </div>
      @if (archive(); as a) {
        <section class="row" data-testid="rating-sheet-archive">
          <p class="name">{{ a.displayName }}</p>
          <app-node-rating [nodeId]="a.id" [known]="a.myRating ?? null" caption="This archive" [name]="a.displayName" />
        </section>
      }
      @if (folder(); as f) {
        <section class="row" data-testid="rating-sheet-folder">
          <p class="name">{{ f.displayName }}</p>
          <app-node-rating [nodeId]="f.id" [known]="f.myRating ?? null" caption="Its folder" [name]="f.displayName" />
        </section>
      }
      @if (error()) {
        <p class="error">Could not load the ratings.</p>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .sheet { padding: 4px 16px 16px; background: var(--mp-surface); color: var(--mp-text); min-width: min(360px, 100%); }
    .head { display: flex; align-items: center; justify-content: space-between; }
    .title { margin: 8px 0; font-size: 18px; font-weight: 500; }
    .row { padding: 6px 0; border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.08); }
    .name { margin: 4px 0 0; font-size: 14px; overflow-wrap: anywhere; }
    .error { color: var(--mp-error); font-size: 13px; }
  `],
})
export class RatingSheetComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialogRef = inject<MatDialogRef<RatingSheetComponent> | null>(MatDialogRef, { optional: true });
  private readonly sheetRef = inject<MatBottomSheetRef<RatingSheetComponent> | null>(MatBottomSheetRef, { optional: true });
  private readonly data: RatingSheetData =
    inject<RatingSheetData | null>(MAT_DIALOG_DATA, { optional: true }) ?? inject<RatingSheetData>(MAT_BOTTOM_SHEET_DATA);

  readonly archive = signal<CatalogNodeDto | null>(null);
  readonly folder = signal<CatalogNodeDto | null>(null);
  readonly error = signal(false);

  ngOnInit(): void {
    this.api.getNode(this.data.archiveId).subscribe({
      next: (archive) => {
        this.archive.set(archive);
        if (!archive.parentId) return;
        this.api.getNode(archive.parentId).subscribe({
          next: (folder) => this.folder.set(folder),
          error: () => this.folder.set(null),
        });
      },
      error: () => this.error.set(true),
    });
  }

  close(): void {
    this.dialogRef?.close();
    this.sheetRef?.dismiss();
  }
}
