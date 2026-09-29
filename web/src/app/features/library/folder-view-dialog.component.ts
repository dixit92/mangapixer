import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatRadioModule } from '@angular/material/radio';

import { ApiService } from '../../core/api/api.service';
import { ViewSwitch } from '../../core/api/api-types';

export interface FolderViewDialogData {
  nodeId: string;
  name: string;
}

/** The choice: null inherits the library (then the global default). */
type Choice = 'auto' | 'on' | 'off';

/**
 * "View..." for one folder (1.29.0, admin): the folder's own Volumes view override - Automatic (follow the library and
 * the global setting), On or Off. A viewer's own Volumes | Folders switch still wins for that viewer. Reads and writes
 * the existing `/admin/folders/{id}/view-settings` endpoint; "Automatic" removes the override.
 */
@Component({
  selector: 'app-folder-view-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule, MatRadioModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>View - {{ data.name }}</h2>
    <mat-dialog-content>
      <p class="hint">
        The Volumes view groups a series' chapters into volume stacks, ordered by volume. Choose whether this folder shows it
        by default. Each person's own Volumes | Folders switch still decides for them.
      </p>
      <mat-radio-group [value]="choice()" (change)="choice.set($event.value)" aria-label="Virtual volumes" class="choices"
                       data-testid="folder-view-choices">
        <mat-radio-button value="auto">Automatic <span class="sub">(the library and global setting)</span></mat-radio-button>
        <mat-radio-button value="on">On</mat-radio-button>
        <mat-radio-button value="off">Off</mat-radio-button>
      </mat-radio-group>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" color="primary" [disabled]="!loaded() || busy()" (click)="save()" data-testid="folder-view-save">Save</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .hint { color: #b8b8c6; font-size: 13px; margin: 0 0 12px; max-width: 420px; }
    .choices { display: flex; flex-direction: column; }
    .sub { color: #8a8a99; font-size: 12px; }
    .error { color: #ff8a80; }
  `],
})
export class FolderViewDialogComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly ref = inject<MatDialogRef<FolderViewDialogComponent, boolean>>(MatDialogRef);
  readonly data = inject<FolderViewDialogData>(MAT_DIALOG_DATA);

  readonly choice = signal<Choice>('auto');
  readonly busy = signal(false);
  readonly error = signal('');
  private readonly read = signal(false);
  readonly loaded = computed(() => this.read());

  ngOnInit(): void {
    this.api.getFolderViewSettings(this.data.nodeId).subscribe({
      next: (s) => {
        this.choice.set(s.virtualVolumes === 'On' ? 'on' : s.virtualVolumes === 'Off' ? 'off' : 'auto');
        this.read.set(true);
      },
      error: () => this.error.set('The current setting could not be read.'),
    });
  }

  save(): void {
    const virtualVolumes: ViewSwitch | null = this.choice() === 'on' ? 'On' : this.choice() === 'off' ? 'Off' : null;
    this.busy.set(true);
    this.api.setFolderViewSettings(this.data.nodeId, { virtualVolumes }).subscribe({
      next: () => this.ref.close(true),
      error: () => {
        this.busy.set(false);
        this.error.set('The setting could not be saved.');
      },
    });
  }
}
