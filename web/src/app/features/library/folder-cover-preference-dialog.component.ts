import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatRadioModule } from '@angular/material/radio';

import { ApiService } from '../../core/api/api.service';
import { FolderCoverPreference, FolderCoverPreferenceDto } from '../../core/api/api-types';

export interface FolderCoverPreferenceDialogData {
  nodeId: string;
  name: string;
}

/** The choice: 'inherit' removes the folder's own value. */
type Choice = 'inherit' | 'Web' | 'File';

const LABELS: Record<FolderCoverPreference, string> = { Web: 'Web covers when available', File: 'File covers' };

/**
 * "Folder covers..." for one folder (1.32.0, admin): what the covers below the folder are made from. Inherit follows the nearest
 * folder above that has a value, then the library's "Show saved web covers"; "Web covers when available" shows saved web covers
 * even where the library hides them; "File covers" shows each file's own cover and stops fetching web covers for the series below.
 * An item's own cover choice (Cover...) still wins. Reads and writes `/admin/folders/{id}/cover-preference`; "Inherit" removes the value.
 */
@Component({
  selector: 'app-folder-cover-preference-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule, MatRadioModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Covers - {{ data.name }}</h2>
    <mat-dialog-content>
      <p class="hint">
        What the covers of the series and volumes in this folder, and in every folder below it, are made from. A cover an admin
        chose for one item with <strong>Cover…</strong> still wins. Your files are never changed.
      </p>
      <mat-radio-group [value]="choice()" (change)="choice.set($event.value)" aria-label="Cover preference" class="choices"
                       data-testid="folder-cover-choices">
        <mat-radio-button value="inherit" data-testid="folder-cover-inherit">{{ inheritLabel() }}</mat-radio-button>
        <mat-radio-button value="Web" data-testid="folder-cover-web">Web covers when available
          <span class="sub">(saved covers of the linked series; the file's cover where there is none)</span></mat-radio-button>
        <mat-radio-button value="File" data-testid="folder-cover-file">File covers
          <span class="sub">(each file's own cover; no web cover is shown or downloaded for the series below)</span></mat-radio-button>
      </mat-radio-group>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" color="primary" [disabled]="!loaded() || busy()" (click)="save()" data-testid="folder-cover-save">Save</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .hint { color: var(--mp-text-secondary); font-size: 13px; margin: 0 0 12px; max-width: 420px; }
    .choices { display: flex; flex-direction: column; }
    .sub { color: var(--mp-text-dim); font-size: 12px; display: block; white-space: normal; }
    .error { color: var(--mp-error); }
  `],
})
export class FolderCoverPreferenceDialogComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly ref = inject<MatDialogRef<FolderCoverPreferenceDialogComponent, boolean>>(MatDialogRef);
  readonly data = inject<FolderCoverPreferenceDialogData>(MAT_DIALOG_DATA);

  readonly choice = signal<Choice>('inherit');
  readonly busy = signal(false);
  readonly error = signal('');
  private readonly state = signal<FolderCoverPreferenceDto | null>(null);
  readonly loaded = computed(() => this.state() !== null);

  /** "Inherit (File covers from <folder>)" / "Inherit (Web covers when available, the library's setting)". */
  readonly inheritLabel = computed(() => {
    const s = this.state();
    if (!s) return 'Inherit';
    const value = LABELS[s.inherited];
    return s.inheritedSourceName ? `Inherit (${value} from ${s.inheritedSourceName})` : `Inherit (${value} - the library's setting)`;
  });

  ngOnInit(): void {
    this.api.getFolderCoverPreference(this.data.nodeId).subscribe({
      next: (s) => {
        this.state.set(s);
        this.choice.set(s.preference ?? 'inherit');
      },
      error: () => this.error.set('The current setting could not be read.'),
    });
  }

  save(): void {
    const choice = this.choice();
    this.busy.set(true);
    const call = choice === 'inherit'
      ? this.api.clearFolderCoverPreference(this.data.nodeId)
      : this.api.setFolderCoverPreference(this.data.nodeId, { preference: choice });
    call.subscribe({
      next: () => this.ref.close(true),
      error: () => {
        this.busy.set(false);
        this.error.set('The setting could not be saved.');
      },
    });
  }
}
