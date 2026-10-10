import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { VolumeViewDto } from '../../core/api/api-types';
import { ListCreditComponent } from '../../shared/list-credit.component';
import {
  ORIGIN_PLACES, STATUS_WORDS, folderLine, languageName, progressIcon, trackersLine,
} from '../metadata/progress/series-progress-labels';

export { languageName };

/**
 * What is out in the preferred language, when known: the official release ("English: 12 of 14 volumes", "English: complete"),
 * else the released chapters ("French: up to chapter 87"), else - English only, from MangaUpdates - the scanlation
 * ("English chapters: ongoing") or "English: not licensed".
 */
export function releasePart(view: VolumeViewDto): string | null {
  const name = languageName(view.language);
  if (!name) return null;
  const released = view.releasedVolumes ?? null;
  const origin = view.originVolumes ?? null;
  if (released !== null && released > 0) {
    if (origin !== null && view.seriesStatus === 'Complete' && released >= origin) return `${name}: complete`;
    if (origin !== null && origin > released) return `${name}: ${released} of ${origin} volumes`;
    return `${name}: ${released} volume${released === 1 ? '' : 's'}`;
  }
  if (view.scanlationComplete === true || view.scanlationComplete === false) {
    return `${name} chapters: ${view.scanlationComplete ? 'complete' : 'ongoing'}`;
  }
  if (view.releasedChapter) return `${name}: up to chapter ${view.releasedChapter}`;
  if (view.licensed === false) return `${name}: not licensed`;
  return null;
}

/**
 * The series status line of the Volumes view (1.29.0 RC): "Complete (Japan) · English: 12 of 14 volumes · 2 volumes missing",
 * "Ongoing (Korea) · English chapters: ongoing · up to date". The status word is the country of origin's; "missing" means
 * released in the preferred language, and "up to date" is said only when that is known. Null when the folder has no own link.
 */
export function seriesStatusLine(view: VolumeViewDto | null | undefined): string | null {
  if (!view?.hasSeriesStatus) return null;
  const word = view.seriesStatus ? STATUS_WORDS[view.seriesStatus] ?? null : null;
  const place = view.origin ? ORIGIN_PLACES[view.origin] ?? null : null;
  const status = word && place ? `${word} (${place})` : word;
  const volumes = view.missingVolumes ?? 0;
  const chapters = view.missingChapters ?? 0;
  const parts: string[] = [];
  if (volumes > 0) parts.push(`${volumes} volume${volumes === 1 ? '' : 's'}`);
  if (chapters > 0) parts.push(`${chapters} chapter${chapters === 1 ? '' : 's'}`);
  const tail = parts.length > 0 ? `${parts.join(', ')} missing` : view.releaseKnown ? 'up to date' : null;
  const line = [status, releasePart(view), tail].filter((x): x is string => !!x).join(' · ');
  return line || null;
}

/**
 * The status lines under the browse bar while the Volumes view of a linked series is shown. 1.30.0 (reach): two lines from the
 * series' progress - the trackers ("Ongoing (Japan): 22 volumes · English (Yen Press): 14 volumes, ongoing · English chapters:
 * to chapter 65") and what the folder holds ("You have volumes 1-14 + chapters 47-65 · up to date · Volume 15 available in
 * English"), with the completion mark; the 1.29.0 one-line form when the server sends no progress.
 */
@Component({
  selector: 'app-volume-series-status',
  standalone: true,
  imports: [MatIconModule, ListCreditComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (lines(); as l) {
      <div class="status" [class.missing]="hasMissing()" [class.complete]="complete()" data-testid="series-status" [title]="hint()">
        <mat-icon aria-hidden="true">{{ icon() }}</mat-icon>
        <div class="text">
          @if (l.trackers) { <span class="trackers" data-testid="series-trackers">{{ l.trackers }}</span> }
          @if (l.folder) { <span class="folder" data-testid="series-folder">{{ l.folder }}</span> }
          <app-list-credit [credit]="view().progress?.listCredit" />
        </div>
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .status { display: flex; align-items: flex-start; gap: 6px; margin: -6px 0 12px; font-size: 13px; color: var(--mp-text-secondary); min-width: 0; }
    .text { display: flex; flex-direction: column; gap: 2px; min-width: 0; overflow-wrap: anywhere; }
    .status.missing .folder { color: var(--mp-warn); }
    .status.missing mat-icon { color: var(--mp-warn); }
    .status.complete mat-icon, .status.complete .folder { color: var(--mp-success-soft); }
    mat-icon { font-size: 18px; width: 18px; height: 18px; flex: none; }
  `],
})
export class VolumeSeriesStatusComponent {
  readonly view = input.required<VolumeViewDto>();

  /** The two lines of the progress, else the 1.29.0 single line as the "folder" line. */
  readonly lines = computed(() => {
    const view = this.view();
    if (!view.hasSeriesStatus) return null;
    const progress = view.progress;
    if (progress) {
      const trackers = trackersLine(progress);
      const folder = folderLine(progress);
      return trackers || folder ? { trackers, folder } : null;
    }
    const line = seriesStatusLine(view);
    return line ? { trackers: null, folder: line } : null;
  });
  readonly hasMissing = computed(() => {
    const p = this.view().progress;
    return p ? p.missingVolumes + p.missingChapters > 0 : (this.view().missingVolumes ?? 0) + (this.view().missingChapters ?? 0) > 0;
  });
  readonly complete = computed(() => this.view().progress?.completion === 'CompleteCollection');
  readonly icon = computed(() => {
    const p = this.view().progress;
    return p ? progressIcon(p) : this.hasMissing() ? 'error_outline' : 'check_circle_outline';
  });
  readonly hint = computed(() => {
    const name = languageName(this.view().language ?? this.view().progress?.trackers.language);
    return name ? `Missing means released in ${name}, your preferred language.` : '';
  });
}
