import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Observable } from 'rxjs';

import { TrackerEntryDto } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';
import { ATTENTION_STATES, entryStateText, failureText, numbersText, statusText } from './tracker-texts';

/** How many rows show before "Show more". */
export const ENTRIES_PAGE = 50;

/**
 * The caller's series on one tracker (1.43.0): each linked series with its state in words and its numbers (here, waiting for
 * Send, last sent, the tracker's), and its actions - Sync now, Send (a held jump), "Set <tracker> to N" (may LOWER the tracker,
 * so it asks first), Don't sync / Sync again. "Check my list" is the first-connect preview: the server reads the tracker for 50
 * series at a time and writes nothing; "Send all" sends every waiting jump after a confirm. Series that need a look come first.
 */
@Component({
  selector: 'app-tracker-entries',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="entries" [attr.data-testid]="'tracker-entries-' + slug()">
      <h5>Your series on {{ name() }}</h5>
      <div class="toolbar">
        <button mat-stroked-button type="button" [disabled]="busy() || !canSync()" (click)="preview()"
                [attr.data-testid]="'tracker-preview-' + slug()">
          <mat-icon>fact_check</mat-icon>
          {{ previewRemaining() > 0 ? 'Check ' + Math.min(previewRemaining(), 50) + ' more (' + previewRemaining() + ' left)' : 'Check my list on ' + name() }}
        </button>
        @if (heldCount() > 0) {
          <button mat-stroked-button type="button" [disabled]="busy() || !canSync()" (click)="confirmSendAll.set(true)"
                  [attr.data-testid]="'tracker-send-all-' + slug()">
            <mat-icon>send</mat-icon> Send all ({{ heldCount() }})
          </button>
        }
        @if (attentionCount() > 0 && attentionCount() < entries().length) {
          <button mat-button type="button" (click)="attentionOnly.set(!attentionOnly())" [attr.aria-pressed]="attentionOnly()"
                  [attr.data-testid]="'tracker-attention-' + slug()">
            {{ attentionOnly() ? 'Show all ' + entries().length : 'Only the ' + attentionCount() + ' that need a look' }}
          </button>
        }
      </div>
      <p class="hint">
        "Check my list" reads your {{ name() }} entries for the series here, 50 at a time, to show where MangaPixer is ahead. It
        changes nothing on {{ name() }}.
      </p>
      @if (confirmSendAll()) {
        <div class="confirm" role="alertdialog" [attr.data-testid]="'tracker-send-all-confirm-' + slug()">
          <p>Send the waiting progress of {{ heldCount() === 1 ? '1 series' : heldCount() + ' series' }} to {{ name() }}? This
            includes folders you marked read at once and imported reading. A number on {{ name() }} is never lowered.</p>
          <div class="actions">
            <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="sendAll()"
                    [attr.data-testid]="'tracker-send-all-yes-' + slug()">Send all</button>
            <button mat-button type="button" [disabled]="busy()" (click)="confirmSendAll.set(false)">Cancel</button>
          </div>
        </div>
      }
      @if (notice(); as n) { <p class="ok" role="status" [attr.data-testid]="'tracker-entries-notice-' + slug()">{{ n }}</p> }
      @if (error(); as e) { <p class="error" role="alert" [attr.data-testid]="'tracker-entries-error-' + slug()">{{ e }}</p> }

      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (entries().length === 0) {
        <p class="muted" [attr.data-testid]="'tracker-entries-empty-' + slug()">
          No series yet. Series you read here that {{ name() }} knows show up as you read them.
        </p>
      } @else {
        <ul class="list">
          @for (e of shown(); track e.seriesId) {
            <li [attr.data-testid]="'tracker-entry-' + e.seriesId" [attr.data-state]="e.state">
              <div class="head">
                @if (e.nodeId) {
                  <a class="title" [routerLink]="['/series', e.nodeId]">{{ e.title }}</a>
                } @else {
                  <span class="title">{{ e.title }}</span>
                }
                <span class="state" [class.attention]="isAttention(e)" [attr.data-testid]="'tracker-entry-state-' + e.seriesId">{{ stateText(e) }}</span>
              </div>
              <div class="numbers">
                @if (numbers(e.localChapters, e.localVolumes); as n) { <span>Here: {{ n }}</span> }
                @if (numbers(e.heldChapters, e.heldVolumes); as n) { <span class="held">Waiting for Send: {{ n }}</span> }
                @if (numbers(e.lastSentChapters, e.lastSentVolumes); as n) { <span>Sent: {{ n }}</span> }
                @if (e.remoteMissing) {
                  <span>{{ name() }}: not on your list</span>
                } @else if (remoteText(e); as r) {
                  <span>{{ name() }}: {{ r }}</span>
                }
              </div>
              @if (setting() === e.seriesId) {
                <form class="set" (ngSubmit)="setProgress(e)" [attr.data-testid]="'tracker-set-form-' + e.seriesId">
                  <p class="warn">This replaces what {{ name() }} has for this series - also when {{ name() }} has MORE. Leave a field
                    empty to keep that number.</p>
                  <label>Chapters <input type="number" name="chapters" min="0" max="100000" step="1" [ngModel]="setChapters()"
                         (ngModelChange)="setChapters.set($event)" [attr.data-testid]="'tracker-set-chapters-' + e.seriesId" /></label>
                  <label>Volumes <input type="number" name="volumes" min="0" max="100000" step="1" [ngModel]="setVolumes()"
                         (ngModelChange)="setVolumes.set($event)" [attr.data-testid]="'tracker-set-volumes-' + e.seriesId" /></label>
                  <div class="actions">
                    <button mat-flat-button color="warn" type="submit" [disabled]="busy() || !setValid()"
                            [attr.data-testid]="'tracker-set-yes-' + e.seriesId">Set {{ name() }} to {{ setSummary() }}</button>
                    <button mat-button type="button" [disabled]="busy()" (click)="setting.set(null)">Cancel</button>
                  </div>
                </form>
              } @else {
                <div class="actions">
                  @if (canAct(e)) {
                    <button mat-button type="button" [disabled]="busy()" (click)="act(e, 'sync')"
                            [attr.data-testid]="'tracker-sync-now-' + e.seriesId">Sync now</button>
                  }
                  @if (e.heldChapters != null || e.heldVolumes != null) {
                    <button mat-button type="button" [disabled]="busy() || !canSync()" (click)="act(e, 'send')"
                            [attr.data-testid]="'tracker-send-' + e.seriesId">Send</button>
                  }
                  @if (e.remoteId && !e.dontSync) {
                    <button mat-button type="button" [disabled]="busy() || !canSync()" (click)="openSet(e)"
                            [attr.data-testid]="'tracker-set-' + e.seriesId">Set {{ name() }} to…</button>
                  }
                  <button mat-button type="button" [disabled]="busy()" (click)="toggleDontSync(e)"
                          [attr.data-testid]="'tracker-dont-sync-' + e.seriesId">{{ e.dontSync ? 'Sync again' : "Don't sync" }}</button>
                </div>
              }
            </li>
          }
        </ul>
        @if (visible().length > shown().length) {
          <button mat-button type="button" (click)="limit.set(limit() + pageSize)" [attr.data-testid]="'tracker-entries-more-' + slug()">
            Show more ({{ visible().length - shown().length }})
          </button>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    h5 { margin: 12px 0 4px; font-size: 14px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .toolbar mat-icon { margin-right: 4px; }
    .hint, .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; overflow-wrap: anywhere; }
    .list { list-style: none; padding: 0; margin: 6px 0 0; display: flex; flex-direction: column; gap: 8px; }
    .list li { border: 1px solid rgb(var(--mp-ink-rgb) / 0.08); border-radius: 6px; padding: 6px 10px; min-width: 0; }
    .head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; }
    .title { font-weight: 500; overflow-wrap: anywhere; color: var(--mp-text); }
    a.title { text-decoration: none; }
    a.title:hover { text-decoration: underline; }
    .state { font-size: 12px; color: var(--mp-text-secondary); overflow-wrap: anywhere; }
    .state.attention { color: var(--mp-warn-strong); }
    .numbers { display: flex; flex-wrap: wrap; gap: 2px 14px; font-size: 13px; color: var(--mp-text-secondary); margin: 2px 0; }
    .numbers .held { color: var(--mp-warn-strong); }
    .actions { display: flex; flex-wrap: wrap; gap: 4px 8px; margin-top: 4px; }
    .set { margin: 6px 0 2px; display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: center; font-size: 13px; }
    .set p { flex: 1 1 100%; margin: 2px 0; }
    .set input { font: inherit; width: 90px; padding: 4px 6px; box-sizing: border-box; color: var(--mp-text); background: var(--mp-surface);
                 border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    .set .actions { flex: 1 1 100%; }
    .warn { color: var(--mp-warn-strong); }
    .confirm { border: 1px solid rgb(var(--mp-warn-strong-rgb) / 0.6); border-radius: 6px; padding: 8px 10px; margin: 8px 0; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .ok { color: var(--mp-success-strong); font-size: 13px; margin: 6px 0; }
    .error { color: var(--mp-error-strong); font-size: 13px; margin: 6px 0; }
  `],
})
export class TrackerEntriesComponent implements OnInit {
  private readonly api = inject(TrackersApiService);

  readonly slug = input.required<string>();
  readonly name = input.required<string>();
  /** The account can sync right now (allowed, connected, current consent): the actions that reach the tracker are on. */
  readonly canSync = input(true);

  protected readonly Math = Math;
  readonly pageSize = ENTRIES_PAGE;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly entries = signal<TrackerEntryDto[]>([]);
  readonly limit = signal(ENTRIES_PAGE);
  readonly attentionOnly = signal(false);
  readonly previewRemaining = signal(0);
  readonly confirmSendAll = signal(false);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  /** The series whose "Set to" form is open, and its numbers. */
  readonly setting = signal<string | null>(null);
  readonly setChapters = signal<number | null>(null);
  readonly setVolumes = signal<number | null>(null);

  readonly heldCount = computed(() => this.entries().filter((e) => e.heldChapters != null || e.heldVolumes != null).length);
  readonly attentionCount = computed(() => this.entries().filter((e) => this.isAttention(e)).length);

  /** Series that need a look first, then by title. */
  readonly visible = computed(() => {
    const list = this.attentionOnly() ? this.entries().filter((e) => this.isAttention(e)) : this.entries();
    return [...list].sort((a, b) =>
      Number(this.isAttention(b)) - Number(this.isAttention(a)) || a.title.localeCompare(b.title));
  });
  readonly shown = computed(() => this.visible().slice(0, this.limit()));

  readonly setValid = computed(() => {
    const ok = (v: number | null) => v == null || (Number.isInteger(v) && v >= 0 && v <= 100000);
    return (this.setChapters() != null || this.setVolumes() != null) && ok(this.setChapters()) && ok(this.setVolumes());
  });
  readonly setSummary = computed(() => numbersText(this.setChapters(), this.setVolumes()) ?? '…');

  readonly stateText = entryStateText;
  readonly numbers = numbersText;

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.entries(this.slug()).subscribe({
      next: (list) => { this.entries.set(list); this.loading.set(false); },
      error: (err: unknown) => { this.loading.set(false); this.error.set(failureText(err, this.slug())); },
    });
  }

  isAttention(e: TrackerEntryDto): boolean {
    return ATTENTION_STATES.includes(e.state);
  }

  canAct(e: TrackerEntryDto): boolean {
    return this.canSync() && !e.dontSync && e.state !== 'NoId' && e.state !== 'DontSync';
  }

  remoteText(e: TrackerEntryDto): string | null {
    const n = numbersText(e.remoteChapters, e.remoteVolumes);
    const s = statusText(e.remoteStatus);
    return n && s ? `${n} (${s})` : (n ?? s);
  }

  act(e: TrackerEntryDto, action: 'sync' | 'send'): void {
    const call = action === 'sync' ? this.api.syncNow(this.slug(), e.seriesId) : this.api.send(this.slug(), e.seriesId);
    this.run(call, (updated) => {
      this.replace(updated);
      this.notice.set(`${e.title}: ${entryStateText(updated).toLowerCase()}.`);
    });
  }

  openSet(e: TrackerEntryDto): void {
    this.setChapters.set(e.localChapters ?? e.heldChapters ?? null);
    this.setVolumes.set(e.localVolumes ?? e.heldVolumes ?? null);
    this.setting.set(e.seriesId);
  }

  setProgress(e: TrackerEntryDto): void {
    if (!this.setValid()) return;
    this.run(this.api.setProgress(this.slug(), e.seriesId, this.setChapters(), this.setVolumes()), (updated) => {
      this.replace(updated);
      this.setting.set(null);
      this.notice.set(`${e.title}: ${entryStateText(updated).toLowerCase()}.`);
    });
  }

  toggleDontSync(e: TrackerEntryDto): void {
    this.run(this.api.setDontSync(this.slug(), e.seriesId, !e.dontSync), (updated) => {
      this.replace(updated);
      this.notice.set(updated.dontSync ? `${e.title} is not synced any more.` : `${e.title} syncs again.`);
    });
  }

  /** The first-connect preview: one batch per click; the list then shows what the server read. */
  preview(): void {
    this.run(this.api.preview(this.slug()), (p) => {
      for (const e of p.entries) this.replace(e);
      this.previewRemaining.set(p.remaining);
      this.notice.set(p.capReached
        ? `Checked ${p.entries.length}. Today's request limit for ${this.name()} is reached - try again tomorrow.`
        : `Checked ${p.entries.length} series${p.remaining > 0 ? `, ${p.remaining} left` : ''}. Nothing was changed on ${this.name()}.`);
    });
  }

  sendAll(): void {
    this.run(this.api.sendAll(this.slug()), (r) => {
      this.confirmSendAll.set(false);
      const parts = [`Sent ${r.sent}`];
      if (r.skipped) parts.push(`${r.skipped} unchanged`);
      if (r.failed) parts.push(`${r.failed} failed`);
      this.notice.set(parts.join(', ') + (r.capReached ? `. Today's request limit for ${this.name()} is reached.` : '.'));
      this.load();
    });
  }

  private replace(updated: TrackerEntryDto): void {
    this.entries.update((list) => {
      const i = list.findIndex((x) => x.seriesId === updated.seriesId);
      if (i < 0) return [...list, updated];
      const copy = [...list];
      copy[i] = updated;
      return copy;
    });
  }

  private run<T>(call: Observable<T>, done: (result: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    call.subscribe({
      next: (result) => { this.busy.set(false); done(result); },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, this.slug())); },
    });
  }
}
