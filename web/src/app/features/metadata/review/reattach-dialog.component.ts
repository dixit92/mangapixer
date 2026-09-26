import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ApiService } from '../../../core/api/api.service';
import { ApiError, CatalogNodeDto } from '../../../core/api/api-types';

export interface ReattachDialogData {
  libraryId: string;
  libraryName: string;
  displayName: string;
}

/** The chosen target folder, or undefined when cancelled. */
export type ReattachDialogResult = { targetNodeId: string; targetName: string } | undefined;

interface Crumb {
  id: string | null;
  name: string;
}

/**
 * Folder picker for "Re-attach to..." on the Missing folders tab (stage 2): browses the
 * SAME library's folders (the ordinary browse API, folders only) and returns the one the
 * admin chose. The library root itself is not a valid target (a link lives on a folder).
 */
@Component({
  selector: 'app-reattach-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Re-attach "{{ data.displayName }}"</h2>
    <mat-dialog-content>
      <p class="hint">Choose the folder it became. Its series link, source precedence, Content setting and reading defaults move there.</p>
      <nav class="crumbs" aria-label="Folder path">
        @for (c of crumbs(); track $index; let last = $last) {
          @if (last) {
            <span class="current">{{ c.name }}</span>
          } @else {
            <button mat-button type="button" (click)="goTo($index)">{{ c.name }}</button><span class="sep">›</span>
          }
        }
      </nav>
      @if (loading()) {
        <div class="state"><mat-spinner diameter="24" /></div>
      } @else {
        <div class="folders" role="listbox" aria-label="Folders">
          @for (f of folders(); track f.id) {
            <div class="folder" role="option" [attr.aria-selected]="chosen()?.id === f.id" [class.chosen]="chosen()?.id === f.id">
              <button type="button" class="pick" (click)="chosen.set(f)" data-testid="reattach-folder">
                <mat-icon>folder</mat-icon> {{ f.displayName }}
              </button>
              @if ((f.childFolderCount ?? 0) > 0) {
                <button mat-icon-button type="button" (click)="open(f)" [attr.aria-label]="'Open ' + f.displayName">
                  <mat-icon>chevron_right</mat-icon>
                </button>
              }
            </div>
          } @empty {
            <p class="muted">No folders here.</p>
          }
          @if (cursor()) {
            <button mat-button type="button" (click)="loadMore()">Load more</button>
          }
        </div>
      }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" [disabled]="!chosen()" (click)="confirm()" data-testid="reattach-confirm">
        Re-attach{{ chosen() ? ' to ' + chosen()!.displayName : '' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .hint { font-size: 13px; color: #b0b0c0; margin-top: 0; }
    .crumbs { display: flex; align-items: center; flex-wrap: wrap; gap: 2px; font-size: 13px; margin-bottom: 6px; }
    .crumbs .current { font-weight: 600; padding: 0 8px; }
    .sep { opacity: 0.5; }
    .folders { display: flex; flex-direction: column; max-height: 50vh; overflow-y: auto; }
    .folder { display: flex; align-items: center; border-radius: 6px; }
    .folder.chosen { background: rgba(179, 157, 255, 0.18); }
    .pick { all: unset; flex: 1 1 auto; display: flex; align-items: center; gap: 8px; padding: 8px; cursor: pointer; overflow-wrap: anywhere; }
    .pick:focus-visible { outline: 2px solid #b39dff; }
    .state { display: flex; justify-content: center; padding: 16px; }
    .muted { color: #9a9aa8; }
    .error { color: #f44336; }
  `],
})
export class ReattachDialogComponent implements OnInit {
  readonly data = inject<ReattachDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ReattachDialogComponent, ReattachDialogResult>>(MatDialogRef);
  private readonly api = inject(ApiService);

  readonly crumbs = signal<Crumb[]>([{ id: null, name: this.data.libraryName }]);
  readonly folders = signal<CatalogNodeDto[]>([]);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly chosen = signal<CatalogNodeDto | null>(null);

  ngOnInit(): void {
    this.load(null, null);
  }

  open(folder: CatalogNodeDto): void {
    this.crumbs.update((c) => [...c, { id: folder.id, name: folder.displayName }]);
    this.load(folder.id, null);
  }

  goTo(index: number): void {
    const c = this.crumbs().slice(0, index + 1);
    this.crumbs.set(c);
    this.load(c[c.length - 1].id, null);
  }

  loadMore(): void {
    const c = this.crumbs();
    this.load(c[c.length - 1].id, this.cursor());
  }

  confirm(): void {
    const f = this.chosen();
    if (f) this.ref.close({ targetNodeId: f.id, targetName: f.displayName });
  }

  private load(parentId: string | null, cursor: string | null): void {
    this.loading.set(cursor === null);
    this.error.set(null);
    this.api.browseLibrary(this.data.libraryId, parentId, cursor, 100, 'name').subscribe({
      next: (page) => {
        const folders = page.items.filter((n) => n.kind === 'Folder' && n.availability !== 'Tombstoned');
        this.folders.update((prev) => (cursor ? [...prev, ...folders] : folders));
        this.cursor.set(page.nextCursor ?? null);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Could not list the folders.');
        this.loading.set(false);
      },
    });
  }
}
