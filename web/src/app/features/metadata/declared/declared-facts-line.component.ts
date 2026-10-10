import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { Subscription } from 'rxjs';

import { NodeDeclaredFactsDto } from '../../../core/api/api-types';
import { DeclaredFactsApiService } from './declared-facts-api.service';
import { conflictText, creatorsText, declaredTypeLabel, editionText, hasDeclared, hasEdition, sourceText } from './declared-facts';

/**
 * The "Declared" line of the Info panel and the series page (1.28.0): the type and creators an admin
 * declared on this folder, a folder above it or the library, and - when the linked web record says
 * otherwise - a conflict badge with BOTH sides (owner, 2026-09-27). 1.39.0: the folder's own edition
 * ("Omnibus - 12 volumes", "Completion not tracked"). Renders nothing when nothing is declared. Reads `/nodes/{id}/declared-facts` and again after any declared-facts change.
 */
@Component({
  selector: 'app-declared-facts-line',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shown(); as d) {
      <div class="declared" data-testid="declared-line">
        <p class="facts">
          <span class="muted">Declared:</span>&ngsp;
          @if (d.effective.type) {
            <span [title]="typeSource()" data-testid="declared-type">{{ typeLabel() }}</span>
          }
          @if (d.effective.type && creators()) {&ngsp;·&ngsp;}
          @if (creators()) {
            <span [title]="creatorsSource()" data-testid="declared-creators">{{ creators() }}</span>
          }
          @if ((d.effective.type || creators()) && edition()) {&ngsp;·&ngsp;}
          @if (edition()) {
            <span title="Set on this folder only" data-testid="declared-edition">{{ edition() }}</span>
          }
        </p>
        @if (conflict(); as text) {
          <p class="conflict" role="note" data-testid="declared-conflict">
            <mat-icon inline>warning_amber</mat-icon>
            <span><span class="badge">Conflict</span> {{ text }}</span>
          </p>
        }
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .declared { clear: both; }
    .facts { margin: 2px 0; font-size: 13px; color: var(--mp-text-secondary); }
    .muted { color: var(--mp-text-dim); }
    .conflict { display: flex; align-items: flex-start; gap: 6px; margin: 4px 0; font-size: 13px; color: var(--mp-warn); }
    .conflict mat-icon { flex: none; margin-top: 1px; }
    .badge {
      display: inline-block; padding: 0 6px; margin-right: 4px; border-radius: 4px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.4px; color: var(--mp-surface-raised); background: var(--mp-warn);
    }
  `],
})
export class DeclaredFactsLineComponent {
  private readonly api = inject(DeclaredFactsApiService);

  readonly nodeId = input.required<string>();

  readonly data = signal<NodeDeclaredFactsDto | null>(null);
  private sub?: Subscription;

  /** Only when something is declared (and the answer is for the current node). */
  readonly shown = computed(() => {
    const d = this.data();
    return d && d.nodeId === this.nodeId() && (hasDeclared(d.effective) || hasEdition(d.edition)) ? d : null;
  });

  readonly typeLabel = computed(() => declaredTypeLabel(this.shown()?.effective.type));
  readonly creators = computed(() => creatorsText(this.shown()?.effective.creators));
  readonly edition = computed(() => editionText(this.shown()?.edition));
  readonly typeSource = computed(() => {
    const e = this.shown()?.effective;
    return e ? `Type ${sourceText(e.typeSource, e.typeFrom)}` : '';
  });
  readonly creatorsSource = computed(() => {
    const e = this.shown()?.effective;
    return e ? `Creators ${sourceText(e.creatorsSource, e.creatorsFrom)}` : '';
  });
  readonly conflict = computed(() => conflictText(this.shown()));

  constructor() {
    effect(() => {
      const id = this.nodeId();
      this.api.version();
      untracked(() => this.load(id));
    });
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  private load(nodeId: string): void {
    this.sub?.unsubscribe();
    this.sub = this.api.forNode(nodeId).subscribe({
      next: (d) => this.data.set(d),
      // Declared facts are optional extra information: a failed read shows nothing.
      error: () => this.data.set(null),
    });
  }
}
