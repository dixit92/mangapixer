import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';

import { ApiError, MetadataFlagReason, MetadataMyFlagDto } from '../../../core/api/api-types';
import { FLAG_NOTE_MAX, FLAG_REASON_OPTIONS } from '../admin-metadata/metadata-admin-labels';
import { MetadataApiService } from '../metadata-api.service';

export interface FlagDialogData {
  nodeId: string;
  title: string;
}

/** The created flag, or undefined when nothing was sent. */
export type FlagDialogResult = MetadataMyFlagDto | undefined;

/** A flag error in words (limit, duplicate, gone); '' for the server's own message. */
export function flagErrorText(err: (ApiError & { status?: number }) | null | undefined): string {
  if (!err) return 'The report was not sent.';
  if (err.status === 429 || err.error === 'flag_limit') {
    return 'You have sent the most reports allowed for today. The limit resets at 00:00 UTC; an admin will see the ones you sent.';
  }
  if (err.status === 409) return 'You already reported this series. An admin will look at it.';
  if (err.status === 404) return 'This series information is no longer available.';
  return err.message || 'The report was not sent.';
}

/**
 * "Wrong series?" (metadata stage 2, design section 5 + decision 12): a reader tells the
 * admins that the series information shown is wrong. A reason, an optional plain-text
 * note (at most 500 characters; only admins see it), then "Thanks - an admin will
 * review this." Nothing is changed for anyone until an admin acts.
 */
@Component({
  selector: 'app-flag-dialog',
  standalone: true,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatRadioModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (sent(); as flag) {
      <h2 mat-dialog-title>Thanks</h2>
      <mat-dialog-content>
        <p class="thanks" data-testid="flag-thanks"><mat-icon>check_circle</mat-icon> Thanks - an admin will review this.</p>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-flat-button type="button" (click)="ref.close(flag)" data-testid="flag-done">Close</button>
      </mat-dialog-actions>
    } @else {
      <h2 mat-dialog-title>Wrong series?</h2>
      <mat-dialog-content>
        <p class="lead">Tell an admin that the series information for <strong>{{ data.title }}</strong> looks wrong.</p>
        <mat-radio-group class="reasons" [value]="reason()" (change)="reason.set($event.value)" aria-label="What is wrong">
          @for (o of options; track o.value) {
            <mat-radio-button [value]="o.value" [attr.data-testid]="'flag-reason-' + o.value">
              <span class="opt">{{ o.label }}</span>
              <span class="hint">{{ o.hint }}</span>
            </mat-radio-button>
          }
        </mat-radio-group>
        <mat-form-field appearance="outline" class="note">
          <mat-label>Note for the admin (optional)</mat-label>
          <textarea matInput rows="3" [maxlength]="max" [ngModel]="note()" (ngModelChange)="note.set($event)"
                    data-testid="flag-note-input"></textarea>
          <mat-hint align="end">{{ note().length }} / {{ max }}</mat-hint>
        </mat-form-field>
        <p class="small">Only admins see your report and note.</p>
        @if (error()) { <p class="error" role="alert" data-testid="flag-error">{{ error() }}</p> }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="button" [disabled]="!canSend()" (click)="send()" data-testid="flag-send">Send report</button>
      </mat-dialog-actions>
    }
  `,
  styles: [`
    .lead { margin-top: 0; }
    .reasons { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
    .opt { display: block; }
    .hint { display: block; font-size: 12px; color: var(--mp-text-muted); }
    .note { width: 100%; }
    .small { font-size: 12px; color: var(--mp-text-muted); margin: 0; }
    .error { color: var(--mp-error); }
    .thanks { display: flex; align-items: center; gap: 8px; }
    .thanks mat-icon { color: var(--mp-success); }
  `],
})
export class FlagDialogComponent {
  readonly data = inject<FlagDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<FlagDialogComponent, FlagDialogResult>>(MatDialogRef);
  private readonly api = inject(MetadataApiService);

  readonly options = FLAG_REASON_OPTIONS;
  readonly max = FLAG_NOTE_MAX;
  readonly reason = signal<MetadataFlagReason | null>(null);
  readonly note = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly sent = signal<MetadataMyFlagDto | null>(null);

  readonly canSend = computed(() => !!this.reason() && !this.busy() && this.note().length <= FLAG_NOTE_MAX);

  send(): void {
    const reason = this.reason();
    if (!reason || !this.canSend()) return;
    this.busy.set(true);
    this.error.set(null);
    const note = this.note().trim();
    this.api.createFlag(this.data.nodeId, { reason, note: note ? note : null }).subscribe({
      next: (flag) => {
        this.busy.set(false);
        this.sent.set(flag);
      },
      error: (err: ApiError & { status?: number }) => {
        this.busy.set(false);
        this.error.set(flagErrorText(err));
      },
    });
  }
}
