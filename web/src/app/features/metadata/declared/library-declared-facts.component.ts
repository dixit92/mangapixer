import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { DeclaredFactsScopeDto, LibraryDto } from '../../../core/api/api-types';
import { DeclaredFactsApiService } from './declared-facts-api.service';
import { DeclaredFactsDialogService } from './declared-facts-dialog.service';
import { declaredSummary } from './declared-facts';

/**
 * Library-level declared facts (1.28.0) under each row of the admin Libraries card (a one-line wiring
 * edit in `admin.component.ts`, next to the scan schedule): "Declared: Manga · 2 creators" and an Edit
 * button that opens the declared-facts dialog in library scope. Every folder of the library inherits
 * these unless a folder declares its own.
 */
@Component({
  selector: 'app-library-declared-facts',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="declared-row" data-testid="library-declared">
      <span class="label">Declared:</span>
      <span class="value" data-testid="library-declared-summary">{{ summary() || 'nothing' }}</span>
      <button mat-button type="button" class="edit" (click)="edit()" [attr.aria-label]="'Edit declared facts of ' + library().name"
              data-testid="library-declared-edit">
        <mat-icon>edit_note</mat-icon> Edit
      </button>
    </div>
  `,
  styles: [`
    .declared-row {
      display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px;
      padding: 0 16px 8px 72px; font-size: 13px;
    }
    .label { opacity: 0.75; }
    .value { opacity: 0.9; }
    .edit { min-width: 0; }
    @media (max-width: 600px) {
      .declared-row { padding-left: 16px; }
    }
  `],
})
export class LibraryDeclaredFactsComponent {
  private readonly api = inject(DeclaredFactsApiService);
  private readonly dialog = inject(DeclaredFactsDialogService);
  private readonly destroyRef = inject(DestroyRef);

  /** The library row from the admin card. */
  readonly library = input.required<LibraryDto>();

  readonly scope = signal<DeclaredFactsScopeDto | null>(null);
  readonly summary = computed(() => {
    const own = this.scope()?.own;
    return own ? declaredSummary(own.type, own.creators) : '';
  });

  constructor() {
    effect(() => {
      const id = this.library().id;
      untracked(() => this.load(id));
    });
  }

  edit(): void {
    void this.dialog.open({ kind: 'library', id: this.library().id }).then((saved) => {
      if (saved) this.scope.set(saved);
    });
  }

  private load(id: string): void {
    this.api.get({ kind: 'library', id })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.scope.set(s),
        error: () => this.scope.set(null),
      });
  }
}
