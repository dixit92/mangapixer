import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { ApiService } from '../../core/api/api.service';
import { ApiError, VolumeScanStatusDto } from '../../core/api/api-types';

/**
 * Volume contents card (1.44.0), next to Performance: the switch "Find the chapters inside volume files" (on by default) and its
 * counts. The scan reads volume files and the chapter files next to them in the background - on the background slots, so
 * "Background work while someone reads" applies - and never sends anything anywhere.
 */
@Component({
  selector: 'app-volume-scan-card',
  standalone: true,
  imports: [MatCardModule, MatSlideToggleModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card class="volume-scan-card" data-testid="volume-scan-card">
      <mat-card-header>
        <mat-card-title>Volume contents</mat-card-title>
        <mat-card-subtitle>Which chapters a volume file holds</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (status(); as s) {
          <mat-slide-toggle [checked]="s.enabled && !s.configDisabled" [disabled]="saving() || s.configDisabled" (change)="setEnabled($event.checked)"
                            data-testid="volume-scan-toggle">
            Find the chapters inside volume files
          </mat-slide-toggle>
          <p class="hint">
            @if (s.configDisabled) {
              <span class="pinned">Switched off by the server configuration.</span>
            }
            Reads volume and chapter files in the background to tell which chapters a volume file holds. Nothing leaves this
            server; files are only read. It follows "Background work while someone reads".
          </p>
          <p class="counts" data-testid="volume-scan-counts">{{ countsText(s) }}</p>
        }
        @if (error(); as e) {
          <div class="error" role="alert">{{ e }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .hint { color: var(--mp-text-muted); font-size: 13px; margin: 8px 0 0; }
    .counts { color: var(--mp-text); font-size: 13px; margin: 8px 0 0; overflow-wrap: anywhere; }
    .pinned { color: var(--mp-warn); font-weight: 500; }
    .muted { color: var(--mp-text-muted); font-size: 14px; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
  `],
})
export class VolumeScanCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly status = signal<VolumeScanStatusDto | null>(null);

  ngOnInit(): void {
    this.api.getVolumeScanStatus().subscribe({
      next: (s) => {
        this.status.set(s);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load the volume contents scan');
        this.loading.set(false);
      },
    });
  }

  /** "12 volume files checked - chapters found inside 3 of them (24 chapter files). 2 folders waiting." (counts only). */
  countsText(s: VolumeScanStatusDto): string {
    const num = (n: number) => n.toLocaleString('en-US');
    const files = (n: number) => `${num(n)} volume file${n === 1 ? '' : 's'}`;
    let text = `${files(s.volumesChecked)} checked`;
    if (s.volumesWithChapters > 0)
      text += ` - chapters found inside ${num(s.volumesWithChapters)} of them (${num(s.chaptersInside)} chapter file${s.chaptersInside === 1 ? '' : 's'})`;
    text += '.';
    if (s.running) text += ' Checking now.';
    else if (s.foldersWaiting) text += ` ${num(s.foldersWaiting)} folder${s.foldersWaiting === 1 ? '' : 's'} waiting.`;
    return text;
  }

  setEnabled(enabled: boolean): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.setVolumeScanEnabled(enabled).subscribe({
      next: (s) => {
        this.status.set(s);
        this.saving.set(false);
      },
      error: (err: ApiError) => {
        this.saving.set(false);
        this.error.set(err?.message || 'Failed to save the switch');
      },
    });
  }
}
