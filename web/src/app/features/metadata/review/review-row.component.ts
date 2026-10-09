import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { MetadataReviewCandidateDto, MetadataReviewItemDto, MetadataReviewTab } from '../../../core/api/api-types';
import { DUPLICATE_TIP, duplicateCountLabel } from '../../../shared/duplicate-units';
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
import { collectionLabel } from '../collection-labels';
import { artistFolderLabel } from '../artist-folder/artist-folder-labels';
import { CoverCompareDirective } from './cover-compare/cover-compare.directive';
import { QueuedImageDirective, QueuedImageState } from './queued-image.directive';
import { candidateBlocks, FAMILY_REASONS, familyRoleLabel, SERIES_FAMILY_NOTE } from './series-family';

/** A row action; `rank` for Accept (the chosen stored candidate). */
export type ReviewRowAction =
  | 'accept' | 'identify' | 'dontMatch' | 'later' | 'notLater' | 'confirm' | 'unlink' | 'clearDontMatch'
  | 'reattach' | 'deleteMissing' | 'rerun'
  // 1.34.0: "Collection about" a series.
  | 'acceptCollection' | 'changeCollection' | 'clearCollection'
  // 1.34.1: mark a waiting folder "Collection about" a series you pick (Identify in collection mode).
  | 'collectionAbout'
  // 1.37.0: mark a waiting folder one artist's folder (key r), or remove that mark (Collections tab).
  | 'artistFolder' | 'clearArtistFolder';

export interface ReviewRowActionEvent {
  action: ReviewRowAction;
  item: MetadataReviewItemDto;
  rank?: number;
}

/** 1.33.0: a group of waiting works to filter Needs review (1.34.0: or Unmatched) by - an author, or a folder. */
export interface ReviewGroup {
  kind: 'author' | 'folder';
  key: string;
  label: string;
}

export interface ReviewActionDef {
  action: ReviewRowAction;
  label: string;
  icon: string;
  /** The keyboard key shown in the tooltip. */
  key?: string;
  primary?: boolean;
  /** A shorter label for the phone bottom bar. */
  short?: string;
}

/** 1.34.0: the first action of a waiting folder that looks like a collection about a series. */
const ACCEPT_COLLECTION: ReviewActionDef = {
  action: 'acceptCollection', label: 'Accept as collection', short: 'Collection', icon: 'collections_bookmark', key: 'f', primary: true,
};

/** 1.34.1: any waiting folder can be marked a collection about a series picked in Identify (not only a suggested one). */
const COLLECTION_ABOUT: ReviewActionDef = {
  action: 'collectionAbout', label: 'Collection about…', short: 'Collection', icon: 'collections_bookmark', key: 'f',
};

/** 1.37.0: a waiting FOLDER can be marked one artist's folder (key r): its works are then matched one by one. */
const ARTIST_FOLDER: ReviewActionDef = { action: 'artistFolder', label: 'Artist folder…', short: 'Artist', icon: 'palette', key: 'r' };

