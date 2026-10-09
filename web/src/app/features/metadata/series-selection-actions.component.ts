import { ChangeDetectionStrategy, Component, Injector, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, forkJoin } from 'rxjs';

import { CatalogNodeDto, FolderMetadataContentDto, MetadataFolderContent, MetadataPrecedence } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { PRECEDENCE_LABELS } from './series-info-labels';
import { rerunMessage } from './rerun-labels';
import { FOLDER_CONTENT_OPTIONS, contentCaption, contentSuggestion, rematchMessage, sumRematch } from './folder-content';
import { DeclaredFactsApiService } from './declared/declared-facts-api.service';
import { ArtistFolderDialogService } from './artist-folder/artist-folder-dialog.service';
import { ARTIST_FOLDER_TIP, artistFolderResultMessage } from './artist-folder/artist-folder-labels';
import { FolderMatchMenuItemDirective } from './folder-match/folder-match-menu-item.directive';

/**
 * Browse selection-bar "Series" menu for admins (1.24.0), mirroring the reading-
 * direction action: mark the selected nodes "Don't match" (or clear it), and set /
 * clear the source precedence on the selected FOLDERS, and (lane B2) "Identify..." when
 * exactly one node is selected. Hidden while "Show series information" is off globally
 * or for this library (it carries the (i) icon, so it reads as series information);
 * the admin settings page is where it is turned back on. Stage 2 adds the folder
 * Content setting for the selected FOLDERS (with the one folder's current value and
 * the detector's suggestion when exactly one is selected). 1.34.0: "Identify one by one" for several selected nodes (the identify dialog's
 * stepping mode), "Re-run matching" for one or several (per-item reasons), and "Collection about..." (exactly one FOLDER: the identify dialog's
 * "pick the series" mode) and "Clear collection" (only clears Collection about rows); 1.37.0: "Artist folder..." (exactly one FOLDER). Link changes are announced
 * through `MetadataStateService` so the cards' (i) update in place.
 */
