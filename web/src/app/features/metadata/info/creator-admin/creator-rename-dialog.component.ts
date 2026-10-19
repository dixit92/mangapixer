import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

import { InfoApiService } from '../info-api.service';
import { CreatorDto } from '../info-types';
import { CreatorDialogData } from './creator-admin.service';
import { creatorErrorText, nameSourceLabel } from './creator-admin-labels';

/** Lane F's limit (`invalid` above it). */
const MAX_NAME = 256;

/**
 * "Rename..." on a creator chip (admin, 1.45.0): the name the creator is shown with everywhere. One of its known spellings can be picked,
 * or a new one typed; "Use the automatic name" goes back to the rule (the author record's main name, else the most used spelling).
 * The new name is saved on this server only; the chips show it after the index's next pass.
 */
@Component({
  selector: 'app-creator-rename-dialog',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Rename creator</h2>
    <mat-dialog-content>
      <p class="lead">The name <strong>{{ data.display }}</strong> is shown with on every chip and in search.</p>
      <form (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="name-field" subscriptSizing="dynamic">
          <mat-label>Name</mat-label>
          <input matInput name="name" [maxlength]="maxName" [ngModel]="name()" (ngModelChange)="name.set($event)"
                 data-testid="creator-rename-name">
        </mat-form-field>
      </form>
      @if (data.creator; as c) {
        @if ((c.names ?? []).length > 1) {
          <p class="small">Known spellings:</p>
          <ul class="names">
            @for (n of c.names ?? []; track n.name) {
              <li>
                <button type="button" class="pick" (click)="name.set(n.name)">{{ n.name }}</button>
                <span class="muted"> {{ source(n.source) }}</span>
              </li>
            }
          </ul>
        }
      }
      @if (error(); as e) { <p class="error" role="alert" data-testid="creator-error">{{ e }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (data.creator.displayNameSource === 'admin') {
        <button mat-button type="button" [disabled]="saving()" (click)="useAutomatic()" data-testid="creator-rename-auto">Use the automatic name</button>
      }
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" [disabled]="!valid() || saving()" (click)="save()" data-testid="creator-rename-save">Rename</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin: 0 0 12px; overflow-wrap: anywhere; }
    .name-field { width: 100%; }
    .small { margin: 12px 0 4px; font-size: 13px; color: var(--mp-text-muted); }
    .names { margin: 0; padding: 0; list-style: none; }
    .names li { margin: 2px 0; overflow-wrap: anywhere; }
    .pick { background: none; border: none; padding: 4px 0; color: var(--mp-accent); cursor: pointer; font: inherit; text-align: left; }
    .muted { font-size: 12px; color: var(--mp-text-dim); }
    .error { margin: 8px 0 0; color: var(--mp-error); }
  `],
})
export class CreatorRenameDialogComponent {
  readonly data = inject<CreatorDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<CreatorRenameDialogComponent, CreatorDto>>(MatDialogRef);
  private readonly api = inject(InfoApiService);

  readonly maxName = MAX_NAME;
  readonly name = signal(this.data.creator.displayName || this.data.display);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly valid = computed(() => {
    const n = this.name().trim();
    return n.length > 0 && n.length <= MAX_NAME && n !== this.data.creator.displayName;
  });

  source(s: string): string {
    return nameSourceLabel(s);
  }

  save(): void {
    if (!this.valid() || this.saving()) return;
    this.send(this.name().trim());
  }

  useAutomatic(): void {
    this.send(null);
  }

  private send(displayName: string | null): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.renameCreator(this.data.creator.id, { displayName }).subscribe({
      next: (c) => this.ref.close(c),
      error: (e) => {
        this.error.set(creatorErrorText(e));
        this.saving.set(false);
      },
    });
  }
}