/** The row actions each tab offers, in button order (the phone bottom bar uses the same list). */
export function rowActions(tab: MetadataReviewTab, item: MetadataReviewItemDto): ReviewActionDef[] {
  switch (tab) {
    case 'NeedsReview':
      return [
        // 1.34.0: a folder that looks like a collection about a series offers that first.
        ...(item.collection ? [ACCEPT_COLLECTION] : []),
        { action: 'accept', label: 'Accept', icon: 'check', key: 'a', primary: !item.collection },
        { action: 'identify', label: 'Identify…', icon: 'travel_explore', key: 'i' },
        ...(item.nodeKind === 'Folder' && !item.collection ? [COLLECTION_ABOUT] : []),
        ...(item.nodeKind === 'Folder' ? [ARTIST_FOLDER] : []),
        { action: 'dontMatch', label: 'Don\'t match', icon: 'block', key: 'd' },
        // 1.33.0: remembered on the server - the row goes to the end of Needs review for every admin until it is decided.
        item.laterAt
          ? { action: 'notLater', label: 'Not later', icon: 'undo', key: 'l' }
          : { action: 'later', label: 'Later', icon: 'schedule', key: 'l' },
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
        ...(item.nodeKind === 'Folder' ? [COLLECTION_ABOUT, ARTIST_FOLDER] : []),
        { action: 'dontMatch', label: 'Don\'t match', icon: 'block', key: 'd' },
      ];
    case 'DontMatch':
      return [{ action: 'clearDontMatch', label: 'Clear Don\'t match', icon: 'undo', primary: true }];
    case 'Collections':
      // 1.37.0: artist folders are listed here too (folders whose works are matched on their own).
      if (item.link?.state === 'ArtistFolder') {
        return [{ action: 'clearArtistFolder', label: 'Remove artist folder', short: 'Remove', icon: 'undo', primary: true }];
      }
      return [
        { action: 'changeCollection', label: 'Change series…', short: 'Change', icon: 'travel_explore', key: 'i' },
        { action: 'clearCollection', label: 'Clear collection', short: 'Clear', icon: 'undo', primary: true },
      ];
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
 * and archive covers are stored locally and always shown. Provider images load through
 * a small queue with retries (1.29.0): a refused one never shows as a broken image.
 */
@Component({
  selector: 'app-review-row',
  standalone: true,
  imports: [DatePipe, NgTemplateOutlet, MatButtonModule, MatCheckboxModule, MatIconModule, MatRadioModule, MatTooltipModule, RouterLink,
    CoverCompareDirective, QueuedImageDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let it = item();
    <div class="row" [class.focused]="focused()" [class.selected]="selected()" [class.compact]="compact()"
         [attr.data-node]="it.nodeId" data-testid="review-row">
      <div class="layout">
        <mat-checkbox class="sel" [checked]="selected()" (change)="toggleSelect.emit()" [attr.aria-label]="'Select ' + it.displayName"
                      (click)="$event.stopPropagation()" />
        <!-- 1.28.0 (owner): your cover next to the SELECTED series' cover, always visible and large enough to spot a
             wrong link without a mouse; hover (or tap) still opens an even larger pair. -->
        <div class="covers" data-testid="review-covers"
             [appCoverCompare]="seriesCoverUrl()" [coverCompareLocal]="localCoverUrl()" [coverCompareLabel]="seriesCoverLabel()">
          <figure class="cover">
            @if (localCoverUrl(); as url) {
              @if (!localFailed()) {
                <img [src]="url" alt="" loading="lazy" (error)="localFailed.set(true)" data-testid="review-local-cover">
              } @else {
                <mat-icon>{{ it.nodeKind === 'Archive' ? 'description' : 'folder' }}</mat-icon>
              }
            } @else {
              <mat-icon>{{ it.nodeKind === 'Archive' ? 'description' : 'folder' }}</mat-icon>
            }
            <figcaption>Yours</figcaption>
          </figure>
          <!-- 1.37.0: an artist folder names no series - only its own cover. -->
          @if (it.link?.state !== 'ArtistFolder') {
          <figure class="cover">
            @if (candidateCoverUrl(); as url) {
              <!-- A provider request: queued, retried, and "No cover" with a retry when it gives up (1.29.0). -->
              <span class="frame">
                <img #series="queuedImage" [appQueuedImage]="url" (queuedImageState)="seriesState.set($event)" alt=""
                     data-testid="review-series-cover">
                @if (seriesState() === 'failed') {
                  <button type="button" class="none retry" (click)="$event.stopPropagation(); series.retry()"
                          matTooltip="The cover could not be loaded. Tap to try again." data-testid="review-series-retry">
                    No cover<br>Retry</button>
                }
              </span>
            } @else if (seriesCoverUrl(); as url) {
              <img [src]="url" alt="" loading="lazy" data-testid="review-series-cover">
            } @else {
              <span class="none">No cover</span>
            }
            <figcaption>{{ it.link && !hasCandidates() ? 'Linked' : 'Selected' }}</figcaption>
          </figure>
          }
        </div>
        <div class="main">
      <div class="head">
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
            @if (duplicates(); as dup) {
              <span class="tag dup" [matTooltip]="dupTip" data-testid="review-duplicates">{{ dup }}</span>
            }
            @if (it.checkingAgain) {
              <span class="tag checking" matTooltip="The matcher's rules changed with an update; this item is being scored again. What is shown is the earlier result until then."
                    data-testid="review-checking-again"><mat-icon inline>autorenew</mat-icon> Checking again</span>
            }
            @if (it.laterAt) {
              <span class="tag later" [matTooltip]="'Set aside for later on ' + (it.laterAt | date: 'medium') + '. Listed after the other items until it is decided.'"
                    data-testid="review-later-tag"><mat-icon inline>schedule</mat-icon> Later</span>
            }
            @if (tab() === 'NeedsReview') {
              @if (it.collection; as c) {
                <!-- 1.34.0: doujin-shaped works by several circles in a folder named like a series - a collection about it. -->
                <span class="chip collection" data-testid="review-collection-hint"
                      matTooltip="Works by several circles in a folder named after this series: most likely works about it (fan works). Accept as collection to keep the series as context and match each work on its own.">
                  <mat-icon inline>collections_bookmark</mat-icon><span class="glabel">Looks like a collection about {{ c.title }}</span></span>
              }
            }
            @if (tab() === 'NeedsReview' || tab() === 'Unmatched') {
              @if (it.sameAuthor; as a) {
                <!-- 1.33.0: other waiting works by the same circle / artist - a tap lists them together (1.34.0: Unmatched too). -->
                <button type="button" class="chip group" (click)="$event.stopPropagation(); pickGroup('author')" data-testid="review-same-author"
                        matTooltip="Other works {{ here() }} whose names start with the same circle or artist. Show them together.">
                  <mat-icon inline>groups</mat-icon><span class="glabel">{{ a.others }} more by {{ a.label }}</span></button>
              }
              @if (it.sameFolder; as f) {
                <button type="button" class="chip group" (click)="$event.stopPropagation(); pickGroup('folder')" data-testid="review-same-folder"
                        matTooltip="Other works {{ here() }} from the same folder. Show them together.">
                  <mat-icon inline>folder</mat-icon><span class="glabel">{{ f.others }} more in {{ f.label }}</span></button>
              }
            }
            @if (it.openFlagCount > 0) {
              <span class="tag flag" matTooltip="A reader reported this series as wrong"><mat-icon inline>flag</mat-icon> {{ it.openFlagCount }}</span>
            }
            @for (r of it.reasons ?? []; track r) {
              <span class="chip" [matTooltip]="tip(r)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                    [matTooltipHideDelay]="0" data-testid="review-reason">{{ reason(r) }}</span>
            }
          </div>
        </div>
        @if (!it.missing && tab() !== 'MissingFolders') {
          <!-- 1.29.0 (owner): open the folder in browse, as the Missing tab does; an archive opens in its folder. -->
          <a mat-icon-button class="open" [routerLink]="folderLink()" (click)="$event.stopPropagation()"
             [attr.aria-label]="it.nodeKind === 'Archive' ? 'Open the folder that contains this archive' : 'Open this folder'"
             [matTooltip]="it.nodeKind === 'Archive' ? 'Open containing folder' : 'Open folder'" data-testid="review-open-folder">
            <mat-icon>folder_open</mat-icon>
          </a>
        }
        @if (hasCandidates()) {
          <button mat-icon-button type="button" class="expand" (click)="toggleExpand.emit()"
                  [attr.aria-expanded]="expanded()" [attr.aria-label]="expanded() ? 'Hide covers' : 'Show candidate covers'"
                  [matTooltip]="expanded() ? 'Hide covers' : 'Show candidate covers (each costs one request)'" data-testid="review-expand">
            <mat-icon>{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
          </button>
        }
      </div>

      @if (it.link; as link) {
        @if (link.state === 'CollectionAbout') {
          <p class="link" data-testid="review-link">
            <mat-icon inline>collections_bookmark</mat-icon>
            {{ collectionText(link.title || link.externalId) }}
            <span class="muted">· {{ link.updatedAt | date: 'mediumDate' }}</span>
          </p>
        } @else if (link.state === 'ArtistFolder') {
          <p class="link" data-testid="review-link">
            <mat-icon inline>palette</mat-icon>
            {{ artistText(it.artist?.name) }}
            <span class="muted">· {{ link.updatedAt | date: 'mediumDate' }}</span>
          </p>
        } @else if (link.state !== 'NeedsReview' && link.state !== 'DontMatch') {
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
        <!-- 1.30.0 (owner): candidates of one series family are shown together, each with its role, so a folder is not paired
             with the main series when it holds a spin-off (or the other way round). Ranks never change. -->
        <mat-radio-group class="cands" [value]="rank()" (change)="choose.emit($event.value)"
                         [attr.aria-label]="'Candidates for ' + it.displayName">
          <!-- Declared inside the group so its radio buttons belong to it. -->
          <ng-template #candidate let-c>
            <mat-radio-button [value]="c.rank" class="cand" data-testid="review-candidate">
              <span class="cand-body">
                @if (expanded() && c.imageToken) {
                  <img class="poster" [appQueuedImage]="posterUrl(c.imageToken)" alt="" data-testid="review-poster">
                }
                <span class="cand-text">
                  <span class="cand-title">{{ c.title }}</span>
                  <span class="muted">{{ line(c) }}</span>
                </span>
                @if (c.familyRole && familyOf(c)) {
                  <span class="tag role" data-testid="review-family-role">{{ roleLabel(c.familyRole) }}</span>
                }
                <span class="score" [matTooltip]="overallTip(c)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                      [matTooltipHideDelay]="0">{{ scoreLabel(c.adjustedScore) }}</span>
                @for (r of candidateReasons(c); track r) {
                  <span class="chip small" [matTooltip]="tip(r)" matTooltipPosition="above" [matTooltipShowDelay]="TOOLTIP_SHOW_DELAY"
                        [matTooltipHideDelay]="0">{{ reason(r) }}</span>
                }
              </span>
            </mat-radio-button>
          </ng-template>
          @for (b of blocks(); track b.candidates[0].rank) {
            @if (b.family) {
              <div class="family" role="group" [attr.aria-label]="familyNote" data-testid="review-family">
                <p class="family-note"><mat-icon inline>account_tree</mat-icon> {{ familyNote }}</p>
                @for (c of b.candidates; track c.rank) {
                  <ng-container *ngTemplateOutlet="candidate; context: { $implicit: c }" />
                }
              </div>
            } @else {
              <ng-container *ngTemplateOutlet="candidate; context: { $implicit: b.candidates[0] }" />
            }
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
      </div>
    </div>
  `,
  styles: [`
    .chip.collection { display: inline-flex; align-items: center; gap: 4px; max-width: 100%; min-width: 0;
      background: rgba(255, 183, 77, 0.16); border-color: rgba(255, 204, 128, 0.45); color: #ffe0b2; }
    .chip.collection .glabel { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
    :host { display: block; }
    .row { padding: 10px 12px; border-radius: 10px; background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.06);
      outline: none; transition: border-color 120ms; }
    .row.focused { border-color: #b39dff; box-shadow: 0 0 0 1px #b39dff inset; }
    .row.selected { background: #242036; }
    /* Wraps: below 520 px the covers take a line of their own above the text (flex-basis 100% in the container query). */
    .layout { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 12px; container-type: inline-size; }
    .main { flex: 1 1 260px; min-width: 0; }
    .head { display: flex; align-items: flex-start; gap: 10px; }
    .sel { margin-top: 6px; }
    .covers { flex: none; display: flex; gap: 8px; cursor: zoom-in; }
    .cover { margin: 0; display: flex; flex-direction: column; align-items: center; gap: 3px; }
    .cover img, .cover > mat-icon, .cover .none { width: 96px; height: 136px; border-radius: 6px; background: #2a2a36; object-fit: cover; }
    .cover > mat-icon, .cover .none { display: flex; align-items: center; justify-content: center; color: #8a8a99; font-size: 12px; }
    .cover figcaption { font-size: 11px; color: #9a9aa8; }
    .frame { position: relative; display: block; width: 96px; height: 136px; border-radius: 6px; background: #2a2a36; }
    .frame img { display: block; }
    .frame .retry { position: absolute; inset: 0; border: 0; cursor: pointer; text-align: center; line-height: 1.4; font: inherit;
      font-size: 12px; }
    @container (max-width: 520px) {
      .covers { flex-basis: 100%; order: -1; justify-content: center; }
      .cover img, .cover > mat-icon, .cover .none, .frame { width: min(150px, 42cqw); height: auto; aspect-ratio: 0.7; }
      .frame img { width: 100%; }
    }
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
    .tag.dup { background: rgba(255, 183, 77, 0.16); color: #ffcc80; }
    .tag.checking { background: rgba(100, 181, 246, 0.18); color: #90caf9; }
    .tag.later { background: rgba(179, 157, 255, 0.16); color: #d1c4ff; }
    .tag.role { background: rgba(128, 203, 196, 0.16); color: #a7ffeb; font-size: 11px; line-height: 18px; white-space: nowrap; }
    .family { margin: 4px 0 4px 8px; padding: 2px 8px 4px 0; border-left: 3px solid rgba(128, 203, 196, 0.55); border-radius: 0 8px 8px 0;
      background: rgba(128, 203, 196, 0.06); min-width: 0; }
    .family-note { margin: 2px 0 0 8px; font-size: 12px; color: #a7ffeb; }
    .chip { padding: 0 8px; border-radius: 10px; background: rgba(179, 157, 255, 0.16); color: #d8ccff; line-height: 20px; }
    .chip.small { font-size: 11px; line-height: 18px; }
    .chip.group { border: 1px solid rgba(255, 204, 128, 0.35); background: rgba(255, 183, 77, 0.1); color: #ffe0b2; font: inherit;
      font-size: 12px; cursor: pointer; display: inline-flex; align-items: center; gap: 3px; max-width: 100%; min-width: 0; }
    .chip.group:hover { background: rgba(255, 183, 77, 0.2); }
    .chip.group:focus-visible { outline: 2px solid #ffcc80; }
    .chip.group .glabel { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 26ch; }
    .expand, .open { flex: none; }
    .link { margin: 6px 0 0; font-size: 13px; }
    .note { margin: 6px 0 0; font-size: 12px; color: #9a9aa8; }
    .muted { color: #9a9aa8; }
    .cands { display: flex; flex-direction: column; margin: 6px 0 0 -8px; }
    .cand { display: block; }
    .cand-body { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .poster { width: 40px; height: 56px; object-fit: cover; border-radius: 4px; background: #2a2a36; }
    .cand-text { display: flex; flex-direction: column; min-width: 180px; }
    .cand-title { font-weight: 500; }
    .score { font-variant-numeric: tabular-nums; font-weight: 600; color: #c5e1a5; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0 0; }
    .actions mat-icon { margin-right: 2px; }
    .row.compact .sel { display: none; }
    .row.compact.selected .sel, :host-context(.select-mode) .row.compact .sel { display: inline-flex; }
  `],
})
export class ReviewRowComponent {
  private readonly api = inject(MetadataApiService);

  readonly item = input.required<MetadataReviewItemDto>();
  readonly tab = input.required<MetadataReviewTab>();
  /** 1.34.0: where the other works of a group are - Needs review waits, Unmatched lists. */
  readonly here = computed(() => (this.tab() === 'Unmatched' ? 'listed here' : 'waiting here'));
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
  /** 1.33.0: filter Needs review by this row's author or folder group. */
  readonly group = output<ReviewGroup>();

  /** A local image that failed to load (no cover yet): show the kind icon instead. */
  readonly localFailed = signal(false);
  /** The selected candidate's cover (a provider image): loading, loaded, or given up after its retries. */
  readonly seriesState = signal<QueuedImageState>('loading');

  readonly actions = computed(() => rowActions(this.tab(), this.item()));
  readonly hasCandidates = computed(() => (this.item().candidates ?? []).length > 0);
  /** The candidates in display order, a series family in one block (1.30.0). */
  readonly blocks = computed(() => candidateBlocks(this.item().candidates));
  readonly familyNote = SERIES_FAMILY_NOTE;
  readonly roleLabel = familyRoleLabel;
  /** 1.31.0: "2 duplicate chapters" when the same number sits in more than one file of a folder below this work; '' otherwise. */
  readonly duplicates = computed(() => duplicateCountLabel(this.item().duplicateChapters ?? 0, this.item().duplicateVolumes ?? 0));
  readonly dupTip = DUPLICATE_TIP;

  readonly groupSize = computed(() => {
    const members = this.item().memberNodeIds ?? [];
    return members.length > 0 ? members.length + 1 : 0;
  });
  readonly workClass = computed(() => workClassLabel(this.item().workClass));
  readonly matchLevel = computed(() => {
    const level = this.item().matchLevel;
    return level ? MATCH_LEVEL_LABELS[level] ?? level : '';
  });

  /** The node's own cover: an archive's, or a folder's first archive as browse shows it (1.26.x). */
  readonly localCoverUrl = computed(() => {
    const it = this.item();
    if (it.coverUrl) return it.coverUrl;
    return it.nodeKind === 'Archive' && !it.missing ? `/api/v1/items/${encodeURIComponent(it.nodeId)}/cover` : null;
  });

  /**
   * The series cover next to it (1.28.0, owner): the SELECTED candidate's image while candidates are listed (it follows
   * the radio choice; one request to MangaUpdates when it is first shown), else the link's stored poster.
   */
  readonly seriesCoverUrl = computed(() => {
    const it = this.item();
    if (this.hasCandidates()) {
      const chosen = (it.candidates ?? []).find((c) => c.rank === this.rank()) ?? (it.candidates ?? [])[0];
      return chosen?.imageToken ? this.posterUrl(chosen.imageToken) : null;
    }
    return it.link?.imageUrl ?? null;
  });

  /** The series cover when it is a provider request (a candidate's image), else null - a stored poster loads directly. */
  readonly candidateCoverUrl = computed(() => (this.hasCandidates() ? this.seriesCoverUrl() : null));

  /** The row's folder in browse: the folder itself, or an archive's containing folder (the library top level when none). */
  readonly folderLink = computed(() => {
    const it = this.item();
    const folderId = it.nodeKind === 'Archive' ? it.parentNodeId : it.nodeId;
    return folderId ? ['/libraries', it.libraryId, 'browse', folderId] : ['/libraries', it.libraryId, 'browse'];
  });

  readonly seriesCoverLabel = computed(() => (this.hasCandidates() ? 'Selected series' : 'Linked series'));

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

  /** True when the candidate is shown in a family block (a role tag without its family would say nothing). */
  familyOf(c: MetadataReviewCandidateDto): boolean {
    return this.blocks().some((b) => b.family && b.candidates.includes(c));
  }

  /** A candidate's chips; inside a family block the family chips are left out (the block's heading says it, 1.30.0). */
  candidateReasons(c: MetadataReviewCandidateDto): string[] {
    const reasons = c.reasons ?? [];
    return this.familyOf(c) ? reasons.filter((r) => !FAMILY_REASONS.has(r)) : reasons;
  }

  disabled(action: ReviewRowAction): boolean {
    return (action === 'accept' || action === 'acceptCollection') && !this.hasCandidates();
  }

  pickGroup(kind: ReviewGroup['kind']): void {
    const hint = kind === 'author' ? this.item().sameAuthor : this.item().sameFolder;
    if (hint) this.group.emit({ kind, key: hint.key, label: hint.label });
  }

  emit(action: ReviewRowAction): void {
    this.action.emit({ action, item: this.item(), rank: action === 'accept' || action === 'acceptCollection' ? this.rank() : undefined });
  }

  /** 1.37.0: "Artist folder: Name" on an artist folder's row (Collections tab). */
  artistText(name: string | null | undefined): string {
    return artistFolderLabel(name);
  }

  collectionText(title: string | null | undefined): string {
    return collectionLabel(title);
  }
}
