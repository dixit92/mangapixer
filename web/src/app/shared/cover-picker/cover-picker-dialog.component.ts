import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';

import { ApiError, CoverChoiceRequest, CoverOptionDto, CoverOptionsDto, CoverStateDto, WebCoverDto, WebCoverGroupDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../cover-image.directive';
import { CoverApiService } from './cover-api.service';
import { CoverStateService } from './cover-state.service';
import { CoverPickerDialogData, CoverPickerDialogResult } from './cover-picker-dialog.service';
import { automaticLabel, cardSource, modeLabel, webCoverLabel, webUnavailableLabel } from './cover-labels';

/** What the admin picked in the dialog. */
export type CoverPick =
  | { kind: 'automatic' }
  | { kind: 'file' }
  | { kind: 'crop'; side: 'Left' | 'Right' }
  | { kind: 'archive'; archiveId: string }
  | { kind: 'web'; coverId: string };

/** The request (or `null` = back to Automatic) for a pick. */
export function choiceFor(pick: CoverPick): CoverChoiceRequest | null {
  switch (pick.kind) {
    case 'automatic': return null;
    case 'file': return { mode: 'FilePinned' };
    case 'crop': return { mode: 'Crop', cropSide: pick.side };
    case 'archive': return { mode: 'Archive', archiveId: pick.archiveId };
    case 'web': return { mode: 'VolumeCover', volumeCoverId: pick.coverId };
  }
}

function samePick(a: CoverPick | null, b: CoverPick): boolean {
  if (!a || a.kind !== b.kind) return false;
  switch (b.kind) {
    case 'crop': return (a as { side: string }).side === b.side;
    case 'archive': return (a as { archiveId: string }).archiveId === b.archiveId;
    case 'web': return (a as { coverId: string }).coverId === b.coverId;
    default: return true;
  }
}

/**
 * The admin "Choose cover..." picker (1.29.0, design 6.7): Automatic (with what it uses now and why), this file's cover
 * ("use the file's cover" also stops every automatic cover), either half of page 1, another item's cover, and the stored
 * covers from the web of the linked series (grouped by volume; a not yet downloaded cover is shown but cannot be picked
 * here). Works under "Don't match", in unlinked libraries and with series information hidden - the web part then says why.
 * 1.36.0: on a folder that is not a series itself, "Covers from the web" offers the stored covers of the series linked below it,
 * one heading per series, and comes FIRST - above "Another item's cover" (owner: chapter covers are rarely real covers); a series
 * folder keeps today's order. Every image comes from MangaPixer. On "Use this cover" the new versioned card URL is announced through
 * `CoverStateService`, so the card changes in place.
 */
@Component({
  selector: 'app-cover-picker-dialog',
  standalone: true,
  imports: [NgTemplateOutlet, MatButtonModule, MatDialogModule, MatIconModule, MatProgressSpinnerModule, CoverImageDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <h2 mat-dialog-title class="title" [title]="data.displayName">Choose cover · {{ data.displayName }}</h2>
      <button mat-icon-button mat-dialog-close aria-label="Close" data-testid="cover-picker-close"><mat-icon>close</mat-icon></button>
    </div>
    <mat-dialog-content class="body">
      @if (loading()) {
        <div class="center"><mat-spinner diameter="28" /></div>
      } @else if (options(); as o) {
        <p class="now" data-testid="cover-picker-current">
          <span class="mode">{{ modeLabel(o.current.mode) }}</span>
          @if (o.current.mode === 'Automatic') { · now {{ automaticLabel(o.current) }} }
        </p>

        <div class="grid" role="group" aria-label="This item">
          <button type="button" class="tile" [class.picked]="isPicked({ kind: 'automatic' })" [attr.aria-pressed]="isPicked({ kind: 'automatic' })"
                  (click)="pick({ kind: 'automatic' })" data-testid="cover-pick-automatic">
            <span class="img">
              @if (o.current.mode === 'Automatic' && o.current.imageUrl) { <img appCover [src]="o.current.imageUrl" alt=""> }
              @else { <mat-icon>auto_awesome</mat-icon> }
            </span>
            <span class="label">Automatic</span>
          </button>
          @for (opt of ownOptions(); track opt.kind) {
            <button type="button" class="tile" [class.picked]="isPicked(pickOf(opt))" [attr.aria-pressed]="isPicked(pickOf(opt))"
                    (click)="pick(pickOf(opt))" [attr.data-testid]="'cover-pick-' + opt.kind">
              <span class="img"><img appCover [src]="opt.imageUrl" alt=""><mat-icon class="fallback">image</mat-icon></span>
              <span class="label">{{ opt.label }}</span>
            </button>
          }
        </div>

        @if (seriesCovers().length > 0) {
          <h3 class="section">Covers from the web</h3>
          <p class="hint" data-testid="cover-picker-web-series-hint">From the series inside this folder.</p>
          @for (s of seriesCovers(); track s.nodeId) {
            <section class="series" [attr.aria-label]="s.displayName" [attr.data-testid]="'cover-picker-series-' + s.nodeId">
              <h4 class="series-name" [title]="s.displayName">{{ s.displayName }}</h4>
              @if (seriesSubtitle(s.displayName, s.seriesTitle); as subtitle) {
                <p class="series-title" [title]="subtitle">{{ subtitle }}</p>
              }
              <ng-container *ngTemplateOutlet="webGroups; context: { $implicit: s.groups }" />
            </section>
          }
          @if (o.webSeriesMore) {
            <p class="hint" data-testid="cover-picker-web-series-more">
              {{ o.webSeriesMore === 1 ? '1 more series inside has' : o.webSeriesMore + ' more series inside have' }} covers from the web (not shown).
            </p>
          }
        }

        @if (archiveOptions().length > 0) {
          <h3 class="section">Another item's cover</h3>
          <div class="grid" role="group" aria-label="Another item's cover">
            @for (opt of archiveOptions(); track opt.archiveId) {
              <button type="button" class="tile" [class.picked]="isPicked(pickOf(opt))" [attr.aria-pressed]="isPicked(pickOf(opt))"
                      (click)="pick(pickOf(opt))" [attr.data-testid]="'cover-pick-archive-' + opt.archiveId">
                <span class="img"><img appCover [src]="opt.imageUrl" alt="" loading="lazy"><mat-icon class="fallback">image</mat-icon></span>
                <span class="label" [title]="opt.label">{{ opt.label }}</span>
              </button>
            }
          </div>
        }

        @if (seriesCovers().length === 0) {
          <h3 class="section">Covers from the web</h3>
          @if (!o.webAvailable || o.web.length === 0) {
            <p class="hint" data-testid="cover-picker-web-unavailable">{{ webUnavailable(o) }}</p>
          } @else {
            <ng-container *ngTemplateOutlet="webGroups; context: { $implicit: o.web }" />
          }
        }
      } @else {
        <p class="hint" role="alert">{{ error() }}</p>
      }
    </mat-dialog-content>
    <ng-template #webGroups let-groups>
      @for (group of asGroups(groups); track group.volume) {
        <div class="volume">
          <span class="vol-label">{{ group.volume === null || group.volume === undefined ? 'Series' : 'Volume ' + group.volume }}</span>
          <div class="grid" role="group" [attr.aria-label]="group.volume === null || group.volume === undefined ? 'Series covers' : 'Volume ' + group.volume">
            @for (c of group.covers; track c.id) {
              <button type="button" class="tile" [class.picked]="isPicked({ kind: 'web', coverId: c.id })"
                      [attr.aria-pressed]="isPicked({ kind: 'web', coverId: c.id })" [disabled]="!c.stored"
                      (click)="pick({ kind: 'web', coverId: c.id })" [attr.data-testid]="'cover-pick-web-' + c.id">
                <span class="img">
                  @if (c.imageUrl) { <img appCover [src]="c.imageUrl" alt="" loading="lazy"> }
                  <mat-icon class="fallback">cloud</mat-icon>
                </span>
                <span class="label">{{ webLabel(c) }}</span>
              </button>
            }
          </div>
        </div>
      }
    </ng-template>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close data-testid="cover-picker-cancel">Cancel</button>
      <button mat-flat-button [disabled]="!canApply()" (click)="apply()" data-testid="cover-picker-apply">Use this cover</button>
    </mat-dialog-actions>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; height: 100%; max-height: inherit; }
    .head { display: flex; align-items: center; gap: 8px; padding-right: 8px; }
    .title { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .body { display: block; }
    .center { display: flex; justify-content: center; padding: 32px; }
    .now { margin: 0 0 12px; color: #c8c8d4; font-size: 13px; }
    .now .mode { font-weight: 600; color: #fff; }
    .section { margin: 18px 0 8px; font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99; }
    .hint { margin: 0; color: #9a9aa8; font-size: 13px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(104px, 1fr)); gap: 10px; }
    .tile {
      display: flex; flex-direction: column; gap: 4px; padding: 4px; border: 2px solid transparent; border-radius: 6px;
      background: transparent; color: inherit; font: inherit; cursor: pointer; text-align: left; min-width: 0;
    }
    .tile:hover:not([disabled]) { background: rgba(255, 255, 255, 0.06); }
    .tile:focus-visible { outline: 2px solid #7c9cff; outline-offset: 2px; }
    .tile.picked { border-color: #7c9cff; background: rgba(124, 156, 255, 0.12); }
    .tile[disabled] { cursor: default; opacity: 0.55; }
    .img {
      position: relative; display: flex; align-items: center; justify-content: center; aspect-ratio: 2 / 3; width: 100%;
      overflow: hidden; border-radius: 4px; background: #2a2a33;
    }
    .img img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; z-index: 1; }
    .img mat-icon { color: #777; }
    .label { font-size: 12px; line-height: 1.3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .volume { margin-bottom: 10px; }
    .vol-label { display: block; margin-bottom: 4px; font-size: 12px; color: #c8c8d4; }
    .series { margin: 12px 0 4px; min-width: 0; }
    .series-name, .series-title { margin: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .series-name { font-size: 14px; font-weight: 600; color: #fff; }
    .series-title { margin-top: 2px; font-size: 12px; color: #9a9aa8; }
    .series .volume:first-of-type { margin-top: 6px; }
    @media (max-width: 599.98px) {
      /* Full-screen on phone: the options fill the screen, the actions stay at the bottom. */
      .body { flex: 1 1 auto; max-height: none; }
      .grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
    }
  `],
})
export class CoverPickerDialogComponent implements OnInit {
  readonly data = inject<CoverPickerDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<CoverPickerDialogComponent, CoverPickerDialogResult>>(MatDialogRef);
  private readonly api = inject(CoverApiService);
  private readonly coverState = inject(CoverStateService);
  private readonly snackBar = inject(MatSnackBar);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly options = signal<CoverOptionsDto | null>(null);
  readonly error = signal<string>('');
  readonly picked = signal<CoverPick | null>(null);

  readonly ownOptions = computed(() => (this.options()?.local ?? []).filter((o) => o.kind !== 'Archive'));
  readonly archiveOptions = computed(() => (this.options()?.local ?? []).filter((o) => o.kind === 'Archive'));
  /** 1.36.0: a folder that is not a series - the covers of the series inside it (then shown first). */
  readonly seriesCovers = computed(() => this.options()?.webSeries ?? []);
  readonly canApply = computed(() => !this.busy() && !this.loading() && this.picked() !== null && this.options() !== null);

  readonly modeLabel = modeLabel;
  readonly automaticLabel = automaticLabel;

  ngOnInit(): void {
    this.api.getOptions(this.data.nodeId).subscribe({
      next: (o) => {
        this.options.set(o);
        this.picked.set(initialPick(o.current.mode));
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.error.set(e.message || 'The cover options could not be loaded.');
        this.loading.set(false);
      },
    });
  }

  pickOf(opt: CoverOptionDto): CoverPick {
    switch (opt.kind) {
      case 'File': return { kind: 'file' };
      case 'CropLeft': return { kind: 'crop', side: 'Left' };
      case 'CropRight': return { kind: 'crop', side: 'Right' };
      case 'Archive': return { kind: 'archive', archiveId: opt.archiveId ?? '' };
    }
  }

  pick(p: CoverPick): void {
    this.picked.set(p);
  }

  isPicked(p: CoverPick): boolean {
    return samePick(this.picked(), p);
  }

  webLabel(c: WebCoverDto): string {
    return webCoverLabel(c);
  }

  /** The template context is untyped: the volume groups it carries. */
  asGroups(groups: unknown): WebCoverGroupDto[] {
    return groups as WebCoverGroupDto[];
  }

  /** The linked record's title under a series heading, unless it only repeats the folder name. */
  seriesSubtitle(displayName: string, title: string | null | undefined): string | null {
    if (!title) return null;
    return title.trim().toLocaleLowerCase() === displayName.trim().toLocaleLowerCase() ? null : title;
  }

  webUnavailable(o: CoverOptionsDto): string {
    return webUnavailableLabel(o.webAvailable ? 'no_companion' : o.webUnavailableReason);
  }

  apply(): void {
    const p = this.picked();
    if (!p || this.busy()) return;
    const request = choiceFor(p);
    const call: Observable<CoverStateDto> = request === null
      ? this.api.clearChoice(this.data.nodeId)
      : this.api.setChoice(this.data.nodeId, request);
    this.busy.set(true);
    call.subscribe({
      next: (state) => {
        this.busy.set(false);
        this.coverState.announce({ nodeId: this.data.nodeId, coverUrl: state.imageUrl ?? null, coverSource: cardSource(state) });
        this.snackBar.open('Cover updated.', undefined, { duration: 3000 });
        this.ref.close(state);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.snackBar.open(e.message || 'The cover could not be changed.', 'OK', { duration: 6000 });
      },
    });
  }
}

/** The pick shown selected when the dialog opens (the modes without a target id). */
function initialPick(mode: CoverStateDto['mode']): CoverPick | null {
  switch (mode) {
    case 'Automatic': return { kind: 'automatic' };
    case 'FilePinned': return { kind: 'file' };
    default: return null;
  }
}
