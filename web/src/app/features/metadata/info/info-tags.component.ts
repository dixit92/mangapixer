import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { AuthService } from '../../../core/auth/auth.service';
import { CreatorAdminService } from './creator-admin/creator-admin.service';
import { InfoApiService, InfoTarget } from './info-api.service';
import { bucketLabel, bucketRank, chipCount, chipText, chipTitle, locationText, roleLabel } from './info-labels';
import { FacetBucket, FacetBucketDto, FacetChipDto, FacetLocationDto, InfoPanelDto } from './info-types';

/**
 * The Info panel's Tags block (1.45.0, D1 2.6): one row per bucket in the owner's order - a label and wrapping chips. A chip opens the
 * tag view, which is the search page filtered by that value (`/search?f=<bucket>:<value id>`, decision 4). A chip shows its count only
 * when it is 2 or more (decision 5: quiet, still clickable); on a container the count is "N here" and each bucket shows its top 12 with
 * "+N more" (Q10) - a button since 1.47.0 that adds the rest of that bucket in place (the bucket page route, 100 at a time). A declared value that disagrees with the linked record is amber, with the declared-facts line's Conflict wording.
 * A single work kept in several places lists them ("2 locations"). Admins get a menu on creator chips: Rename, Merge into...,
 * Split... (lane F's routes).
 *
 * Hosted under the series summary by the Info overlay and the series page; 1.47.0: also alone in the overlay of a library root
 * (`libraryId` instead of `nodeId` - a library root is not a node). Inside the overlay a chip also closes it (a router navigation
 * does not close a Material dialog by itself). Nothing is shown while loading, when nothing is indexed, or when the server answers with
 * an error (the summary keeps its plain-text lines then).
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
              @for (chip of b.chips; track chip.id) {
                <li class="chip-wrap" [class.conflict]="chip.conflict">
                  <a class="chip" [routerLink]="['/search']" [queryParams]="{ f: chip.filter }"
                     [attr.aria-label]="title(b.bucket, chip)" [matTooltip]="title(b.bucket, chip)" matTooltipShowDelay="600"
                     [class.has-menu]="menuFor(b.bucket)" data-testid="info-chip" [attr.data-value]="chip.id"
                     (click)="opened()">
                    @if (chip.conflict) { <mat-icon class="warn" aria-hidden="true">warning_amber</mat-icon> }
                    <span class="name">{{ text(b.bucket, chip) }}</span>
                    @if (role(chip); as r) { <span class="role">{{ r }}</span> }
                    @if (count(chip); as c) { <span class="count" data-testid="info-chip-count">{{ c }}</span> }
                  </a>
                  @if (menuFor(b.bucket)) {
                    <button type="button" class="chip-menu" [matMenuTriggerFor]="creatorMenu" [matMenuTriggerData]="{ chip: chip }"
                            [attr.aria-label]="'Creator actions for ' + chip.display" data-testid="info-chip-menu">
                      <mat-icon>more_vert</mat-icon>
                    </button>
                  }
                </li>
              }
              @if (b.more > 0) {
                <li class="chip-wrap">
                  <button type="button" class="more" data-testid="info-more" (click)="showMore(b)" [disabled]="loadingMore().has(b.bucket)"
                          [attr.aria-label]="'Show ' + b.more + ' more in ' + label(b.bucket)">+{{ b.more }} more</button>
                </li>
              }
            </ul>
          </div>
        }
        @if (locations().length > 1) {
          <div class="bucket" data-testid="info-locations">
            <h3 class="label">{{ panel()?.work?.locationCount }} locations</h3>
            <ul class="places">
              @for (loc of locations(); track loc.nodeId) {
                <li>
                  <a [routerLink]="['/libraries', loc.libraryId, 'browse', loc.nodeId]" [title]="place(loc)" (click)="opened()">{{ place(loc) }}</a>
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
    .bucket { margin: 0 0 10px; min-width: 0; }
    .label { margin: 0 0 6px; font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: var(--mp-text-dim); }
    /* Chips wrap; 32 px tall with 8 px gaps (D1 2.6). */
    .chips { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; padding: 0; list-style: none; min-width: 0; }
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
    /* "+N more" (1.47.0): a quiet text button the size of a chip; it adds the rest of the bucket in place. */
    .more {
      display: inline-flex; align-items: center; box-sizing: border-box; height: 32px; padding: 0 10px; border: 0; border-radius: 16px;
      background: transparent; color: var(--mp-accent); font: inherit; font-size: 12px; font-weight: 600; white-space: nowrap; cursor: pointer;
    }
    .more:hover { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .more:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 1px; }
    .more:disabled { color: var(--mp-text-muted); cursor: default; }
    .places { margin: 0; padding: 0; list-style: none; font-size: 13px; }
    .places li { min-width: 0; }
    .places a {
      display: block; padding: 6px 0; color: var(--mp-accent); text-decoration: none;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
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
  readonly nodeId = input<string | null>(null);

  /** 1.47.0: a library root whose tags are shown (instead of a node). */
  readonly libraryId = input<string | null>(null);

  /** What the panel is for: the library root when one is given, else the node; null while neither is set. */
  readonly target = computed<InfoTarget | null>(() => {
    const library = this.libraryId();
    if (library) return { libraryId: library };
    const node = this.nodeId();
    return node ? { nodeId: node } : null;
  });

  /** Buckets whose "+N more" request is running. */
  readonly loadingMore = signal<ReadonlySet<FacetBucket>>(new Set());

  /** The panel as loaded (null: nothing indexed, or not available) - the summary hides its plain-text lines for buckets shown here. */
  readonly loaded = output<InfoPanelDto | null>();

  readonly panel = signal<InfoPanelDto | null>(null);

  readonly buckets = computed(() =>
    [...(this.panel()?.buckets ?? [])]
      .filter((b) => b.chips.length > 0)
      .sort((a, b) => bucketRank(a.bucket) - bucketRank(b.bucket)),
  );

  /** A single work's places when it lives in more than one (a split series, or one series in two libraries). */
  readonly locations = computed(() => (this.panel()?.mode === 'Work' ? this.panel()?.work?.locations ?? [] : []));

  private readonly container = computed(() => this.panel()?.mode === 'Container');

  /** Bumped per load, so a late answer for an earlier node is dropped. */
  private generation = 0;

  constructor() {
    effect(() => {
      const target = this.target();
      untracked(() => this.load(target));
    });
  }

  label(bucket: FacetBucket): string {
    return bucketLabel(bucket);
  }

  text(bucket: FacetBucket, chip: FacetChipDto): string {
    return chipText(bucket, chip);
  }

  title(bucket: FacetBucket, chip: FacetChipDto): string {
    return chipTitle(bucket, chip, this.container());
  }

  role(chip: FacetChipDto): string | null {
    return roleLabel(chip.roles);
  }

  count(chip: FacetChipDto): string | null {
    return chipCount(chip, this.container());
  }

  place(loc: FacetLocationDto): string {
    return locationText(loc);
  }

  /** Admins: creator chips carry the Rename / Merge / Split menu. */
  menuFor(bucket: FacetBucket): boolean {
    return bucket === 'Creators' && this.auth.isAdmin();
  }

  /** A chip was followed: leave the overlay (the search page replaces what is behind it). */
  opened(): void {
    this.dialogRef?.close();
    this.sheetRef?.dismiss();
  }

  async rename(chip: FacetChipDto): Promise<void> {
    if (await this.creatorAdmin.rename(chip)) this.load(this.target());
  }

  async merge(chip: FacetChipDto): Promise<void> {
    if (await this.creatorAdmin.merge(chip)) this.load(this.target());
  }

  async split(chip: FacetChipDto): Promise<void> {
    if (await this.creatorAdmin.split(chip)) this.load(this.target());
  }

  /**
   * "+N more" (1.47.0): the bucket's next chips, from the offset of the chips shown, appended in place (the server's order and the
   * viewer's counts, as the panel). The button stays while more remain; a failed request leaves the bucket as it was.
   */
  showMore(bucket: FacetBucketDto): void {
    const target = this.target();
    if (!target || this.loadingMore().has(bucket.bucket)) return;
    const generation = this.generation;
    this.setLoadingMore(bucket.bucket, true);
    this.api.getBucket(target, bucket.bucket, bucket.chips.length).subscribe({
      next: (page) => {
        this.setLoadingMore(bucket.bucket, false);
        if (generation !== this.generation) return;
        this.panel.update((panel) => {
          if (!panel) return panel;
          return {
            ...panel,
            buckets: panel.buckets.map((b) => {
              if (b.bucket !== page.bucket) return b;
              const known = new Set(b.chips.map((c) => c.id));
              const chips = [...b.chips, ...page.chips.filter((c) => !known.has(c.id))];
              return { ...b, chips, more: Math.max(0, page.total - chips.length) };
            }),
          };
        });
      },
      error: () => this.setLoadingMore(bucket.bucket, false),
    });
  }

  private setLoadingMore(bucket: FacetBucket, on: boolean): void {
    this.loadingMore.update((set) => {
      const next = new Set(set);
      if (on) next.add(bucket);
      else next.delete(bucket);
      return next;
    });
  }

  private load(target: InfoTarget | null): void {
    const generation = ++this.generation;
    if (!target) {
      this.panel.set(null);
      return;
    }
    const answer = target.libraryId !== undefined ? this.api.getLibraryInfo(target.libraryId) : this.api.getInfo(target.nodeId);
    answer.subscribe({
      next: (panel) => {
        if (generation !== this.generation) return;
        this.panel.set(panel);
        const shown = panel.mode !== 'None' && panel.buckets.some((b) => b.chips.length > 0);
        this.loaded.emit(shown ? panel : null);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.panel.set(null);
        this.loaded.emit(null);
      },
    });
  }
}
