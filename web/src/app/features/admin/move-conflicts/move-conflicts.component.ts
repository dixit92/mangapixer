import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { MoveConflictDto, MoveConflictResolution } from '../../../core/api/api-types';
import { MoveConflictsApiService } from './move-conflicts-api.service';
import {
  MOVE_CONFLICT_KIND_LABELS, MOVE_CONFLICT_STATE_LABELS, fromText, resolvedSentence, sideText, whoText,
} from './move-conflict-labels';

type View = 'open' | 'resolved';

/**
 * Move conflicts (1.31.0, admin page at `/admin/move-conflicts`): items moved to another library while both copies had their
 * own state - the new library was scanned before the old one, and a user read the new copy meanwhile (or the new folder got
 * a different series link). The new copy keeps its own state until the admin chooses: "Use old" copies the state from before
 * the move onto it, "Keep new" leaves it. One by one, for a selection, or for every open conflict.
 */
@Component({
  selector: 'app-move-conflicts',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page" data-testid="move-conflicts">
      <nav class="back"><a routerLink="/admin"><mat-icon>arrow_back</mat-icon> MangaPixer Administration</a></nav>
      <header class="head">
        <h1>Move conflicts</h1>
        @if (openCount() > 0) { <span class="count" data-testid="move-conflicts-open-count">{{ openCount() }} open</span> }
      </header>
      <p class="intro">When series move to another library and the new library is scanned first, MangaPixer copies each
        user's reading state from the old copies to the new ones. Where someone had already read the new copy differently (or the
        new folder got another series link), both are kept here until you choose. <strong>Use old</strong> puts the state from
        before the move on the new copy; <strong>Keep new</strong> leaves the new copy as it is.</p>

      <div class="toolbar">
        <mat-button-toggle-group [value]="view()" (change)="setView($event.value)" aria-label="Which conflicts" hideSingleSelectionIndicator>
          <mat-button-toggle value="open" data-testid="move-conflicts-view-open">Open</mat-button-toggle>
          <mat-button-toggle value="resolved" data-testid="move-conflicts-view-resolved">Resolved</mat-button-toggle>
        </mat-button-toggle-group>
        @if (view() === 'open' && items().length > 0) {
          <div class="bulk" data-testid="move-conflicts-bulk">
            <mat-checkbox [checked]="allSelected()" [indeterminate]="someSelected()" (change)="toggleAll($event.checked)"
                          data-testid="move-conflicts-select-all">Select all shown</mat-checkbox>
            <button mat-stroked-button type="button" [disabled]="busy() || selected().size === 0" (click)="resolveSelected('Overwrite')"
                    data-testid="move-conflicts-use-old-selected">Use old ({{ selected().size }})</button>
            <button mat-stroked-button type="button" [disabled]="busy() || selected().size === 0" (click)="resolveSelected('Keep')"
                    data-testid="move-conflicts-keep-new-selected">Keep new ({{ selected().size }})</button>
            <button mat-button type="button" [disabled]="busy()" (click)="askAll('Overwrite')" data-testid="move-conflicts-use-old-all">Use old for all</button>
            <button mat-button type="button" [disabled]="busy()" (click)="askAll('Keep')" data-testid="move-conflicts-keep-new-all">Keep new for all</button>
          </div>
        }
      </div>

      @if (confirmAll(); as resolution) {
        <div class="confirm" role="alertdialog" aria-label="Resolve every open conflict" data-testid="move-conflicts-confirm">
          <p>{{ resolution === 'Overwrite' ? 'Use the old state' : 'Keep the new state' }} for all {{ openCount() }} open conflicts?</p>
          <div class="confirm-actions">
            <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="resolveAll(resolution)"
                    data-testid="move-conflicts-confirm-yes">{{ resolution === 'Overwrite' ? 'Use old for all' : 'Keep new for all' }}</button>
            <button mat-button type="button" (click)="confirmAll.set(null)" data-testid="move-conflicts-confirm-no">Cancel</button>
          </div>
        </div>
      }
      @if (message()) { <p class="message" role="status" data-testid="move-conflicts-message">{{ message() }}</p> }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (c of items(); track c.id) {
            <article class="row" [class.resolved]="c.state !== 'Open'" data-testid="move-conflict-row">
              @if (c.state === 'Open') {
                <mat-checkbox class="pick" [checked]="selected().has(c.id)" (change)="toggle(c.id, $event.checked)"
                              [aria-label]="'Select ' + c.title" />
              }
              <div class="body">
                <header>
                  @if (c.isFolder) {
                    <a class="name" [routerLink]="['/libraries', c.libraryId, 'browse', c.nodeId]" data-testid="move-conflict-item">{{ c.title }}</a>
                  } @else {
                    <span class="name" data-testid="move-conflict-item">{{ c.title }}</span>
                  }
                  <span class="kind" data-testid="move-conflict-kind">{{ kindLabels[c.kind] }}</span>
                  @if (c.state !== 'Open') { <span class="done" data-testid="move-conflict-state">{{ stateLabels[c.state] }}</span> }
                </header>
                <p class="meta">
                  {{ c.libraryName }}@if (c.parentTitle) { · {{ c.parentTitle }} } · {{ fromText(c) }} · {{ whoText(c) }}
                </p>
                <div class="sides">
                  <div class="side old"><span class="label">Before the move</span><span data-testid="move-conflict-old">{{ sideText(c.kind, c.old) }}</span></div>
                  <div class="side new"><span class="label">Now</span><span data-testid="move-conflict-new">{{ sideText(c.kind, c.new) }}</span></div>
                </div>
              </div>
              @if (c.state === 'Open') {
                <div class="actions">
                  <button mat-stroked-button type="button" [disabled]="busy()" (click)="resolveOne(c, 'Overwrite')" data-testid="move-conflict-use-old">Use old</button>
                  <button mat-button type="button" [disabled]="busy()" (click)="resolveOne(c, 'Keep')" data-testid="move-conflict-keep-new">Keep new</button>
                </div>
              }
            </article>
          } @empty {
            <div class="empty" data-testid="move-conflicts-empty">
              <mat-icon>done_all</mat-icon>
              <p>{{ view() === 'open' ? 'No open move conflicts.' : 'No resolved move conflicts yet.' }}</p>
            </div>
          }
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)" data-testid="move-conflicts-more">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .page { max-width: 1180px; margin: 0 auto; padding-bottom: 32px; }
    .back a { display: inline-flex; align-items: center; gap: 4px; color: #b39dff; text-decoration: none; font-size: 14px; }
    .back mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 16px; margin: 8px 0 4px; }
    h1 { font-size: 24px; font-weight: 500; margin: 0; }
    .count { font-size: 12px; font-weight: 600; padding: 0 8px; border-radius: 10px; background: #7c4dff; color: #fff; line-height: 20px; }
    .intro { margin: 4px 0 12px; color: #9a9aa8; font-size: 13px; max-width: 860px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: center; margin-bottom: 8px; }
    .bulk { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: center; }
    .confirm { margin: 8px 0; padding: 10px 12px; border-radius: 10px; background: #2a2236; border: 1px solid rgba(179, 157, 255, 0.4); }
    .confirm p { margin: 0 0 8px; }
    .confirm-actions { display: flex; flex-wrap: wrap; gap: 8px; }
    .message { margin: 4px 0 8px; font-size: 13px; color: #a5d6a7; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 8px 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.06); border-left: 3px solid #ffb74d; }
    .row.resolved { border-left-color: #555; }
    .pick { flex: none; margin-top: -6px; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    a.name:hover { text-decoration: underline; }
    .kind, .done { padding: 0 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.08); font-size: 12px; line-height: 20px; }
    .done { background: rgba(129, 199, 132, 0.16); color: #a5d6a7; }
    .meta { margin: 2px 0 6px; font-size: 12px; color: #9a9aa8; overflow-wrap: anywhere; }
    .sides { display: flex; flex-wrap: wrap; gap: 4px 24px; font-size: 13px; }
    .side { display: flex; flex-direction: column; min-width: 0; overflow-wrap: anywhere; }
    .side .label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.04em; color: #8a8a99; }
    .side.old span:last-child { color: #ffcc80; }
    .actions { display: flex; flex-direction: column; gap: 4px; flex: none; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: #8a8a99; text-align: center; }
    .more { display: flex; justify-content: center; margin-top: 12px; }
    @media (max-width: 599.98px) {
      h1 { font-size: 20px; }
      /* Phone: the checkbox stays beside the title; the actions take their own line under both. */
      .row { display: grid; grid-template-columns: auto minmax(0, 1fr); }
      .row.resolved { grid-template-columns: minmax(0, 1fr); }
      .actions { grid-column: 1 / -1; flex-direction: row; justify-content: flex-end; }
    }
  `],
})
export class MoveConflictsComponent implements OnInit {
  private readonly api = inject(MoveConflictsApiService);

  readonly view = signal<View>('open');
  readonly items = signal<MoveConflictDto[]>([]);
  readonly openCount = signal(0);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly confirmAll = signal<MoveConflictResolution | null>(null);

  readonly openItems = computed(() => this.items().filter((c) => c.state === 'Open'));
  readonly allSelected = computed(() => this.openItems().length > 0 && this.openItems().every((c) => this.selected().has(c.id)));
  readonly someSelected = computed(() => this.selected().size > 0 && !this.allSelected());

  readonly kindLabels = MOVE_CONFLICT_KIND_LABELS;
  readonly stateLabels = MOVE_CONFLICT_STATE_LABELS;
  readonly sideText = sideText;
  readonly whoText = whoText;
  readonly fromText = fromText;

  ngOnInit(): void {
    this.load(false);
  }

  setView(view: View): void {
    this.view.set(view);
    this.confirmAll.set(null);
    this.message.set(null);
    this.load(false);
  }

  toggle(id: string, on: boolean): void {
    this.selected.update((s) => {
      const next = new Set(s);
      if (on) next.add(id); else next.delete(id);
      return next;
    });
  }

  toggleAll(on: boolean): void {
    this.selected.set(on ? new Set(this.openItems().map((c) => c.id)) : new Set());
  }

  resolveOne(c: MoveConflictDto, resolution: MoveConflictResolution): void {
    this.resolve({ ids: [c.id], resolution });
  }

  resolveSelected(resolution: MoveConflictResolution): void {
    const ids = [...this.selected()];
    if (ids.length > 0) this.resolve({ ids, resolution });
  }

  askAll(resolution: MoveConflictResolution): void {
    this.confirmAll.set(resolution);
  }

  resolveAll(resolution: MoveConflictResolution): void {
    this.resolve({ all: true, resolution });
  }

  private resolve(request: { ids?: string[]; all?: boolean; resolution: MoveConflictResolution }): void {
    this.busy.set(true);
    this.message.set(null);
    this.api.resolve(request).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.confirmAll.set(null);
        this.message.set(resolvedSentence(r.resolved, r.skipped));
        this.load(false);
      },
      error: () => {
        this.busy.set(false);
        this.message.set('The conflicts could not be resolved.');
      },
    });
  }

  load(more: boolean): void {
    if (!more) {
      this.loading.set(true);
      this.cursor.set(null);
      this.selected.set(new Set());
    }
    this.error.set(null);
    this.api.list(this.view() === 'resolved', more ? this.cursor() : null).subscribe({
      next: (page) => {
        this.items.set(more ? [...this.items(), ...page.items] : page.items);
        this.openCount.set(page.openCount);
        this.cursor.set(page.nextCursor ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The move conflicts could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}
