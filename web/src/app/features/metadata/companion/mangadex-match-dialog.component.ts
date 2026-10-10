import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Observable } from 'rxjs';

import { ApiError, CompanionDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { companionStateText, looksLikeMangaDexReference, mangaDexCompanion } from './mangadex-match';

export interface MangaDexMatchDialogData {
  nodeId: string;
}

/**
 * "Change MangaDex match..." (1.29.0, admin): the MangaDex record that gives a linked series its volume covers and
 * volume list. Shows the current companion (found automatically, chosen, not found, "Not on MangaDex"); a pasted
 * MangaDex title address or id replaces it (the server parses it and makes ONE request by id); "Not on MangaDex"
 * stops every lookup there; "Check again" reads it and its lists now. Every request is made by the server, through
 * its gates; the browser only talks to MangaPixer. Closes with true when something changed.
 */
@Component({
  selector: 'app-mangadex-match-dialog',
  standalone: true,
  imports: [DatePipe, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>MangaDex match</h2>
    <mat-dialog-content>
      <p class="small">MangaDex gives this series its volume covers and the list of chapters in each volume. It is found
        from the series' MangaUpdates link; you can choose another MangaDex title or say the series is not there.</p>
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else {
        <div class="current" data-testid="mdx-current">
          <strong>Now:</strong> {{ stateText() }}
          @if (current()?.siteUrl; as url) {
            <br><a [href]="url" target="_blank" rel="noopener noreferrer" data-testid="mdx-link">Open on MangaDex</a>
          }
          @if (current()?.checkedAt; as at) { <span class="muted small"> · checked {{ at | date: 'mediumDate' }}</span> }
        </div>
        <mat-form-field appearance="outline" class="ref" subscriptSizing="dynamic">
          <mat-label>MangaDex title address or id</mat-label>
          <input matInput [ngModel]="reference()" (ngModelChange)="reference.set($event)" data-testid="mdx-reference"
                 placeholder="https://mangadex.org/title/..." autocomplete="off">
        </mat-form-field>
        @if (reference() && !referenceOk()) {
          <p class="error small" data-testid="mdx-reference-error">Paste a mangadex.org/title/... address or a title id.</p>
        }
      }
      @if (error()) { <p class="error" role="alert" data-testid="mdx-error">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [disabled]="busy() || current()?.state === 'None'" (click)="notOnMangaDex()"
              data-testid="mdx-none">Not on MangaDex</button>
      <button mat-button type="button" [disabled]="busy()" (click)="recheck()" data-testid="mdx-recheck">Check again</button>
      <button mat-button type="button" (click)="close()">Close</button>
      <button mat-flat-button color="primary" type="button" [disabled]="busy() || !referenceOk()" (click)="use()"
              data-testid="mdx-use">Use this title</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .small { font-size: 13px; color: var(--mp-text-secondary); }
    .muted { color: var(--mp-text-muted); }
    .current { font-size: 13px; margin: 8px 0 12px; line-height: 1.6; }
    .current a { color: var(--mp-accent); }
    .ref { width: 100%; }
    .error { color: var(--mp-error-strong); }
  `],
})
export class MangaDexMatchDialogComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly ref = inject(MatDialogRef<MangaDexMatchDialogComponent, boolean>);
  readonly data = inject<MangaDexMatchDialogData>(MAT_DIALOG_DATA);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly companions = signal<CompanionDto[]>([]);
  readonly reference = signal('');
  private changed = false;

  readonly current = computed(() => mangaDexCompanion(this.companions()));
  readonly stateText = computed(() => companionStateText(this.current()?.state));
  readonly referenceOk = computed(() => looksLikeMangaDexReference(this.reference()));

  ngOnInit(): void {
    this.api.getCompanions(this.data.nodeId).subscribe({
      next: (list) => {
        this.companions.set(list);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err?.message || 'Could not load the MangaDex match.');
      },
    });
  }

  use(): void {
    if (!this.referenceOk()) return;
    this.run(this.api.setMangaDexCompanion(this.data.nodeId, this.reference().trim()));
  }

  notOnMangaDex(): void {
    this.run(this.api.clearMangaDexCompanion(this.data.nodeId));
  }

  recheck(): void {
    this.run(this.api.recheckMangaDexCompanion(this.data.nodeId));
  }

  close(): void {
    this.ref.close(this.changed);
  }

  private run(call: Observable<CompanionDto[]>): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (list) => {
        this.busy.set(false);
        this.companions.set(list);
        this.reference.set('');
        this.changed = true;
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err?.message || 'The change was not saved.');
      },
    });
  }
}
