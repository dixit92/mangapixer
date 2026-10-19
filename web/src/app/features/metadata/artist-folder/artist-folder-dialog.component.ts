import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';

import { AuthorSearchCandidateDto, SetArtistFolderRequest } from '../../../core/api/api-types';
import { ARTIST_ROLE_OPTIONS } from './artist-folder-labels';
import { AuthorCandidatesComponent } from './author-candidates.component';
import { AuthorSearchApiService } from './author-search-api.service';

export interface ArtistFolderDialogData {
  /** The folder's name: the artist's default name ('' when the caller does not know it - the server then uses it). */
  folderName: string;
  /** 1.45.0: the folder's node id - offers "Find on MangaUpdates" (an author search for that folder); none without it. */
  nodeId?: string | null;
}

/** The artist to declare, or undefined when cancelled. */
export type ArtistFolderDialogResult = SetArtistFolderRequest | undefined;

/** Same limit as the server (`DeclaredFactKeys.MaxValueLength`). */
const MAX_NAME = 200;

/**
 * "Artist folder" (1.37.0): asks for the artist's name (the folder's name by default) and role ("Story & art" by default) before a
 * folder is marked as one artist's works. The dialog only collects the answer; the caller sends it (the review dashboard after its
 * Undo window, the admin menus at once).
 * 1.45.0: "Find on MangaUpdates" searches MangaUpdates' authors for the Artist field's text (else the folder's name) - once per press,
 * nothing else is sent. The authors found are only offered; a picked author fills the name and is returned with its MangaUpdates id,
 * which the caller stores with the mark (that sends nothing).
 */
@Component({
  selector: 'app-artist-folder-dialog',
  standalone: true,
  imports: [AuthorCandidatesComponent, FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Artist folder</h2>
    <mat-dialog-content>
      <p class="lead">
        @if (data.folderName) { Mark <strong>{{ data.folderName }}</strong> as one artist's folder. } @else { Mark this folder as one artist's folder. }
      </p>
      <p class="small" data-testid="artist-folder-explain">
        The folder is never linked to a series and nothing inside inherits a link from it. Each work inside is matched on its own,
        with the artist as a hint. The artist is saved as the folder's declared creator; the saved artist is only compared on this server, never sent.
      </p>
      <form (ngSubmit)="save()" class="fields">
        <mat-form-field appearance="outline" class="name-field" subscriptSizing="dynamic">
          <mat-label>Artist</mat-label>
          <input matInput name="artist" [maxlength]="maxName" [ngModel]="name()" (ngModelChange)="name.set($event)"
                 placeholder="The folder's name" data-testid="artist-folder-name">
          <mat-hint>Empty: the folder's name</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="role-field" subscriptSizing="dynamic">
          <mat-label>Role</mat-label>
          <mat-select [value]="role()" (selectionChange)="role.set($event.value)" aria-label="Artist role" data-testid="artist-folder-role">
            @for (o of roleOptions; track o.value) {
              <mat-option [value]="o.value">{{ o.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </form>
      @if (data.nodeId) {
        <section class="find" data-testid="artist-folder-find-section">
          <button mat-stroked-button type="button" [disabled]="!searchText() || searching()" (click)="find()"
                  data-testid="artist-folder-find">Find on MangaUpdates</button>
          <p class="small">
            Sends only the text in the Artist field (or the folder's name) to MangaUpdates' author search, once, counted in the daily
            budget. The authors found are only offered: pick one to save its MangaUpdates id with the artist.
          </p>
          @if (searching()) { <div class="small" role="status">Searching…</div> }
          @if (searchError(); as e) { <div class="error small" role="alert" data-testid="artist-folder-find-error">{{ e }}</div> }
          @if (candidates(); as list) {
            @if (list.length) {
              <app-author-candidates [candidates]="list" [pick]="pick()" (picked)="choose($event)" [label]="searched()"
                                     testId="artist-folder-candidates" />
            } @else { <div class="small" data-testid="artist-folder-find-none">No author found on MangaUpdates</div> }
          }
        </section>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button type="button" [disabled]="!valid()" (click)="save()" data-testid="artist-folder-save">Mark artist folder</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .lead { margin: 0 0 6px; overflow-wrap: anywhere; }
    .small { margin: 0 0 12px; font-size: 13px; line-height: 1.45; color: var(--mp-text-muted); }
    .fields { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: flex-start; }
    .name-field { flex: 1 1 220px; min-width: 0; }
    .role-field { flex: 0 1 160px; min-width: 140px; }
    .find { margin-top: 12px; padding-top: 8px; border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.12); }
    .find .small { margin: 6px 0 4px; }
    .error { color: var(--mp-error); }
  `],
})
export class ArtistFolderDialogComponent {
  readonly data = inject<ArtistFolderDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ArtistFolderDialogComponent, ArtistFolderDialogResult>>(MatDialogRef);
  private readonly authors = inject(AuthorSearchApiService);

  readonly roleOptions = ARTIST_ROLE_OPTIONS;
  readonly maxName = MAX_NAME;
  readonly name = signal(this.data.folderName ?? '');
  readonly role = signal('author');
  readonly valid = computed(() => this.name().trim().length <= MAX_NAME);

  /** What "Find on MangaUpdates" sends: the Artist field, else the folder's name. */
  readonly searchText = computed(() => (this.name().trim() || this.data.folderName || '').trim().replace(/\s+/g, ' ').slice(0, MAX_NAME));
  readonly searching = signal(false);
  readonly searchError = signal<string | null>(null);
  /** The authors found (null before a search). */
  readonly candidates = signal<AuthorSearchCandidateDto[] | null>(null);
  readonly searched = signal('');
  /** The picked author (index into the candidates), -1 = none. */
  readonly pick = signal(-1);
  /** The picked author, while the name still is its name (editing the name away drops the pick). */
  readonly picked = computed(() => {
    const c = this.candidates()?.[this.pick()];
    return c && c.name === this.name().trim() ? c : null;
  });

  /** One author search for the folder (no automatic retry, no second request until pressed again). */
  async find(): Promise<void> {
    const nodeId = this.data.nodeId;
    const text = this.searchText();
    if (!nodeId || !text || this.searching()) return;
    this.searching.set(true);
    this.searchError.set(null);
    try {
      const result = await firstValueFrom(this.authors.search(nodeId, text));
      this.candidates.set(result.candidates);
      this.searched.set(text);
      this.pick.set(-1);
    } catch (e) {
      this.searchError.set((e as { message?: string })?.message ?? 'The search failed.');
    } finally {
      this.searching.set(false);
    }
  }

  choose(index: number): void {
    this.pick.set(index);
    const c = this.candidates()?.[index];
    if (c) this.name.set(c.name);
  }

  save(): void {
    if (!this.valid()) return;
    const author = this.picked();
    this.ref.close(author
      ? { name: author.name, role: this.role(), provider: author.provider, externalId: author.externalId }
      : { name: this.name().trim() || null, role: this.role() });
  }
}
