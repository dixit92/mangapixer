import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { MetadataOriginStatus, VolumeViewDto } from '../../core/api/api-types';

const STATUS_WORDS: Partial<Record<MetadataOriginStatus, string>> = {
  Ongoing: 'Ongoing',
  Complete: 'Complete',
  Hiatus: 'On hiatus',
  Cancelled: 'Cancelled',
};

/** A language code as an English name ("fr" -> "French"); the code itself when the runtime cannot name it. */
export function languageName(code: string | null | undefined): string {
  if (!code) return '';
  try {
    return new Intl.DisplayNames(['en'], { type: 'language' }).of(code) ?? code;
  } catch {
    return code;
  }
}

/**
 * The series status line of the Volumes view (1.29.0 RC): "Ongoing - up to date", "Complete - up to date", "Ongoing - 2 volumes,
 * 3 chapters missing". "Missing" means released in the preferred language; "up to date" is said only when what is released in
 * it is known. Null when the folder has no own link.
 */
export function seriesStatusLine(view: VolumeViewDto | null | undefined): string | null {
  if (!view?.hasSeriesStatus) return null;
  const status = view.seriesStatus ? STATUS_WORDS[view.seriesStatus] ?? null : null;
  const volumes = view.missingVolumes ?? 0;
  const chapters = view.missingChapters ?? 0;
  const parts: string[] = [];
  if (volumes > 0) parts.push(`${volumes} volume${volumes === 1 ? '' : 's'}`);
  if (chapters > 0) parts.push(`${chapters} chapter${chapters === 1 ? '' : 's'}`);
  const tail = parts.length > 0 ? `${parts.join(', ')} missing` : view.releaseKnown ? 'up to date' : null;
  const line = [status, tail].filter((x): x is string => !!x).join(' - ');
  return line || null;
}

/** The status line under the browse bar while the Volumes view of a linked series is shown. */
@Component({
  selector: 'app-volume-series-status',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (line(); as text) {
      <p class="status" [class.missing]="hasMissing()" data-testid="series-status" [title]="hint()">
        <mat-icon aria-hidden="true">{{ hasMissing() ? 'error_outline' : 'check_circle_outline' }}</mat-icon>
        <span>{{ text }}</span>
      </p>
    }
  `,
  styles: [`
    :host { display: block; }
    .status { display: flex; align-items: center; gap: 6px; margin: -6px 0 12px; font-size: 13px; color: #b8b8c6; }
    .status.missing { color: #ffcc80; }
    mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `],
})
export class VolumeSeriesStatusComponent {
  readonly view = input.required<VolumeViewDto>();

  readonly line = computed(() => seriesStatusLine(this.view()));
  readonly hasMissing = computed(() => (this.view().missingVolumes ?? 0) + (this.view().missingChapters ?? 0) > 0);
  readonly hint = computed(() => {
    const name = languageName(this.view().language);
    return name ? `Missing means released in ${name}, your preferred language.` : '';
  });
}
