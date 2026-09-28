import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { ApiError, DeclaredCreatorDto, DeclaredFactsScopeDto, DeclaredType } from '../../../core/api/api-types';
import { DeclaredFactsApiService, DeclaredScope } from './declared-facts-api.service';
import {
  DECLARED_MAX_CREATORS,
  DECLARED_MAX_NAME,
  DECLARED_ROLE_OPTIONS,
  DECLARED_TYPE_OPTIONS,
  creatorsText,
  declaredErrorText,
  declaredRoleLabel,
  declaredTypeLabel,
  sourceText,
} from './declared-facts';

export type DeclaredFactsDialogData = DeclaredScope;

/** The saved scope, or undefined when nothing changed. */
export type DeclaredFactsDialogResult = DeclaredFactsScopeDto | undefined;

/**
 * "Declared facts" editor (1.28.0) for one folder or one library, admin only: a type select and a
 * creator list (name + optional role, as chips). What applies from above is shown as the
 * fallback, so an empty field reads "inherits Manhwa from Shelf". Save replaces this scope's
 * declaration; "Clear" removes it (the value from above applies again).
 */
@Component({
  selector: 'app-declared-facts-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatChipsModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Declared facts</h2>
    <mat-dialog-content>
      @if (scope(); as s) {
        <p class="lead" data-testid="declared-lead">
          {{ data.kind === 'library' ? 'For every folder in the library' : 'For this folder and everything below it' }}:
          <strong>{{ s.displayName }}</strong>
        </p>
        <p class="small">
          State what the works here are. A folder below can declare its own. MangaPixer uses this as a hint and shows it
          next to the series information; it never changes a linked record.
        </p>

        <mat-form-field appearance="outline" class="type-field" subscriptSizing="dynamic">
          <mat-label>Type</mat-label>
          <mat-select [value]="type() ?? ''" (selectionChange)="type.set($event.value || null)" data-testid="declared-type-select"
                      aria-label="Declared type">
            <mat-option value="">{{ inheritedTypeText() || 'Not set' }}</mat-option>
            @for (o of typeOptions; track o.value) {
              <mat-option [value]="o.value">{{ o.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <h3 class="section">Creators</h3>
        @if (creators().length > 0) {
          <mat-chip-set aria-label="Declared creators" data-testid="declared-creators">
            @for (c of creators(); track $index) {
              <mat-chip [removable]="true" (removed)="remove($index)">
                {{ c.name }}@if (c.role) {<span class="role"> · {{ roleLabel(c.role) }}</span>}
                <button matChipRemove [attr.aria-label]="'Remove ' + c.name"><mat-icon>cancel</mat-icon></button>
              </mat-chip>
            }
          </mat-chip-set>
        } @else if (inheritedCreatorsText(); as inherited) {
          <p class="small" data-testid="declared-creators-inherited">{{ inherited }}</p>
        } @else {
          <p class="small">None declared.</p>
        }
        <div class="add-row">
          <mat-form-field appearance="outline" class="name-field" subscriptSizing="dynamic">
            <mat-label>Creator name</mat-label>
            <input matInput [maxlength]="maxName" [ngModel]="draftName()" (ngModelChange)="draftName.set($event)"
                   (keydown.enter)="add(); $event.preventDefault()" data-testid="declared-creator-name">
          </mat-form-field>
          <mat-form-field appearance="outline" class="role-field" subscriptSizing="dynamic">
            <mat-label>Role</mat-label>
            <mat-select [value]="draftRole()" (selectionChange)="draftRole.set($event.value)" aria-label="Creator role"
                        data-testid="declared-creator-role">
              @for (o of roleOptions; track o.value) {
                <mat-option [value]="o.value">{{ o.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <button mat-stroked-button type="button" [disabled]="!canAdd()" (click)="add()" data-testid="declared-creator-add">
            <mat-icon>add</mat-icon> Add
          </button>
        </div>
        @if (error()) { <p class="error" role="alert" data-testid="declared-error">{{ error() }}</p> }
      } @else if (error()) {
        <p class="error" role="alert" data-testid="declared-error">{{ error() }}</p>
      } @else {
        <p class="small">Loading…</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (hasOwn()) {
        <button mat-button type="button" class="clear" [disabled]="busy()" (click)="clear()" data-testid="declared-clear">Clear</button>
      }
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" [disabled]="!scope() || busy()" (click)="save()" data-testid="declared-save">Save</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin-top: 0; }
    .small { font-size: 12px; color: #9a9aa8; margin: 4px 0 8px; }
    .type-field { width: 100%; margin-top: 8px; }
    .section { margin: 16px 0 6px; font-size: 13px; font-weight: 600; }
    .role { color: #b0b0c0; }
    .add-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 8px; }
    .name-field { flex: 1 1 200px; }
    .role-field { flex: 0 1 150px; }
    .error { color: #ff8a80; }
    .clear { margin-right: auto; }
  `],
})
export class DeclaredFactsDialogComponent {
  readonly data = inject<DeclaredFactsDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<DeclaredFactsDialogComponent, DeclaredFactsDialogResult>>(MatDialogRef);
  private readonly api = inject(DeclaredFactsApiService);

  readonly typeOptions = DECLARED_TYPE_OPTIONS;
  readonly roleOptions = DECLARED_ROLE_OPTIONS;
  readonly maxName = DECLARED_MAX_NAME;
  readonly roleLabel = declaredRoleLabel;

  readonly scope = signal<DeclaredFactsScopeDto | null>(null);
  readonly type = signal<DeclaredType | null>(null);
  readonly creators = signal<DeclaredCreatorDto[]>([]);
  readonly draftName = signal('');
  readonly draftRole = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly hasOwn = computed(() => {
    const own = this.scope()?.own;
    return !!own && (!!own.type || (own.creators ?? []).length > 0);
  });

  readonly canAdd = computed(() => {
    const name = this.draftName().trim();
    return name.length > 0 && name.length <= DECLARED_MAX_NAME && this.creators().length < DECLARED_MAX_CREATORS;
  });

  /** "Inherit: Manhwa (from Shelf)" for the empty type option; '' when nothing applies from above. */
  readonly inheritedTypeText = computed(() => {
    const inh = this.scope()?.inherited;
    return inh?.type ? `Inherit: ${declaredTypeLabel(inh.type)} (${sourceText(inh.typeSource, inh.typeFrom)})` : '';
  });

  readonly inheritedCreatorsText = computed(() => {
    const inh = this.scope()?.inherited;
    const names = creatorsText(inh?.creators);
    return names ? `Inherits ${names} (${sourceText(inh!.creatorsSource, inh!.creatorsFrom)})` : '';
  });

  constructor() {
    this.api.get(this.data).subscribe({
      next: (s) => this.load(s),
      error: (err: ApiError & { status?: number }) => this.error.set(declaredErrorText(err)),
    });
  }

  private load(s: DeclaredFactsScopeDto): void {
    this.scope.set(s);
    this.type.set(s.own.type ?? null);
    this.creators.set([...(s.own.creators ?? [])]);
  }

  add(): void {
    if (!this.canAdd()) return;
    const name = this.draftName().trim().replace(/\s+/g, ' ');
    const role = this.draftRole() || null;
    const key = `${name.toLowerCase()}|${role ?? ''}`;
    if (!this.creators().some((c) => `${c.name.toLowerCase()}|${c.role ?? ''}` === key)) {
      this.creators.update((list) => [...list, { name, role }]);
    }
    this.draftName.set('');
  }

  remove(index: number): void {
    this.creators.update((list) => list.filter((_, i) => i !== index));
  }

  save(): void {
    // A name typed but not added yet is still meant.
    if (this.draftName().trim()) this.add();
    this.run(this.api.set(this.data, { type: this.type(), creators: this.creators() }));
  }

  clear(): void {
    this.run(this.api.clear(this.data));
  }

  private run(call: ReturnType<DeclaredFactsApiService['set']>): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (s) => {
        this.busy.set(false);
        this.ref.close(s);
      },
      error: (err: ApiError & { status?: number }) => {
        this.busy.set(false);
        this.error.set(declaredErrorText(err));
      },
    });
  }
}
