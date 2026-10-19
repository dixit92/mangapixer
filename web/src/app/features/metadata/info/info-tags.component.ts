import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { AuthService } from '../../../core/auth/auth.service';
import { CreatorAdminService } from './creator-admin/creator-admin.service';
import { InfoApiService } from './info-api.service';
import { bucketLabel, bucketRank, chipCount, chipText, chipTitle, filterParam, roleLabel } from './info-labels';
import { FacetBucketDto, FacetBucketName, FacetChipDto, InfoPanelDto } from './info-types';

/**
 * The Info panel's Tags block (1.45.0, D1 2.6): one row per bucket in the owner's order - a label and wrapping chips. A chip opens the
 * tag view, which is the search page filtered by that value (`/search?f=<bucket>:<value id>`, decision 4). A chip shows its count only
 * when it is 2 or more (decision 5: quiet, still clickable); on a container the count is "N here" and each bucket shows its top 12 with
 * "More" (Q10). A declared value that disagrees with the linked record is amber, with the declared-facts line's Conflict wording.
 * Admins get a menu on creator chips: Rename, Merge into..., Split... (lane F's routes).
 *
 * Hosted by the series summary (overlay and series page). Inside the overlay a chip also closes it (a router navigation does not close a
 * Material dialog by itself). Nothing is shown while loading, when nothing is indexed, or when the server answers with an error (the
 * summary keeps its plain-text lines then).
 */
