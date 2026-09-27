import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MetadataReviewItemDto, MetadataReviewTab } from '../../../core/api/api-types';
import {
  MATCH_LEVEL_LABELS,
  overallScoreTip,
  reasonLabel,
  reasonTip,
  reviewCandidateLine,
  scorePercentLabel,
  workClassLabel,
} from '../admin-metadata/metadata-admin-labels';
import { MetadataApiService } from '../metadata-api.service';

/** A row action; `rank` for Accept (the chosen stored candidate). */
export type ReviewRowAction =
  | 'accept' | 'identify' | 'dontMatch' | 'confirm' | 'unlink' | 'clearDontMatch'
  | 'reattach' | 'deleteMissing' | 'rerun';

export interface ReviewRowActionEvent {
  action: ReviewRowAction;
  item: MetadataReviewItemDto;
  rank?: number;
}

export interface ReviewActionDef {
  action: ReviewRowAction;
  label: string;
  icon: string;
  /** The keyboard key shown in the tooltip. */
  key?: string;
  primary?: boolean;
}

/** The row actions each tab offers, in button order (the phone bottom bar uses the same list). */
export function rowActions(tab: MetadataReviewTab, item: MetadataReviewItemDto): ReviewActionDef[] {
  switch (tab) {
    case 'NeedsReview':
      return [
        { action: 'accept', label: 'Accept', icon: 'check', key: 'a', primary: true },
        { action: 'identify', label: 'Identify…', icon: 'travel_explore', key: 'i' },
        { action: 'dontMatch', label: 'Don\'t match', icon: 'block', key: 'd' },
      ];
    case 'AutoLinked':
      return [
        { action: 'confirm', label: 'Confirm', icon: 'verified', key: 'c', primary: true },
        { action: 'identify', label: 'Change…', icon: 'travel_explore', key: 'i' },
        { action: 'unlink', label: 'Unlink', icon: 'link_off', key: 'u' },
        { action: 'dontMatch', label: 'Don\'t match', icon: 'block', key: 'd' },
      ];
    case 'Unmatched':
      return [
        { action: 'identify', label: 'Identify…', icon: 'travel_explore', key: 'i', primary: true },
        { action: 'dontMatch', label: 'Don\'t match', icon: 'block', key: 'd' },
      ];
    case 'DontMatch':
      return [{ action: 'clearDontMatch', label: 'Clear Don\'t match', icon: 'undo', primary: true }];
    case 'Confirmed':
      return [
        { action: 'identify', label: 'Change…', icon: 'travel_explore', key: 'i' },
        { action: 'unlink', label: 'Unlink', icon: 'link_off', key: 'u' },
      ];
    case 'MissingFolders':
      return [
        { action: 'reattach', label: 'Re-attach to…', icon: 'drive_file_move', primary: true },
        { action: 'deleteMissing', label: 'Delete', icon: 'delete' },
      ];
    default:
      return item.link ? [{ action: 'identify', label: 'Change…', icon: 'travel_explore' }] : [];
  }
}

/**
 * One review row (metadata stage 2, design section 5). Desktop: a list row with the
 * stored candidates as a radio list and inline actions. Phone: a card; actions move to
 * the page's bottom bar for the focused row (`compact`).
 *
 * Candidate posters are NOT loaded until the row is expanded: each one costs a
 * provider request through the candidate-image token path. The linked record's poster
 * and archive covers are stored locally and always shown.
 */
