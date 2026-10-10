import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { SetArtistFolderRequest } from '../../../core/api/api-types';
import { ARTIST_ROLE_OPTIONS } from './artist-folder-labels';

export interface ArtistFolderDialogData {
  /** The folder's name: the artist's default name ('' when the caller does not know it - the server then uses it). */
  folderName: string;
}

/** The artist to declare, or undefined when cancelled. */
export type ArtistFolderDialogResult = SetArtistFolderRequest | undefined;

/** Same limit as the server (`DeclaredFactKeys.MaxValueLength`). */
const MAX_NAME = 200;

/**
 * "Artist folder" (1.37.0): asks for the artist's name (the folder's name by default) and role ("Story & art" by default) before a
 * folder is marked as one artist's works. The dialog only collects the answer; the caller sends it (the review dashboard after its
 * Undo window, the admin menus at once).
 */
@Component({
  selector: 'app-artist-folder-dialog',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Artist folder</h2>
    <mat-dialog-content>
      <p class="lead">
        @if (data.folderName) { Mark <strong>{{ data.folderName }}</strong> as one artist's folder. } @else { Mark this folder as one artist's folder. }
      </p>
      <p class="small" data-testid="artist-folder-explain">
        The folder is never linked to a series and nothing inside inherits a link from it. Each work inside is matched on its own,
        with the artist as a hint. The artist is saved as the folder's declared creator; it is only compared on this server, never sent.
      </p>
      <form (ngSubmit)="save()" class="fields">
        <mat-form-field appearance="outline" class="name-field" subscriptSizing="dynamic">
          <mat-label>Artist</mat-label>
          <input matInput name="artist" [maxlength]="maxName" [ngModel]="name()" (ngModelChange)="name.set($event)"
                 placeholder="The folder's name" data-testid="artist-folder-name">
          <mat-hint>Empty: the folder's name</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="role-field" subscriptSizing="dynamic">
          <mat-label>Role</mat-label>
          <mat-select [value]="role()" (selectionChange)="role.set($event.value)" aria-label="Artist role" data-testid="artist-folder-role">
            @for (o of roleOptions; track o.value) {
              <mat-option [value]="o.value">{{ o.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" [disabled]="!valid()" (click)="save()" data-testid="artist-folder-save">Mark artist folder</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin: 0 0 6px; overflow-wrap: anywhere; }
    .small { margin: 0 0 12px; font-size: 13px; line-height: 1.45; color: var(--mp-text-muted); }
    .fields { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: flex-start; }
    .name-field { flex: 1 1 220px; min-width: 0; }
    .role-field { flex: 0 1 160px; min-width: 140px; }
  `],
})
export class ArtistFolderDialogComponent {
  readonly data = inject<ArtistFolderDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ArtistFolderDialogComponent, ArtistFolderDialogResult>>(MatDialogRef);

  readonly roleOptions = ARTIST_ROLE_OPTIONS;
  readonly maxName = MAX_NAME;
  readonly name = signal(this.data.folderName ?? '');
  readonly role = signal('author');
  readonly valid = computed(() => this.name().trim().length <= MAX_NAME);

  save(): void {
    if (!this.valid()) return;
    this.ref.close({ name: this.name().trim() || null, role: this.role() });
  }
}
