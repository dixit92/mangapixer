import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { BackupSnapshotMoveIssueDto, BackupSnapshotMoveStatusDto } from '../../core/api/api-types';

/** Human-readable byte size, e.g. "1.4 MB". */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

const ISSUE_TEXT: Record<string, string> = {
  name_conflict: 'a different file with the same name is already in the new location',
  verify_failed: 'the copy did not match the original',
  copy_failed: 'it could not be copied to the new location',
  source_delete_failed: 'copied, but the original could not be removed',
  cancelled: 'the move stopped when the server shut down',
};

/**
 * Progress and result of the background move of existing snapshots after a
 * backup location change (1.23.0), shown inside the Backup settings card.
 * Lists every snapshot that did not move cleanly and says where it is now.
 */
@Component({
  selector: 'app-backup-snapshot-move-status',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let s = status();
    <div class="move" data-testid="snapshot-move" aria-live="polite">
      @if (s.state === 'running') {
        <p>
          Moving existing snapshots: {{ s.filesDone }} of {{ s.totalFiles }} files
          ({{ size(s.bytesDone) }} of {{ size(s.totalBytes) }})
        </p>
        <progress [value]="s.bytesDone" [max]="s.totalBytes || 1" aria-label="Snapshot move progress"></progress>
      } @else {
        <p [class.ok]="s.issues.length === 0 && s.state === 'completed'">
          @if (s.issues.length === 0 && s.state === 'completed') { <mat-icon inline>check_circle</mat-icon> }
          @if (s.totalFiles === 0) {
            There were no snapshots to move.
          } @else {
            Moved {{ s.movedCount }} of {{ s.totalFiles }} snapshot(s) to the new location.
          }
          @if (s.prunedCount > 0) {
            {{ s.prunedCount }} older snapshot(s) were then removed to keep the configured number.
          }
          @if (s.state === 'cancelled') { The move stopped because the server shut down. }
        </p>
        @if (s.issues.length > 0) {
          <div class="issues" role="alert">
            <p>Some snapshots did not move:</p>
            <ul>
              @for (i of s.issues; track i.fileName) {
                <li><code>{{ i.fileName }}</code>: {{ describe(i) }}</li>
              }
            </ul>
          </div>
        }
      }
    </div>
  `,
  styles: [`
    .move { font-size: 14px; margin: 8px 0; }
    .move p { margin: 4px 0; }
    progress { width: 100%; max-width: 480px; height: 8px; }
    .ok { color: #4caf50; }
    .issues { color: #ffb300; }
    .issues ul { margin: 4px 0; padding-left: 20px; }
    code { font-size: 12px; }
  `],
})
export class BackupSnapshotMoveStatusComponent {
  readonly status = input.required<BackupSnapshotMoveStatusDto>();

  size(bytes: number): string {
    return formatBytes(bytes);
  }

  describe(issue: BackupSnapshotMoveIssueDto): string {
    const why = ISSUE_TEXT[issue.code] ?? 'it could not be moved';
    return issue.location === 'both'
      ? `${why}; it is in both locations`
      : `${why}; it is still in the previous location`;
  }
}
