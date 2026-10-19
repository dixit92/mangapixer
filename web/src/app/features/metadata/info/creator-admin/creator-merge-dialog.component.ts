import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';

import { InfoApiService } from '../info-api.service';
import { CreatorDto } from '../info-types';
import { CreatorDialogData } from './creator-admin.service';
import { creatorErrorText, creatorSummary } from './creator-admin-labels';

/**
 * "Merge into..." on a creator chip (admin, 1.45.0, D1 2.2): two creators that are one person (two provider ids for one author, or a
 * spelling the rules kept apart). Step 1 finds the creator to keep by name; step 2 confirms. The merged creator's names and ids move to
 * the kept one, and links or bookmarks to the merged one open the kept one. Split undoes it.
 */
@Component({
  selector: 'app-creator-merge-dialog',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Merge creator</h2>
    <mat-dialog-content>
      @if (target(); as t) {
        <p class="lead" data-testid="creator-merge-confirm">
          Merge <strong>{{ data.display }}</strong> into <strong>{{ t.displayName }}</strong>?
        </p>
        <p class="small">
          {{ data.display }}'s names and ids move to {{ t.displayName }}, and its works are then listed under {{ t.displayName }}.
          Links to {{ data.display }} open {{ t.displayName }}. Split undoes this.
        </p>
      } @else {
        <p class="lead">Find the creator <strong>{{ data.display }}</strong> is the same person as.</p>
        <mat-form-field appearance="outline" class="name-field" subscriptSizing="dynamic">
          <mat-label>Creator name</mat-label>
          <input matInput name="q" [ngModel]="query()" (ngModelChange)="onQuery($event)" autocomplete="off"
                 data-testid="creator-merge-search">
        </mat-form-field>
        @if (searching()) {
          <div class="state"><mat-spinner diameter="22" /></div>
        } @else if (query().trim().length >= 2 && candidates().length === 0) {
          <p class="small">No other creator with that name.</p>
        }
        <ul class="candidates">
          @for (c of candidates(); track c.id) {
            <li>
              <button type="button" class="candidate" (click)="target.set(c)" data-testid="creator-merge-candidate">
                <span class="cname">{{ c.displayName }}</span>
                <span class="muted">{{ summary(c) }}</span>
              </button>
            </li>
          }
        </ul>
      }
      @if (error(); as e) { <p class="error" role="alert" data-testid="creator-error">{{ e }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (target()) {
        <button mat-button type="button" [disabled]="saving()" (click)="target.set(null)">Back</button>
      }
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      @if (target()) {
        <button mat-flat-button type="button" [disabled]="saving()" (click)="merge()" data-testid="creator-merge-save">Merge</button>
      }
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin: 0 0 12px; overflow-wrap: anywhere; }
    .small { margin: 0 0 8px; font-size: 13px; line-height: 1.45; color: var(--mp-text-muted); overflow-wrap: anywhere; }
    .name-field { width: 100%; }
    .state { display: flex; justify-content: center; padding: 8px; }
    .candidates { margin: 4px 0 0; padding: 0; list-style: none; }
    .candidate {
      display: flex; flex-direction: column; align-items: flex-start; gap: 2px; width: 100%; box-sizing: border-box;
      padding: 8px; border: none; border-radius: 6px; background: none; color: var(--mp-text); font: inherit; text-align: left; cursor: pointer;
    }
    .candidate:hover, .candidate:focus-visible { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .cname { font-weight: 500; overflow-wrap: anywhere; }
    .muted { font-size: 12px; color: var(--mp-text-dim); overflow-wrap: anywhere; }
    .error { margin: 8px 0 0; color: var(--mp-error); }
  `],
})
export class CreatorMergeDialogComponent {
  readonly data = inject<CreatorDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<CreatorMergeDialogComponent, CreatorDto>>(MatDialogRef);
  private readonly api = inject(InfoApiService);

  readonly query = signal('');
  readonly candidates = signal<CreatorDto[]>([]);
  readonly target = signal<CreatorDto | null>(null);
  readonly searching = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  private readonly query$ = new Subject<string>();

  constructor() {
    this.query$
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        switchMap((text) => {
          const t = text.trim();
          if (t.length < 2) return of(null);
          this.searching.set(true);
          return this.api.findCreators(t).pipe(catchError((e) => {
            this.error.set(creatorErrorText(e));
            return of(null);
          }));
        }),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((list) => {
        this.searching.set(false);
        this.candidates.set((list?.items ?? []).filter((c) => c.id !== this.data.creatorId));
      });
  }

  onQuery(text: string): void {
    this.query.set(text);
    this.error.set(null);
    this.query$.next(text);
  }

  summary(c: CreatorDto): string {
    return creatorSummary(c);
  }

  merge(): void {
    const t = this.target();
    if (!t || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.api.mergeCreator(this.data.creatorId, { targetId: t.id }).subscribe({
      next: (c) => this.ref.close(c),
      error: (e) => {
        this.error.set(creatorErrorText(e));
        this.saving.set(false);
      },
    });
  }
}
