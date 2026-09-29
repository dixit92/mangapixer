import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable } from 'rxjs';

import { FolderMetadataContentDto, MetadataFolderContent, MetadataPrecedence, SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { FOLDER_CONTENT_OPTIONS, contentCaption, contentSuggestion, rematchMessage } from './folder-content';
import { DeclaredFactsDialogService } from './declared/declared-facts-dialog.service';
import { MangaDexMatchDialogService } from './companion/mangadex-match-dialog.service';

/**
 * Admin menu for one node's series metadata (1.24.0), shared by the overlay and the
 * series page. Acts on the node that was asked about (`info.nodeId`):
 * - Identify... (lane B2) - opens the identify dialog. Enabled when the web switches
 *   allow it for this node's library (checked when the menu opens, no network); when
 *   not, the item says why. Refresh re-fetches the web record that applies.
 * - Don't match / Clear Don't match - the node is not one series; nothing is inherited.
 * - Unlink - removes the node's own web link (inheritance resumes).
 * - Source precedence (folders): inherit / web first / ComicInfo first.
 * - Change MangaDex match... (1.29.0, a node with a web link): the MangaDex record that gives the linked series its
 *   volume covers and volume list - choose another, "Not on MangaDex", or check again.
 * - Content (folders, stage 2): Auto / Doujinshi & adult one-shots / Not doujinshi, with
 *   where the current value comes from and the detector's suggestion (loaded when the
 *   menu opens; hidden when the server has no Content setting).
 * Emits `changed` after a successful change so the host re-resolves the info; a link
 * change (Don't match, Clear, Unlink) is also announced through `MetadataStateService`
 * so the browse card (i) and top-bar button update without a reload.
 */
@Component({
  selector: 'app-series-admin-actions',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatDividerModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-stroked-button type="button" [matMenuTriggerFor]="adminMenu" [disabled]="busy()" (menuOpened)="onMenuOpened()"
            aria-label="Series admin actions" data-testid="series-admin-menu">
      <mat-icon>admin_panel_settings</mat-icon> Admin
    </button>
    <mat-menu #adminMenu="matMenu">
      <!-- Lane B2: web identify (the dialog explains a disabled state too). -->
      <button mat-menu-item data-slot="identify" data-testid="identify" [disabled]="!identifyAvailable()" (click)="identify()">
        <mat-icon>travel_explore</mat-icon>
        <span>{{ info().link?.state === 'Confirmed' && !info().link?.inherited ? 'Change match…' : 'Identify…' }}</span>
      </button>
      @if (identifyReason(); as reason) {
        <span class="why" data-testid="identify-why">{{ reason }}</span>
      }
      @if (info().web) {
        <button mat-menu-item [disabled]="!identifyAvailable()" (click)="refresh()" data-testid="refresh">
          <mat-icon>refresh</mat-icon> Refresh from {{ info().web!.providerName }}
        </button>
        @if (info().web!.provider === 'mangaupdates') {
          <button mat-menu-item (click)="changeMangaDex()" data-testid="mangadex-match">
            <mat-icon>photo_library</mat-icon> Change MangaDex match…
          </button>
        }
      }
      <mat-divider />
      @if (ownDontMatch()) {
        <button mat-menu-item (click)="clearDontMatch()" data-testid="clear-dont-match">
          <mat-icon>undo</mat-icon> Clear "Don't match"
        </button>
      } @else {
        <button mat-menu-item (click)="dontMatch()" data-testid="dont-match"
                matTooltip="Not one series: nothing is inherited from above">
          <mat-icon>block</mat-icon> Don't match
        </button>
      }
      @if (ownLink()) {
        <button mat-menu-item (click)="unlink()" data-testid="unlink">
          <mat-icon>link_off</mat-icon> Unlink
        </button>
      }
      @if (isFolder()) {
        <mat-divider />
        <span class="caption">Source precedence</span>
        <button mat-menu-item (click)="setPrecedence(null)" data-testid="precedence-inherit">
          <mat-icon>vertical_align_top</mat-icon> Inherit
        </button>
        <button mat-menu-item (click)="setPrecedence('WebFirst')" data-testid="precedence-web">
          <mat-icon>public</mat-icon> Web first
        </button>
        <button mat-menu-item (click)="setPrecedence('ComicInfoFirst')" data-testid="precedence-comicinfo">
          <mat-icon>description</mat-icon> ComicInfo first
        </button>
        <mat-divider />
        <button mat-menu-item (click)="editDeclared()" data-testid="declared-facts">
          <mat-icon>edit_note</mat-icon> Declared facts…
        </button>
        @if (content(); as c) {
          <mat-divider />
          <span class="caption" data-testid="content-caption">{{ caption() }}</span>
          @for (o of contentOptions; track o.value) {
            <button mat-menu-item (click)="setContent(o.value)" [matTooltip]="o.hint" matTooltipPosition="left"
                    [attr.data-testid]="'content-' + o.value">
              <mat-icon>{{ c.effective === o.value ? 'check' : o.icon }}</mat-icon>
              <span>{{ o.label }}</span>
              @if (suggested() === o.value) { <span class="suggest" data-testid="content-suggested">suggested</span> }
            </button>
          }
          @if (c.content) {
            <button mat-menu-item (click)="setContent(null)" data-testid="content-inherit">
              <mat-icon>vertical_align_top</mat-icon> Inherit (clear)
            </button>
          }
        }
      }
    </mat-menu>
  `,
  styles: [`
    :host { display: inline-flex; }
    button mat-icon { margin-right: 4px; }
    .why { display: block; max-width: 240px; padding: 0 16px 6px 56px; font-size: 12px; color: #9a9aa8; }
    .suggest { margin-left: 8px; font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px; color: #ffcc80; }
    .caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
  `],
})
export class SeriesAdminActionsComponent {
  private readonly api = inject(MetadataApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly identifyDialog = inject(IdentifyDialogService);
  private readonly metadataState = inject(MetadataStateService);
  private readonly declaredDialog = inject(DeclaredFactsDialogService);
  private readonly mangaDexDialog = inject(MangaDexMatchDialogService);

  readonly info = input.required<SeriesInfoDto>();
  readonly changed = output<void>();

  readonly busy = signal(false);

  /** Whether the web switches allow Identify/Refresh here; null until checked. */
  readonly identifyAvailable = signal(false);
  readonly identifyReason = signal<string | null>(null);

  /** Folder Content setting (stage 2); null until loaded or when unavailable. */
  readonly content = signal<FolderMetadataContentDto | null>(null);
  readonly contentOptions = FOLDER_CONTENT_OPTIONS;
  readonly caption = computed(() => contentCaption(this.content()));
  readonly suggested = computed(() => contentSuggestion(this.content()));

  onMenuOpened(): void {
    this.checkIdentify();
    if (this.isFolder()) this.loadContent();
  }

  /** Local read; a server without the Content setting leaves the section hidden. */
  loadContent(): void {
    this.api.getFolderContent(this.info().nodeId).subscribe({
      next: (c) => this.content.set(c),
      error: () => this.content.set(null),
    });
  }

  /** null clears the folder's own value (the nearest ancestor's applies again). */
  setContent(value: MetadataFolderContent | null): void {
    const id = this.info().nodeId;
    this.busy.set(true);
    const call = value ? this.api.setFolderContent(id, value) : this.api.clearFolderContent(id);
    call.subscribe({
      next: (c) => {
        this.busy.set(false);
        this.content.set(c);
        const text = (value ? `Content set: ${contentLabel(value)}` : 'Content cleared (inherited again)') + rematchMessage(c.rematch);
        if (c.rematch?.needsConfirmation) {
          this.snackBar.open(text, 'Match again', { duration: 15000 }).onAction()
            .subscribe(() => this.api.rematchFolderContent(id).subscribe({
              next: (r) => this.snackBar.open(`${r.queued} item${r.queued === 1 ? '' : 's'} queued to match again`, 'Close', { duration: 3000 }),
              error: (err: { message?: string }) => this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 }),
            }));
        } else {
          this.snackBar.open(text, 'Close', { duration: c.rematch?.affected ? 5000 : 2500 });
        }
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** Asks the server (no network call) whether web lookups are allowed for this node. */
  checkIdentify(): void {
    this.api.getIdentifyContext(this.info().nodeId).subscribe({
      next: (ctx) => {
        this.identifyAvailable.set(ctx.fetchAvailable);
        this.identifyReason.set(ctx.fetchAvailable ? null : ctx.unavailableMessage ?? 'Web lookups are off.');
      },
      error: () => {
        this.identifyAvailable.set(false);
        this.identifyReason.set('Could not check whether web lookups are on.');
      },
    });
  }

  /** Declared type / creators for this folder (1.28.0); the "Declared" line re-reads itself after a save. */
  editDeclared(): void {
    void this.declaredDialog.open({ kind: 'folder', id: this.info().nodeId });
  }

  identify(): void {
    void this.identifyDialog.open(this.info().nodeId).then((linked) => {
      if (linked) this.changed.emit();
    });
  }

  /** The MangaDex companion of the linked series (1.29.0); the dialog makes every change itself. */
  changeMangaDex(): void {
    void this.mangaDexDialog.open(this.info().nodeId).then((changed) => {
      if (changed) this.changed.emit();
    });
  }

  refresh(): void {
    this.run(this.api.refresh(this.info().nodeId), 'Series information refreshed');
  }

  /** The node's OWN row (not inherited) is a Don't match. */
  readonly ownDontMatch = computed(() => {
    const link = this.info().link;
    return !!link && !link.inherited && link.state === 'DontMatch';
  });

  /** The node's OWN row is a web link. */
  readonly ownLink = computed(() => {
    const link = this.info().link;
    return !!link && !link.inherited && link.state !== 'DontMatch';
  });

  readonly isFolder = computed(() => this.info().nodeKind === 'Folder');

  dontMatch(): void {
    this.run(this.api.setDontMatch(this.info().nodeId), "Marked Don't match", true);
  }

  clearDontMatch(): void {
    this.run(this.api.clearDontMatch(this.info().nodeId), "Don't match cleared", true);
  }

  unlink(): void {
    this.run(this.api.unlink(this.info().nodeId), 'Series link removed', true);
  }

  setPrecedence(precedence: MetadataPrecedence | null): void {
    const id = this.info().nodeId;
    const call: Observable<unknown> = precedence
      ? this.api.setFolderPrecedence(id, precedence)
      : this.api.clearFolderPrecedence(id);
    this.run(call, precedence ? 'Source precedence set' : 'Source precedence cleared');
  }

  /** `linkChange`: the node's own link row changed, so browse must learn its new state. */
  run(call: Observable<unknown>, message: string, linkChange = false): void {
    const nodeId = this.info().nodeId;
    this.busy.set(true);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        if (linkChange) this.metadataState.refresh(nodeId);
        this.snackBar.open(message, 'Close', { duration: 2500 });
        this.changed.emit();
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }
}

function contentLabel(value: MetadataFolderContent): string {
  return FOLDER_CONTENT_OPTIONS.find((o) => o.value === value)?.label ?? value;
}
