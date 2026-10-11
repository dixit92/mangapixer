import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ReadingModeSuggestionDto, ReadingModeSuggestionsDto } from '../../../core/api/api-types';
import { ReadingModeSuggestionsApiService } from './reading-mode-suggestions-api.service';
import { CONFIDENCE_LABELS, appliedSentence, currentText, evidenceLines, modeLabel } from './reading-mode-labels';

/**
 * Reading mode suggestions (1.41.0, admin page at `/admin/reading-modes`): folders whose pages are webtoon-shaped tall strips, whose
 * ComicInfo says right to left, or whose linked series hints at a mode, and that read in another mode today. Apply (one row, or the
 * selected rows) sets the folder's reader default - the same setting as the browse page's Direction. Nothing is applied on its own.
 * Folders an admin already set a default for (on them or above) are never suggested; where their evidence disagrees they are listed
 * under "Set by an admin".
 */
@Component({
  selector: 'app-reading-mode-suggestions',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatCheckboxModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page" data-testid="reading-mode-suggestions">
      <nav class="back"><a routerLink="/admin"><mat-icon>arrow_back</mat-icon> MangaPixer Administration</a></nav>
      <header class="head">
        <h1>Reading mode suggestions</h1>
        @if (data(); as d) { <span class="count" data-testid="reading-modes-count">{{ d.total }} suggested</span> }
      </header>
      <p class="intro">MangaPixer looks at what it already knows about each folder - the shape of its pages (long webtoon strips), the
        ComicInfo in its files, and the linked series as a hint - and lists the folders that would read better in another mode.
        <strong>Apply</strong> sets the folder's reading mode for everyone (readers can still change it for themselves). Nothing changes
        until you apply it.</p>

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else if (data(); as d) {
        @if (d.items.length > 0) {
          <div class="toolbar" data-testid="reading-modes-bulk">
            <mat-checkbox [checked]="allSelected()" [indeterminate]="someSelected()" (change)="toggleAll($event.checked)"
                          data-testid="reading-modes-select-all">Select all</mat-checkbox>
            <button mat-flat-button color="primary" type="button" [disabled]="busy() || selected().size === 0" (click)="applySelected()"
                    data-testid="reading-modes-apply-selected">Apply selected ({{ selected().size }})</button>
          </div>
        }
        @if (message()) { <p class="message" role="status" data-testid="reading-modes-message">{{ message() }}</p> }
        <div class="list">
          @for (row of d.items; track row.nodeId) {
            <article class="row" [attr.data-confidence]="row.confidence" data-testid="reading-mode-row">
              <mat-checkbox class="pick" [checked]="selected().has(row.nodeId)" (change)="toggle(row.nodeId, $event.checked)"
                            [aria-label]="'Select ' + row.folderName" />
              <div class="body">
                <header>
                  <a class="name" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]" data-testid="reading-mode-folder">{{ row.folderName }}</a>
                  <span [class]="'chip ' + row.confidence.toLowerCase()" data-testid="reading-mode-confidence">{{ confidenceLabels[row.confidence] }}</span>
                </header>
                <p class="meta">{{ row.libraryName }}@if (row.parentName) { · {{ row.parentName }} }</p>
                <ul class="evidence">
                  @for (line of evidence(row); track line) { <li>{{ line }}</li> }
                </ul>
                <p class="modes">
                  <span class="now" data-testid="reading-mode-current">Now: {{ current(row) }}</span>
                  <mat-icon aria-hidden="true">arrow_forward</mat-icon>
                  <span class="suggested" data-testid="reading-mode-suggested">{{ mode(row.suggestedMode) }}</span>
                </p>
              </div>
              <div class="actions">
                <button mat-stroked-button type="button" [disabled]="busy()" (click)="applyOne(row)"
                        [attr.aria-label]="'Apply ' + mode(row.suggestedMode) + ' to ' + row.folderName"
                        data-testid="reading-mode-apply">Apply</button>
              </div>
            </article>
          } @empty {
            <div class="empty" data-testid="reading-modes-empty">
              <mat-icon>done_all</mat-icon>
              <p>No suggestions: every folder MangaPixer has evidence for already reads in the mode it points to.</p>
            </div>
          }
        </div>
        @if (d.total > d.items.length) {
          <p class="note" data-testid="reading-modes-more">Showing the first {{ d.items.length }} of {{ d.total }}. Apply some and reload for more.</p>
        }
        <p class="note">{{ d.foldersChecked }} folders checked.</p>

        @if (d.setByAdmin.length > 0) {
          <section class="admin-set" data-testid="reading-modes-admin-set">
            <button mat-button type="button" (click)="showAdminSet.set(!showAdminSet())" [attr.aria-expanded]="showAdminSet()"
                    data-testid="reading-modes-admin-set-toggle">
              <mat-icon>{{ showAdminSet() ? 'expand_less' : 'expand_more' }}</mat-icon>
              Set by an admin ({{ d.setByAdmin.length }})
            </button>
            @if (showAdminSet()) {
              <p class="note">These folders have a reading mode an admin chose (on the folder or a folder above) that their evidence
                disagrees with. They are never suggested; change them from the folder's Direction in the library if needed.</p>
              @for (row of d.setByAdmin; track row.nodeId) {
                <article class="row decided" data-testid="reading-mode-admin-row">
                  <div class="body">
                    <header>
                      <a class="name" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]">{{ row.folderName }}</a>
                    </header>
                    <p class="meta">{{ row.libraryName }}@if (row.parentName) { · {{ row.parentName }} }</p>
                    <ul class="evidence">
                      @for (line of evidence(row); track line) { <li>{{ line }}</li> }
                    </ul>
                    <p class="modes"><span class="now">Set: {{ current(row) }}</span> · <span>Evidence points to {{ mode(row.suggestedMode) }}</span></p>
                  </div>
                </article>
              }
            }
          </section>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .page { max-width: 1180px; margin: 0 auto; padding-bottom: 32px; }
    .back a { display: inline-flex; align-items: center; gap: 4px; color: var(--mp-accent); text-decoration: none; font-size: 14px; }
    .back mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 16px; margin: 8px 0 4px; }
    h1 { font-size: 24px; font-weight: 500; margin: 0; }
    .count { font-size: 12px; font-weight: 600; padding: 0 8px; border-radius: 10px; background: var(--mp-accent-strong); color: var(--mp-on-accent); line-height: 20px; }
    .intro { margin: 4px 0 12px; color: var(--mp-text-muted); font-size: 13px; max-width: 860px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: center; margin-bottom: 8px; }
    .message { margin: 4px 0 8px; font-size: 13px; color: var(--mp-success-soft); }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 8px 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: var(--mp-surface-raised);
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.06); border-left: 3px solid var(--mp-accent); }
    .row[data-confidence="Low"] { border-left-color: var(--mp-text-faint); }
    .row.decided { border-left-color: var(--mp-caution); margin-top: 8px; }
    .pick { flex: none; margin-top: -6px; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    a.name:hover { text-decoration: underline; }
    .chip { padding: 0 8px; border-radius: 10px; background: rgb(var(--mp-ink-rgb) / 0.08); font-size: 12px; line-height: 20px; }
    .chip.high { background: rgb(var(--mp-success-rgb) / 0.16); color: var(--mp-success-soft); }
    .chip.medium { background: rgb(var(--mp-accent-rgb) / 0.16); }
    .chip.low { color: var(--mp-text-muted); }
    .meta { margin: 2px 0 4px; font-size: 12px; color: var(--mp-text-muted); overflow-wrap: anywhere; }
    .evidence { margin: 0 0 6px; padding-left: 18px; font-size: 13px; }
    .evidence li { overflow-wrap: anywhere; }
    .modes { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin: 0; font-size: 13px; }
    .modes mat-icon { font-size: 16px; width: 16px; height: 16px; color: var(--mp-text-dim); }
    .now { color: var(--mp-text-muted); }
    .suggested { font-weight: 600; }
    .actions { display: flex; flex-direction: column; gap: 4px; flex: none; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: var(--mp-error); }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: var(--mp-text-dim); text-align: center; }
    .note { margin: 8px 0 0; font-size: 12px; color: var(--mp-text-dim); }
    .admin-set { margin-top: 16px; }
    @media (max-width: 599.98px) {
      h1 { font-size: 20px; }
      /* Phone: the checkbox stays beside the title; Apply takes its own line under both. */
      .row { display: grid; grid-template-columns: auto minmax(0, 1fr); }
      .row.decided { grid-template-columns: minmax(0, 1fr); }
      .actions { grid-column: 1 / -1; flex-direction: row; justify-content: flex-end; }
    }
  `],
})
export class ReadingModeSuggestionsComponent implements OnInit {
  private readonly api = inject(ReadingModeSuggestionsApiService);

  readonly data = signal<ReadingModeSuggestionsDto | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly showAdminSet = signal(false);

  readonly items = computed(() => this.data()?.items ?? []);
  readonly allSelected = computed(() => this.items().length > 0 && this.items().every((r) => this.selected().has(r.nodeId)));
  readonly someSelected = computed(() => this.selected().size > 0 && !this.allSelected());

  readonly confidenceLabels = CONFIDENCE_LABELS;
  readonly evidence = evidenceLines;
  readonly current = currentText;
  readonly mode = modeLabel;

  ngOnInit(): void {
    this.load();
  }

  toggle(nodeId: string, on: boolean): void {
    this.selected.update((s) => {
      const next = new Set(s);
      if (on) next.add(nodeId); else next.delete(nodeId);
      return next;
    });
  }

  toggleAll(on: boolean): void {
    this.selected.set(on ? new Set(this.items().map((r) => r.nodeId)) : new Set());
  }

  applyOne(row: ReadingModeSuggestionDto): void {
    this.apply([row]);
  }

  applySelected(): void {
    const rows = this.items().filter((r) => this.selected().has(r.nodeId));
    if (rows.length > 0) this.apply(rows);
  }

  private apply(rows: ReadingModeSuggestionDto[]): void {
    this.busy.set(true);
    this.message.set(null);
    this.api.apply({ items: rows.map((r) => ({ nodeId: r.nodeId, readerMode: r.suggestedMode })) }).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.message.set(appliedSentence(result.applied, result.skipped.length));
        this.selected.set(new Set());
        this.load();
      },
      error: () => {
        this.busy.set(false);
        this.message.set('The reading modes could not be applied.');
      },
    });
  }

  load(): void {
    this.error.set(null);
    if (!this.data()) this.loading.set(true);
    this.api.list().subscribe({
      next: (d) => {
        this.data.set(d);
        this.loading.set(false);
        const shown = new Set(d.items.map((r) => r.nodeId));
        this.selected.update((s) => new Set([...s].filter((id) => shown.has(id))));
      },
      error: () => {
        this.loading.set(false);
        this.error.set('The suggestions could not be loaded.');
      },
    });
  }
}
