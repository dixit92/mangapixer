import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { InfoApiService } from '../info-api.service';
import { CreatorDto, CreatorProviderIdDto } from '../info-types';
import { CreatorDialogData } from './creator-admin.service';
import { creatorErrorText, nameSourceLabel, providerIdLabel } from './creator-admin-labels';

const idKey = (id: CreatorProviderIdDto) => `${id.provider}:${id.externalId}`;

/**
 * "Split..." on a creator chip (admin, 1.45.0, D1 2.2): one creator that is really two people (a shared spelling, or a merge to undo).
 * The admin ticks the names and provider ids that belong to the OTHER person; they move to a new creator, and the rules never put them
 * back. Something must move and something must stay. Step 2 confirms.
 */
@Component({
  selector: 'app-creator-split-dialog',
  standalone: true,
  imports: [MatButtonModule, MatCheckboxModule, MatDialogModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Split creator</h2>
    <mat-dialog-content>
      @if (confirming()) {
        <p class="lead" data-testid="creator-split-confirm">
          Move {{ movingText() }} from <strong>{{ data.display }}</strong> to a new creator?
        </p>
        <p class="small">The works they credit are then listed under the new creator. Merge puts them back together.</p>
      } @else if (creator(); as c) {
        <p class="lead">Tick what belongs to another person than <strong>{{ data.display }}</strong>.</p>
        @if (c.providerIds.length > 0) {
          <p class="group">Ids</p>
          @for (id of c.providerIds; track key(id)) {
            <mat-checkbox [checked]="ids().has(key(id))" (change)="toggleId(id)" data-testid="creator-split-id">{{ idLabel(id) }}</mat-checkbox>
          }
        }
        <p class="group">Names</p>
        @for (n of c.names; track n.name) {
          <mat-checkbox [checked]="names().has(n.name)" (change)="toggleName(n.name)" data-testid="creator-split-name">
            {{ n.name }} <span class="muted">{{ source(n.source) }}</span>
          </mat-checkbox>
        }
        @if (keepsNothing()) { <p class="small warn">Keep at least one name or id on {{ data.display }}.</p> }
      } @else if (!error()) {
        <div class="state"><mat-spinner diameter="24" /></div>
      }
      @if (error(); as e) { <p class="error" role="alert" data-testid="creator-error">{{ e }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (confirming()) {
        <button mat-button type="button" [disabled]="saving()" (click)="confirming.set(false)">Back</button>
      }
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      @if (confirming()) {
        <button mat-flat-button type="button" [disabled]="saving()" (click)="split()" data-testid="creator-split-save">Split</button>
      } @else {
        <button mat-flat-button type="button" [disabled]="!valid()" (click)="confirming.set(true)" data-testid="creator-split-next">Next</button>
      }
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin: 0 0 8px; overflow-wrap: anywhere; }
    .group { margin: 10px 0 2px; font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: var(--mp-text-dim); }
    mat-checkbox { display: block; overflow-wrap: anywhere; }
    .small { margin: 4px 0 0; font-size: 13px; line-height: 1.45; color: var(--mp-text-muted); }
    .warn { color: var(--mp-warn); }
    .muted { font-size: 12px; color: var(--mp-text-dim); }
    .state { display: flex; justify-content: center; padding: 12px; }
    .error { margin: 8px 0 0; color: var(--mp-error); }
  `],
})
export class CreatorSplitDialogComponent implements OnInit {
  readonly data = inject<CreatorDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<CreatorSplitDialogComponent, CreatorDto>>(MatDialogRef);
  private readonly api = inject(InfoApiService);

  readonly creator = signal<CreatorDto | null>(null);
  readonly ids = signal<ReadonlySet<string>>(new Set());
  readonly names = signal<ReadonlySet<string>>(new Set());
  readonly confirming = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  private readonly total = computed(() => (this.creator()?.providerIds.length ?? 0) + (this.creator()?.names.length ?? 0));
  private readonly moving = computed(() => this.ids().size + this.names().size);
  readonly keepsNothing = computed(() => this.total() > 0 && this.moving() >= this.total());
  readonly valid = computed(() => this.moving() > 0 && !this.keepsNothing());
  readonly movingText = computed(() => {
    const parts: string[] = [];
    const n = this.names().size;
    const i = this.ids().size;
    if (n > 0) parts.push(`${n} name${n === 1 ? '' : 's'}`);
    if (i > 0) parts.push(`${i} id${i === 1 ? '' : 's'}`);
    return parts.join(' and ');
  });

  ngOnInit(): void {
    this.api.getCreator(this.data.creatorId).subscribe({
      next: (c) => this.creator.set(c),
      error: (e) => this.error.set(creatorErrorText(e)),
    });
  }

  key(id: CreatorProviderIdDto): string {
    return idKey(id);
  }

  idLabel(id: CreatorProviderIdDto): string {
    return providerIdLabel(id);
  }

  source(s: CreatorDto['names'][number]['source']): string {
    return nameSourceLabel(s);
  }

  toggleId(id: CreatorProviderIdDto): void {
    this.ids.update((set) => toggle(set, idKey(id)));
  }

  toggleName(name: string): void {
    this.names.update((set) => toggle(set, name));
  }

  split(): void {
    const c = this.creator();
    if (!c || !this.valid() || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    const providerIds = c.providerIds.filter((id) => this.ids().has(idKey(id)));
    const names = c.names.map((n) => n.name).filter((n) => this.names().has(n));
    this.api.splitCreator(this.data.creatorId, { providerIds, names }).subscribe({
      next: (created) => this.ref.close(created),
      error: (e) => {
        this.error.set(creatorErrorText(e));
        this.saving.set(false);
      },
    });
  }
}

function toggle(set: ReadonlySet<string>, key: string): ReadonlySet<string> {
  const next = new Set(set);
  if (next.has(key)) next.delete(key);
  else next.add(key);
  return next;
}