@Component({
  selector: 'app-review-row',
  standalone: true,
  imports: [DatePipe, MatButtonModule, MatCheckboxModule, MatIconModule, MatRadioModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let it = item();
    <div class="row" [class.focused]="focused()" [class.selected]="selected()" [class.compact]="compact()"
         [attr.data-node]="it.nodeId" data-testid="review-row">
      <div class="head">
        <mat-checkbox class="sel" [checked]="selected()" (change)="toggleSelect.emit()" [attr.aria-label]="'Select ' + it.displayName"
                      (click)="$event.stopPropagation()" />
        <button type="button" class="thumb" (click)="focusRow.emit()" [attr.aria-label]="'Focus ' + it.displayName" tabindex="-1">
          @if (thumbUrl(); as url) {
            <img [src]="url" alt="" loading="lazy" (error)="thumbFailed.set(true)">
          } @else {
            <mat-icon>{{ it.nodeKind === 'Archive' ? 'description' : 'folder' }}</mat-icon>
          }
        </button>
        <div class="title">
          <button type="button" class="name" (click)="focusRow.emit()" data-testid="review-name">{{ it.displayName }}</button>
          <div class="where">
            <span>{{ it.libraryName }}</span>
            @for (t of it.trail ?? []; track $index) { <span class="sep">›</span><span>{{ t }}</span> }
          </div>
          <div class="facts">
            @if (it.nodeKind === 'Archive') {
              <span class="tag kind" data-testid="review-archive">Archive</span>
            }
            @if (groupSize() > 1) {
              <span class="tag group" data-testid="review-group" matTooltip="These archives are matched together as one work">
                Archive group · {{ groupSize() }} archives</span>
            }
            @if (workClass()) { <span class="tag">{{ workClass() }}</span> }
            @if (it.matchLevel && it.matchLevel !== 'None' && it.matchLevel !== 'Folder') { <span class="tag">{{ matchLevel() }}</span> }
            <span class="count">{{ it.itemCount }} item{{ it.itemCount === 1 ? '' : 's' }}</span>
            @if (it.openFlagCount > 0) {
              <span class="tag flag" matTooltip="A reader reported this series as wrong"><mat-icon inline>flag</mat-icon> {{ it.openFlagCount }}</span>
            }
            @for (r of it.reasons ?? []; track r) {
              <span class="chip" [matTooltip]="tip(r)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                    [matTooltipHideDelay]="0" data-testid="review-reason">{{ reason(r) }}</span>
            }
          </div>
        </div>
        @if (hasCandidates()) {
          <button mat-icon-button type="button" class="expand" (click)="toggleExpand.emit()"
                  [attr.aria-expanded]="expanded()" [attr.aria-label]="expanded() ? 'Hide covers' : 'Show candidate covers'"
                  [matTooltip]="expanded() ? 'Hide covers' : 'Show candidate covers (each costs one request)'" data-testid="review-expand">
            <mat-icon>{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
          </button>
        }
      </div>

      @if (it.link; as link) {
        @if (link.state !== 'NeedsReview' && link.state !== 'DontMatch') {
          <p class="link" data-testid="review-link">
            <mat-icon inline>link</mat-icon>
            {{ link.title || link.externalId }}
            <span class="muted">· {{ link.state === 'Auto' ? 'auto' : 'confirmed' }}
              @if (link.matchScore !== null && link.matchScore !== undefined) { · Overall {{ scoreLabel(link.matchScore) }} }
              · {{ link.updatedAt | date: 'mediumDate' }}</span>
          </p>
        }
      }
      @if (it.missing || tab() === 'MissingFolders') {
        <p class="note">This folder is gone from the library. Re-attach its link, precedence and reading defaults to the folder it
          became, or delete them.</p>
      }
      @if (it.nextRetryAt) {
        <p class="note">Next automatic try {{ it.nextRetryAt | date: 'mediumDate' }}.</p>
      }

      @if (hasCandidates()) {
        <mat-radio-group class="cands" [value]="rank()" (change)="choose.emit($event.value)"
                         [attr.aria-label]="'Candidates for ' + it.displayName">
          @for (c of it.candidates; track c.rank) {
            <mat-radio-button [value]="c.rank" class="cand" data-testid="review-candidate">
              <span class="cand-body">
                @if (expanded() && c.imageToken) {
                  <img class="poster" [src]="posterUrl(c.imageToken)" alt="" loading="lazy" data-testid="review-poster">
                }
                <span class="cand-text">
                  <span class="cand-title">{{ c.title }}</span>
                  <span class="muted">{{ line(c) }}</span>
                </span>
                <span class="score" [matTooltip]="overallTip(c)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                      [matTooltipHideDelay]="0">{{ scoreLabel(c.adjustedScore) }}</span>
                @for (r of c.reasons ?? []; track r) {
                  <span class="chip small" [matTooltip]="tip(r)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                        [matTooltipHideDelay]="0">{{ reason(r) }}</span>
                }
              </span>
            </mat-radio-button>
          }
        </mat-radio-group>
      }

      @if (!compact()) {
        <div class="actions">
          @for (a of actions(); track a.action) {
            @if (a.primary) {
              <button mat-flat-button type="button" (click)="emit(a.action)" [disabled]="disabled(a.action)"
                      [matTooltip]="a.key ? 'Key: ' + a.key : ''" [attr.data-testid]="'review-' + a.action">
                <mat-icon>{{ a.icon }}</mat-icon> {{ a.label }}</button>
            } @else {
              <button mat-stroked-button type="button" (click)="emit(a.action)" [disabled]="disabled(a.action)"
                      [matTooltip]="a.key ? 'Key: ' + a.key : ''" [attr.data-testid]="'review-' + a.action">
                <mat-icon>{{ a.icon }}</mat-icon> {{ a.label }}</button>
            }
          }
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .row { padding: 10px 12px; border-radius: 10px; background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.06);
      outline: none; transition: border-color 120ms; }
    .row.focused { border-color: #b39dff; box-shadow: 0 0 0 1px #b39dff inset; }
    .row.selected { background: #242036; }
    .head { display: flex; align-items: flex-start; gap: 10px; }
    .sel { margin-top: 6px; }
    .thumb { flex: none; width: 48px; height: 68px; border-radius: 6px; overflow: hidden; background: #2a2a36; border: 0; padding: 0;
      display: flex; align-items: center; justify-content: center; color: #8a8a99; cursor: pointer; }
    .thumb img { width: 100%; height: 100%; object-fit: cover; }
    .title { flex: 1 1 auto; min-width: 0; }
    .name { all: unset; cursor: pointer; font-weight: 500; font-size: 15px; overflow-wrap: anywhere; }
    .name:focus-visible { outline: 2px solid #b39dff; }
    .where { font-size: 12px; color: #9a9aa8; display: flex; flex-wrap: wrap; gap: 4px; margin: 2px 0; }
    .sep { opacity: 0.6; }
    .facts { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 12px; }
    .count { color: #9a9aa8; }
    .tag { padding: 0 6px; border-radius: 4px; background: rgba(255, 255, 255, 0.07); line-height: 20px; }
    .tag.kind { background: rgba(100, 181, 246, 0.18); color: #90caf9; }
    .tag.group { background: rgba(255, 183, 77, 0.16); color: #ffcc80; }
    .tag.flag { background: rgba(244, 67, 54, 0.18); color: #ff8a80; }
    .chip { padding: 0 8px; border-radius: 10px; background: rgba(179, 157, 255, 0.16); color: #d8ccff; line-height: 20px; }
    .chip.small { font-size: 11px; line-height: 18px; }
    .expand { flex: none; }
    .link { margin: 6px 0 0 58px; font-size: 13px; }
    .note { margin: 6px 0 0 58px; font-size: 12px; color: #9a9aa8; }
    .muted { color: #9a9aa8; }
    .cands { display: flex; flex-direction: column; margin: 6px 0 0 50px; }
    .cand { display: block; }
    .cand-body { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .poster { width: 40px; height: 56px; object-fit: cover; border-radius: 4px; background: #2a2a36; }
    .cand-text { display: flex; flex-direction: column; min-width: 180px; }
    .cand-title { font-weight: 500; }
    .score { font-variant-numeric: tabular-nums; font-weight: 600; color: #c5e1a5; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0 0 58px; }
    .actions mat-icon { margin-right: 2px; }
    .row.compact .link, .row.compact .note { margin-left: 0; }
    .row.compact .cands { margin-left: 0; }
    .row.compact .sel { display: none; }
    .row.compact.selected .sel, :host-context(.select-mode) .row.compact .sel { display: inline-flex; }
  `],
})
export class ReviewRowComponent {
  private readonly api = inject(MetadataApiService);

  readonly item = input.required<MetadataReviewItemDto>();
  readonly tab = input.required<MetadataReviewTab>();
  readonly focused = input(false);
  readonly selected = input(false);
  readonly expanded = input(false);
  /** Phone card: no inline actions (the page's bottom bar has them). */
  readonly compact = input(false);
  /** The chosen candidate rank (defaults to 1). */
  readonly rank = input(1);

  readonly action = output<ReviewRowActionEvent>();
  readonly toggleSelect = output<void>();
  readonly toggleExpand = output<void>();
  readonly choose = output<number>();
  readonly focusRow = output<void>();

  /** A local image that failed to load (no cover yet): show the kind icon instead. */
  readonly thumbFailed = signal(false);

  readonly actions = computed(() => rowActions(this.tab(), this.item()));
  readonly hasCandidates = computed(() => (this.item().candidates ?? []).length > 0);
  readonly groupSize = computed(() => {
    const members = this.item().memberNodeIds ?? [];
    return members.length > 0 ? members.length + 1 : 0;
  });
  readonly workClass = computed(() => workClassLabel(this.item().workClass));
  readonly matchLevel = computed(() => {
    const level = this.item().matchLevel;
    return level ? MATCH_LEVEL_LABELS[level] ?? level : '';
  });

  /**
   * Local images only: the stored poster of the current link, else the node's own cover - an
   * archive's, or a folder's first archive as browse shows it - so it can be compared with the
   * candidates' covers (owner, 1.26.x).
   */
  readonly thumbUrl = computed(() => {
    const it = this.item();
    if (this.thumbFailed()) return null;
    if (it.link?.imageUrl) return it.link.imageUrl;
    if (it.coverUrl) return it.coverUrl;
    return it.nodeKind === 'Archive' && !it.missing ? `/api/v1/items/${encodeURIComponent(it.nodeId)}/cover` : null;
  });

  readonly reason = reasonLabel;
  readonly tip = reasonTip;
  readonly scoreLabel = scorePercentLabel;
  readonly overallTip = overallScoreTip;
  readonly line = reviewCandidateLine;
  /**
   * A short delay before a reason/score chip's tooltip shows, with an immediate hide:
   * sweeping the mouse across several adjacent chips (a candidate's reasons, then the
   * next candidate's score right below) no longer pops more than one tooltip at a time,
   * nor leaves the previous one's fade-out overlapping the next (owner bug report, 1.27.0).
   */
  readonly TOOLTIP_SHOW_DELAY = 200;

  posterUrl(token: string): string {
    return this.api.candidateImageUrl(token);
  }

  disabled(action: ReviewRowAction): boolean {
    return action === 'accept' && !this.hasCandidates();
  }

  emit(action: ReviewRowAction): void {
    this.action.emit({ action, item: this.item(), rank: action === 'accept' ? this.rank() : undefined });
  }
}