@Component({
  selector: 'app-series-selection-actions',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatDividerModule, MatTooltipModule, FolderMatchMenuItemDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shown()) {
    <button mat-button type="button" [matMenuTriggerFor]="seriesMenu"
            [disabled]="disabled() || busy() || selectedNodes().length === 0"
            matTooltip="Series metadata for the selection" data-testid="series-selection-menu" (menuOpened)="onMenuOpened()">
      <mat-icon>info_outline</mat-icon><span class="lbl">Series</span>
    </button>
    <mat-menu #seriesMenu="matMenu">
      <button mat-menu-item [disabled]="selectedNodes().length === 0" (click)="identify()" data-testid="bulk-identify"
              [matTooltip]="selectedNodes().length > 1 ? 'One at a time: search, preview and link each, or skip it' : ''" matTooltipPosition="left">
        <mat-icon>travel_explore</mat-icon> Identify{{ selectedNodes().length > 1 ? ' one by one' : '' }}…
      </button>
      <button mat-menu-item [disabled]="selectedNodes().length === 0" (click)="rerun()" data-testid="bulk-rerun"
              matTooltip="Match the selected items again in the background (unlinked items only)" matTooltipPosition="left">
        <mat-icon>refresh</mat-icon> Re-run matching
      </button>
      <button mat-menu-item [disabled]="selectedNodes().length !== 1 || selectedFolders().length !== 1" (click)="collection()"
              matTooltip="A folder of works about a series (fan works): the folder shows the series, its items are matched on their own"
              matTooltipPosition="left" data-testid="bulk-collection">
        <mat-icon>collections_bookmark</mat-icon> Collection about…
      </button>
      <!-- 1.37.0: one artist's works (exactly one FOLDER). -->
      <button mat-menu-item [disabled]="selectedNodes().length !== 1 || selectedFolders().length !== 1" (click)="artistFolder()"
              [matTooltip]="artistTip" matTooltipPosition="left" data-testid="bulk-artist-folder">
        <mat-icon>palette</mat-icon> Artist folder…
      </button>
      <button mat-menu-item [appFolderMatch]="selectedNodes()" [disabled]="selectedFolders().length === 0" data-testid="bulk-folder-match"><mat-icon>drive_file_rename_outline</mat-icon> Match folders by name…</button>
      <mat-divider />
      <button mat-menu-item (click)="dontMatch(true)" data-testid="bulk-dont-match">
        <mat-icon>block</mat-icon> Don't match
      </button>
      <button mat-menu-item (click)="dontMatch(false)" data-testid="bulk-clear-dont-match">
        <mat-icon>undo</mat-icon> Clear "Don't match"
      </button>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="clearCollection()" data-testid="bulk-clear-collection">
        <mat-icon>undo</mat-icon> Clear collection
      </button>
      <mat-divider />
      <span class="caption">Source precedence (folders)</span>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence('WebFirst')" data-testid="bulk-precedence-web">
        <mat-icon>public</mat-icon> Web first
      </button>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence('ComicInfoFirst')" data-testid="bulk-precedence-comicinfo">
        <mat-icon>description</mat-icon> ComicInfo first
      </button>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence(null)" data-testid="bulk-precedence-inherit">
        <mat-icon>vertical_align_top</mat-icon> Inherit (clear)
      </button>
      @if (contentAvailable()) {
        <mat-divider />
        <span class="caption" data-testid="bulk-content-caption">{{ caption() }}</span>
        @for (o of contentOptions; track o.value) {
          <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="content(o.value)" [matTooltip]="o.hint"
                  matTooltipPosition="left" [attr.data-testid]="'bulk-content-' + o.value">
            <mat-icon>{{ single()?.effective === o.value ? 'check' : o.icon }}</mat-icon> {{ o.label }}
            @if (suggested() === o.value) { <span class="suggest">suggested</span> }
          </button>
        }
        <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="content(null)" data-testid="bulk-content-inherit">
          <mat-icon>vertical_align_top</mat-icon> Inherit (clear)
        </button>
      }
    </mat-menu>
    }
  `,
  styles: [`
    :host { display: contents; }
    mat-icon { margin-right: 4px; }
    .suggest { margin-left: 8px; font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px; color: #ffcc80; }
    .caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    @media (max-width: 599.98px) {
      .lbl { display: none; }
      mat-icon { margin-right: 0; }
    }
  `],
})
export class SeriesSelectionActionsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly metadataState = inject(MetadataStateService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly identifyDialog = inject(IdentifyDialogService);
  private readonly artistDialog = inject(ArtistFolderDialogService);
  // Resolved on use only (after a save): hosts that never mark an artist folder do not need the declared-facts API.
  private readonly injector = inject(Injector);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();

  /** Ids of the selected nodes. */
  readonly selected = input.required<ReadonlySet<string>>();

  /** The host's own busy state (bulk read marks etc.). */
  readonly disabled = input(false);

  readonly busy = signal(false);

  /** "Show series information" for this library, from the settings (hidden until known). */
  private readonly showSeriesInfo = signal<boolean | null>(null);
  readonly shown = computed(() => this.showSeriesInfo() === true);

  ngOnInit(): void {
    // Admin-only surface (the host renders it for admins), so the admin settings call is allowed.
    this.api.getSettings().subscribe({
      next: (s) => {
        const libraryId = this.nodes()[0]?.libraryId;
        const library = s.libraries.find((l) => l.libraryId === libraryId);
        this.showSeriesInfo.set(s.showSeriesInfo && (library?.showSeriesInfo ?? true));
      },
      error: () => this.showSeriesInfo.set(true), // cannot tell: keep the admin actions reachable
    });
  }

  readonly selectedNodes = computed(() => this.nodes().filter((n) => this.selected().has(n.id)));
  readonly selectedFolders = computed(() => this.selectedNodes().filter((n) => n.kind === 'Folder'));

  /** Whether the server has the folder Content setting (unknown = shown; a failed read hides it). */
  readonly contentAvailable = signal(true);
  /** The one selected folder's Content, when exactly one folder is selected. */
  readonly single = signal<FolderMetadataContentDto | null>(null);
  readonly contentOptions = FOLDER_CONTENT_OPTIONS;
  readonly caption = computed(() => {
    const folders = this.selectedFolders().length;
    if (folders === 1 && this.single()) return contentCaption(this.single());
    return folders > 1 ? `Content (${folders} folders)` : 'Content (folders)';
  });
  readonly suggested = computed(() => (this.selectedFolders().length === 1 ? contentSuggestion(this.single()) : null));

  onMenuOpened(): void {
    this.single.set(null);
    const folders = this.selectedFolders();
    if (folders.length !== 1) return;
    this.api.getFolderContent(folders[0].id).subscribe({
      next: (c) => {
        this.single.set(c);
        this.contentAvailable.set(true);
      },
      error: (err: { status?: number }) => {
        if (err?.status === 501 || err?.status === 404) this.contentAvailable.set(false);
      },
    });
  }

  content(value: MetadataFolderContent | null): void {
    const folders = this.selectedFolders();
    if (folders.length === 0) return;
    const calls = folders.map((f) => (value ? this.api.setFolderContent(f.id, value) : this.api.clearFolderContent(f.id)));
    const label = value ? FOLDER_CONTENT_OPTIONS.find((o) => o.value === value)?.label ?? value : 'Inherit';
    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: (results) => {
        this.busy.set(false);
        const rematch = sumRematch(results.map((r) => r.rematch));
        const text = `Content (${label}) on ${plural(folders.length, 'folder')}${rematchMessage(rematch)}`;
        if (!rematch?.needsConfirmation) {
          this.snackBar.open(text, 'Close', { duration: rematch?.affected ? 5000 : 2500 });
          return;
        }
        // Over the limit somewhere: "Match again" queues the folders that asked first.
        const asked = results.filter((r) => r.rematch?.needsConfirmation).map((r) => this.api.rematchFolderContent(r.nodeId));
        this.snackBar.open(text, 'Match again', { duration: 15000 }).onAction()
          .subscribe(() => forkJoin(asked).subscribe({
            next: (done) => {
              const n = done.reduce((sum, r) => sum + r.queued, 0);
              this.snackBar.open(`${n} item${n === 1 ? '' : 's'} queued to match again`, 'Close', { duration: 3000 });
            },
            error: (err: { message?: string }) => this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 }),
          }));
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }

  /**
   * Opens the identify dialog for the selected node (the dialog explains a disabled state); with several selected (1.34.0) it
   * steps through them one at a time - link or skip each, no "same record for all".
   */
  identify(): void {
    const nodes = this.selectedNodes();
    if (nodes.length === 1) void this.identifyDialog.open(nodes[0].id);
    else if (nodes.length > 1) void this.identifyDialog.openMany(nodes.map((n) => n.id));
  }

  /** 1.34.0: queues the selected items for automatic matching again and says, per refusal reason, what was not queued. */
  rerun(): void {
    const ids = this.selectedNodes().map((n) => n.id);
    if (ids.length === 0) return;
    // The review bulk call takes at most 200 anchors.
    const chunks: string[][] = [];
    for (let i = 0; i < ids.length; i += 200) chunks.push(ids.slice(i, i + 200));
    this.busy.set(true);
    forkJoin(chunks.map((c) => this.api.reviewBulk('RerunMatching', c))).subscribe({
      next: (results) => {
        this.busy.set(false);
        const all = results.flatMap((r) => r.results);
        const refused = all.some((r) => r.code !== 'ok');
        this.snackBar.open(rerunMessage(all), 'Close', { duration: refused ? 10000 : 3500 });
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** 1.34.0: the identify dialog's "pick the series these works are about" mode for the one selected folder. */
  collection(): void {
    const folders = this.selectedFolders();
    if (folders.length === 1 && this.selectedNodes().length === 1) void this.identifyDialog.open(folders[0].id, 'collection');
  }

  readonly artistTip = ARTIST_FOLDER_TIP;

  /** 1.37.0: asks for the artist (the folder's name by default), then marks the one selected folder an artist's folder. */
  async artistFolder(): Promise<void> {
    const folders = this.selectedFolders();
    if (folders.length !== 1 || this.selectedNodes().length !== 1) return;
    const folder = folders[0];
    const artist = await this.artistDialog.open(folder.displayName);
    if (!artist) return;
    this.busy.set(true);
    this.api.setArtistFolder(folder.id, artist).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.injector.get(DeclaredFactsApiService).version.update((v) => v + 1);
        this.metadataState.refresh(folder.id);
        this.snackBar.open(artistFolderResultMessage(result), 'Close', { duration: 5000 });
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** 1.34.0: clears "Collection about" on the selected folders (any other link stays). */
  clearCollection(): void {
    const folders = this.selectedFolders();
    this.runAll(folders.map((f) => this.api.clearCollection(f.id)), `Collection cleared on ${plural(folders.length, 'folder')}`,
      () => folders.forEach((f) => this.metadataState.refresh(f.id)));
  }

  dontMatch(on: boolean): void {
    const nodes = this.selectedNodes();
    const calls = nodes.map((n) => (on ? this.api.setDontMatch(n.id) : this.api.clearDontMatch(n.id)));
    this.runAll(calls, `${on ? "Don't match set" : "Don't match cleared"} on ${plural(nodes.length, 'item')}`,
      () => nodes.forEach((n) => this.metadataState.refresh(n.id)));
  }

  precedence(value: MetadataPrecedence | null): void {
    const folders = this.selectedFolders();
    const calls: Observable<unknown>[] = folders.map((f) =>
      value ? this.api.setFolderPrecedence(f.id, value) : this.api.clearFolderPrecedence(f.id));
    const label = value ? PRECEDENCE_LABELS[value] : 'Inherit';
    this.runAll(calls, `Source precedence (${label}) on ${plural(folders.length, 'folder')}`);
  }

  private runAll(calls: Observable<unknown>[], message: string, done?: () => void): void {
    if (calls.length === 0) return;
    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        this.busy.set(false);
        done?.();
        this.snackBar.open(message, 'Close', { duration: 2500 });
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }
}

function plural(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`;
}