@Component({
  selector: 'app-info-tags',
  standalone: true,
  imports: [RouterLink, MatIconModule, MatMenuModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (buckets().length > 0) {
      <section class="tags" aria-label="Tags" data-testid="info-tags">
        @for (b of buckets(); track b.bucket) {
          <div class="bucket" [attr.data-bucket]="b.bucket" data-testid="info-bucket">
            <h3 class="label">{{ label(b.bucket) }}</h3>
            <ul class="chips">
              @for (chip of b.chips; track chip.valueId) {
                <li class="chip-wrap" [class.conflict]="chip.conflict">
                  <a class="chip" [routerLink]="['/search']" [queryParams]="{ f: param(b.bucket, chip) }"
                     [attr.aria-label]="title(b.bucket, chip)" [matTooltip]="title(b.bucket, chip)" matTooltipShowDelay="600"
                     [class.has-menu]="menuFor(b.bucket, chip)" data-testid="info-chip" [attr.data-value]="chip.valueId"
                     (click)="opened()">
                    @if (chip.conflict) { <mat-icon class="warn" aria-hidden="true">warning_amber</mat-icon> }
                    <span class="name">{{ text(b.bucket, chip) }}</span>
                    @if (role(chip); as r) { <span class="role">{{ r }}</span> }
                    @if (count(chip); as c) { <span class="count" data-testid="info-chip-count">{{ c }}</span> }
                  </a>
                  @if (menuFor(b.bucket, chip)) {
                    <button type="button" class="chip-menu" [matMenuTriggerFor]="creatorMenu" [matMenuTriggerData]="{ chip: chip }"
                            [attr.aria-label]="'Creator actions for ' + chip.display" data-testid="info-chip-menu">
                      <mat-icon>more_vert</mat-icon>
                    </button>
                  }
                </li>
              }
              @if (b.more > 0) {
                <li class="chip-wrap">
                  <button type="button" class="chip more" [disabled]="loadingMore() === b.bucket" (click)="loadMore(b.bucket)"
                          data-testid="info-more">
                    <span class="name">{{ loadingMore() === b.bucket ? 'Loading…' : 'More' }}</span>
                    <span class="count">+{{ b.more }}</span>
                  </button>
                </li>
              }
            </ul>
          </div>
        }
      </section>
    }

    <mat-menu #creatorMenu="matMenu">
      <ng-template matMenuContent let-chip="chip">
        <button mat-menu-item type="button" (click)="rename(chip)" data-testid="creator-rename"><mat-icon>edit</mat-icon> Rename…</button>
        <button mat-menu-item type="button" (click)="merge(chip)" data-testid="creator-merge"><mat-icon>call_merge</mat-icon> Merge into…</button>
        <button mat-menu-item type="button" (click)="split(chip)" data-testid="creator-split"><mat-icon>call_split</mat-icon> Split…</button>
      </ng-template>
    </mat-menu>
  `,
  styles: [`
    :host { display: block; clear: both; }
    .tags { margin-top: 12px; padding-top: 10px; border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.08); }
    .bucket { margin: 0 0 10px; }
    .label { margin: 0 0 6px; font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: var(--mp-text-dim); }
    /* Chips wrap; 32 px tall with 8 px gaps (D1 2.6: 44 px rows on a phone when they wrap is the row height + gap). */
    .chips { display: flex; flex-wrap: wrap; gap: 8px; margin: 0; padding: 0; list-style: none; min-width: 0; }
    .chip-wrap { display: inline-flex; max-width: 100%; min-width: 0; }
    .chip {
      display: inline-flex; align-items: center; gap: 6px; box-sizing: border-box;
      height: 32px; max-width: 100%; min-width: 0; padding: 0 12px;
      border-radius: 16px; border: 1px solid rgb(var(--mp-ink-rgb) / 0.16);
      background: rgb(var(--mp-ink-rgb) / 0.04); color: var(--mp-text);
      font: inherit; font-size: 13px; line-height: 1; text-decoration: none; cursor: pointer;
    }
    .chip:hover { background: rgb(var(--mp-ink-rgb) / 0.08); }
    .chip:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 1px; }
    .chip.has-menu { border-top-right-radius: 0; border-bottom-right-radius: 0; padding-right: 8px; }
    /* Long names are cut inside the chip; the count and the role never wrap and are never cut. */
    .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .role { flex: none; white-space: nowrap; font-size: 11px; color: var(--mp-text-muted); }
    .count { flex: none; white-space: nowrap; font-size: 12px; color: var(--mp-text-muted); }
    .chip-menu {
      display: inline-flex; align-items: center; justify-content: center; flex: none; box-sizing: border-box;
      width: 32px; height: 32px; padding: 0; margin-left: -1px; cursor: pointer;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); border-radius: 0 16px 16px 0;
      background: rgb(var(--mp-ink-rgb) / 0.04); color: var(--mp-text-muted);
    }
    .chip-menu:hover { background: rgb(var(--mp-ink-rgb) / 0.08); }
    .chip-menu:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 1px; }
    .chip-menu mat-icon { font-size: 18px; width: 18px; height: 18px; }
    /* A conflicting value (owner 2026-09-27 answer 3): amber, as the declared-facts line's Conflict badge. */
    .conflict .chip, .conflict .chip-menu { border-color: var(--mp-warn); background: rgb(var(--mp-warn-rgb) / 0.12); }
    .conflict .name { color: var(--mp-warn); }
    .warn { flex: none; font-size: 16px; width: 16px; height: 16px; color: var(--mp-warn); }
    .more { color: var(--mp-accent); border-style: dashed; }
    .more:disabled { cursor: default; opacity: 0.7; }
  `],
})
export class InfoTagsComponent {
  private readonly api = inject(InfoApiService);
  private readonly auth = inject(AuthService);
  private readonly creatorAdmin = inject(CreatorAdminService);
  /** Set when the block is inside the Info overlay: a chip closes it on the way to the search page. */
  private readonly dialogRef = inject(MatDialogRef, { optional: true });
  private readonly sheetRef = inject(MatBottomSheetRef, { optional: true });

  /** The node whose tags are shown (the folder or archive the panel is for). */
  readonly nodeId = input.required<string>();

  /** The panel as loaded (null: nothing indexed, or not available) - the summary hides its plain-text lines for buckets shown here. */
  readonly loaded = output<InfoPanelDto | null>();

  readonly panel = signal<InfoPanelDto | null>(null);
  readonly loadingMore = signal<FacetBucketName | null>(null);

  readonly buckets = computed(() =>
    [...(this.panel()?.buckets ?? [])]
      .filter((b) => b.chips.length > 0)
      .sort((a, b) => bucketRank(a.bucket) - bucketRank(b.bucket)),
  );

  /** Bumped per load, so a late answer for an earlier node (or an earlier "More") is dropped. */
  private generation = 0;

  constructor() {
    effect(() => {
      const id = this.nodeId();
      untracked(() => this.load(id));
    });
  }

  label(bucket: FacetBucketName): string {
    return bucketLabel(bucket);
  }

  text(bucket: FacetBucketName, chip: FacetChipDto): string {
    return chipText(bucket, chip);
  }

  title(bucket: FacetBucketName, chip: FacetChipDto): string {
    return chipTitle(bucket, chip, this.panel()?.container ?? false);
  }

  role(chip: FacetChipDto): string | null {
    return roleLabel(chip.role);
  }

  count(chip: FacetChipDto): string | null {
    return chipCount(chip, this.panel()?.container ?? false);
  }

  param(bucket: FacetBucketName, chip: FacetChipDto): string {
    return filterParam(bucket, chip.valueId);
  }

  /** Admins: creator chips with a canonical creator id carry the Rename / Merge / Split menu. */
  menuFor(bucket: FacetBucketName, chip: FacetChipDto): boolean {
    return bucket === 'Creators' && !!chip.creatorId && this.auth.isAdmin();
  }

  /** A chip was followed: leave the overlay (the search page replaces what is behind it). */
  opened(): void {
    this.dialogRef?.close();
    this.sheetRef?.dismiss();
  }

  /** "More": every chip of the bucket replaces its top 12. */
  loadMore(bucket: FacetBucketName): void {
    const generation = this.generation;
    this.loadingMore.set(bucket);
    this.api.getInfoBucket(this.nodeId(), bucket).subscribe({
      next: (full) => {
        if (generation !== this.generation) return;
        this.panel.update((p) => p && { ...p, buckets: p.buckets.map((b) => (b.bucket === bucket ? { ...full, bucket, more: 0 } : b)) });
        this.loadingMore.set(null);
      },
      error: () => {
        if (generation === this.generation) this.loadingMore.set(null);
      },
    });
  }

  async rename(chip: FacetChipDto): Promise<void> {
    if (chip.creatorId && (await this.creatorAdmin.rename(chip.creatorId, chip.display))) this.load(this.nodeId());
  }

  async merge(chip: FacetChipDto): Promise<void> {
    if (chip.creatorId && (await this.creatorAdmin.merge(chip.creatorId, chip.display))) this.load(this.nodeId());
  }

  async split(chip: FacetChipDto): Promise<void> {
    if (chip.creatorId && (await this.creatorAdmin.split(chip.creatorId, chip.display))) this.load(this.nodeId());
  }

  private load(nodeId: string): void {
    const generation = ++this.generation;
    this.loadingMore.set(null);
    this.api.getInfo(nodeId).subscribe({
      next: (panel) => {
        if (generation !== this.generation) return;
        this.panel.set(panel);
        this.loaded.emit(panel.works > 0 ? panel : null);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.panel.set(null);
        this.loaded.emit(null);
      },
    });
  }
}
