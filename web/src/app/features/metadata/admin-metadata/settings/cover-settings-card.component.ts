import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable } from 'rxjs';

import { ApiError, MetadataSettingsDto } from '../../../../core/api/api-types';
import { CoverApiService } from '../../../../shared/cover-picker/cover-api.service';
import { MetadataApiService } from '../../metadata-api.service';

/**
 * Metadata Manager > Settings "Covers" card (1.29.0 cover layer), self-contained so the settings page carries one line:
 * - "Crop jacket spreads" (global, local only - no request): a volume whose page 1 is an unfolded jacket shows its front half;
 * - "Show saved web covers" per library - SEPARATE from "Show series information" (owner decision): off shows the file
 *   covers in that library; the stored covers stay;
 * - "Delete stored volume covers": the downloaded web covers, the automatic choices that used them and admin choices that
 *   pointed at them go (those items fall back to automatic).
 * Admins also choose a single item's cover with "Cover..." in the browse selection bar or "Choose cover..." in its admin menu.
 */
@Component({
  selector: 'app-cover-settings-card',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatSlideToggleModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (settings(); as s) {
      <section class="card" aria-labelledby="md-covers-h" data-testid="md-covers">
        <h3 id="md-covers-h"><mat-icon aria-hidden="true">image</mat-icon> Covers</h3>
        <mat-slide-toggle [checked]="s.spreadCropEnabled ?? true" [disabled]="busy()" (change)="setCrop($event.checked)"
                          data-testid="md-covers-crop">Crop jacket spreads</mat-slide-toggle>
        <p class="note">When page 1 of a volume is an unfolded jacket (back, spine and front), its card shows the front half.
          Done on this server - no request is sent.</p>

        <h4>Show saved web covers</h4>
        <p class="note">Separate from "Show series information". Off shows each item's own page 1 in that library; the saved covers stay.</p>
        <div class="libs">
          @for (lib of s.libraries; track lib.libraryId) {
            <mat-slide-toggle [checked]="lib.showWebCovers ?? true" [disabled]="busy()" (change)="setLibrary(lib.libraryId, $event.checked)"
                              [attr.data-testid]="'md-covers-lib-' + lib.libraryId">{{ lib.name }}</mat-slide-toggle>
          }
        </div>

        <h4>Stored volume covers</h4>
        @if (confirming()) {
          <span class="confirm">Delete every downloaded volume cover? Items that used one show their own cover again.
            <button mat-flat-button color="warn" type="button" (click)="deleteStored()" data-testid="md-covers-delete-confirm">Delete</button>
            <button mat-button type="button" (click)="confirming.set(false)">Cancel</button></span>
        } @else {
          <button mat-stroked-button type="button" [disabled]="busy()" (click)="confirming.set(true)" data-testid="md-covers-delete">
            Delete stored volume covers</button>
        }
        @if (message()) { <p class="ok small" role="status">{{ message() }}</p> }
        @if (error()) { <p class="error small" role="alert">{{ error() }}</p> }
      </section>
    }
  `,
  styles: [`
    :host { display: contents; }
    .card { background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.07); border-radius: 12px; padding: 14px 18px; min-width: 0; }
    h3 { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; font-size: 16px; font-weight: 500; }
    h3 mat-icon { font-size: 20px; width: 20px; height: 20px; color: #b39dff; }
    h4 { margin: 16px 0 6px; font-size: 14px; }
    .note { font-size: 12px; color: #9a9aa8; margin: 4px 0 8px; }
    .small { font-size: 12px; }
    .libs { display: flex; flex-direction: column; gap: 6px; }
    .confirm { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; font-size: 13px; }
    .ok { color: #4caf50; }
    .error { color: #f44336; }
    @media (max-width: 599.98px) { .card { padding: 12px; } }
  `],
})
export class CoverSettingsCardComponent {
  private readonly api = inject(MetadataApiService);
  private readonly covers = inject(CoverApiService);

  /** The settings as the page loaded them; this card keeps its own copy after a change. */
  readonly initial = input.required<MetadataSettingsDto>();

  readonly settings = signal<MetadataSettingsDto | null>(null);
  readonly busy = signal(false);
  readonly confirming = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => this.settings.set(this.initial()));
  }

  setCrop(on: boolean): void {
    this.save(this.api.updateSettings({ spreadCropEnabled: on }), on ? 'Jacket spreads will be cropped' : 'Jacket spreads are shown whole');
  }

  setLibrary(libraryId: string, show: boolean): void {
    this.save(this.api.updateLibrary(libraryId, { showWebCovers: show }), show ? 'Saved web covers are shown' : 'Saved web covers are hidden');
  }

  deleteStored(): void {
    this.busy.set(true);
    this.error.set(null);
    this.covers.deleteStoredVolumeCovers().subscribe({
      next: (r) => {
        this.busy.set(false);
        this.confirming.set(false);
        this.message.set(`Deleted ${r.coversDeleted} stored cover${r.coversDeleted === 1 ? '' : 's'}.`);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.error.set(e.message || 'The stored covers could not be deleted.');
      },
    });
  }

  private save(call: Observable<MetadataSettingsDto>, message: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.message.set(null);
    call.subscribe({
      next: (s) => {
        this.settings.set(s);
        this.busy.set(false);
        this.message.set(message);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.error.set(e.message || 'The setting could not be saved.');
      },
    });
  }
}
