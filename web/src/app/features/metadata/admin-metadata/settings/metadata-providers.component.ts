import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { ApiError, MetadataProviderDto, MetadataSettingsDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';

/**
 * The provider allowlist (1.28.0, owner decision d) in Metadata Manager > Settings > Web lookups: every approved
 * site as a chip (name, hosts, what it is used for and what is sent), each removable with its x; removed sites are
 * listed under "Not allowed" with "Add back". ONE general consent covers the allowlist - removing or adding back a
 * site never asks for consent again, because the list can only shrink below what was consented. The gateway refuses
 * every request (manual and automatic) to a removed site. Emits the saved settings.
 */
@Component({
  selector: 'app-metadata-providers',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="providers" data-testid="md-providers">
      <h4>Allowed sites</h4>
      <div class="chips" role="list">
        @for (p of allowed(); track p.id) {
          <div class="chip" role="listitem" [attr.data-provider]="p.id" data-testid="md-provider-chip">
            <div class="chip-head">
              <mat-icon class="globe" aria-hidden="true">language</mat-icon>
              <span class="pname">{{ p.name }}</span>
              <span class="hosts">{{ p.hosts.join(', ') }}</span>
              <button mat-icon-button type="button" class="remove" [disabled]="busy() || disabled()"
                      [attr.aria-label]="'Remove ' + p.name + ' from the allowed sites'" (click)="remove(p)"
                      data-testid="md-provider-remove">
                <mat-icon>close</mat-icon>
              </button>
            </div>
            <p class="line"><span class="k">Used for</span> {{ p.usedFor }}</p>
            <p class="line"><span class="k">Sends</span> {{ p.sends }}</p>
          </div>
        } @empty {
          <p class="none" data-testid="md-providers-none">No site is allowed: nothing is fetched from the web.</p>
        }
      </div>
      @if (removed().length > 0) {
        <div class="removed" data-testid="md-providers-removed">
          <span class="muted">Not allowed:</span>
          @for (p of removed(); track p.id) {
            <span class="gone" [attr.data-provider]="p.id">
              <span class="pname">{{ p.name }}</span>
              <button mat-button type="button" [disabled]="busy() || disabled()" (click)="addBack(p)"
                      [attr.aria-label]="'Add ' + p.name + ' back to the allowed sites'" data-testid="md-provider-add">
                <mat-icon>add</mat-icon> Add back
              </button>
            </span>
          }
        </div>
        @if (mangaUpdatesRemoved()) {
          <p class="note" data-testid="md-providers-mu-note">Identify, Automatic matching and refresh use MangaUpdates: they stay off
            until it is added back.</p>
        }
      }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: [`
    h4 { margin: 12px 0 6px; font-size: 14px; font-weight: 500; }
    .chips { display: flex; flex-direction: column; gap: 6px; }
    .chip { padding: 6px 4px 6px 10px; border-radius: 10px; background: rgba(179, 157, 255, 0.08); border: 1px solid rgba(179, 157, 255, 0.22); }
    .chip-head { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .globe { font-size: 18px; width: 18px; height: 18px; color: #b39dff; }
    .pname { font-weight: 500; }
    .hosts { font-size: 12px; color: #9a9aa8; font-family: monospace; overflow-wrap: anywhere; flex: 1 1 auto; }
    .remove { margin-left: auto; }
    .line { margin: 2px 0 0 24px; font-size: 12px; color: #c8c8d4; }
    .k { display: inline-block; min-width: 64px; color: #9a9aa8; }
    .removed { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; margin-top: 8px; font-size: 13px; }
    .gone { display: inline-flex; align-items: center; gap: 2px; padding-left: 8px; border-radius: 10px;
      border: 1px dashed rgba(255, 255, 255, 0.2); color: #9a9aa8; }
    .gone .pname { text-decoration: line-through; }
    .none, .note { font-size: 12px; color: #ffb300; margin: 4px 0; }
    .muted { color: #9a9aa8; }
    .error { color: #f44336; font-size: 13px; }
    @media (max-width: 599.98px) {
      .line { margin-left: 4px; }
      .k { display: block; min-width: 0; }
    }
  `],
})
export class MetadataProvidersComponent {
  private readonly api = inject(MetadataApiService);

  readonly settings = input.required<MetadataSettingsDto>();
  readonly disabled = input(false);
  readonly changed = output<MetadataSettingsDto>();

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly providers = computed(() => this.settings().providers ?? []);
  readonly allowed = computed(() => this.providers().filter((p) => p.allowed));
  readonly removed = computed(() => this.providers().filter((p) => !p.allowed));
  readonly mangaUpdatesRemoved = computed(() => this.removed().some((p) => p.id === 'mangaupdates'));

  remove(p: MetadataProviderDto): void {
    this.save([...this.removed().map((r) => r.id), p.id]);
  }

  addBack(p: MetadataProviderDto): void {
    this.save(this.removed().map((r) => r.id).filter((id) => id !== p.id));
  }

  private save(removedProviders: string[]): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.updateSettings({ removedProviders }).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.changed.emit(s);
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err?.message || 'The allowed sites were not saved');
      },
    });
  }
}
