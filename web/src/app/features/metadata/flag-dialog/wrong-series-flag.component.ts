import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';

import { MetadataMyFlagDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { isPhone } from '../series-info-overlay.service';
import { FlagDialogData, FlagDialogResult } from './flag-dialog.component';

/**
 * "Wrong series?" for readers (metadata stage 2) on the series panel and page, shown by
 * the host only for non-admins when web data is shown. Reads the reader's OWN flag
 * state: "You reported this" while it is open, "Reviewed" once an admin resolved it (a
 * new report is possible again). Hidden when the server says the reader cannot flag or
 * has no flags (a server without stage 2).
 */
@Component({
  selector: 'app-wrong-series-flag',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (flag(); as f) {
      @if (f.state === 'Open') {
        <span class="state" data-testid="flag-reported"><mat-icon inline>flag</mat-icon> You reported this</span>
      } @else {
        <span class="state done" data-testid="flag-reviewed"><mat-icon inline>task_alt</mat-icon> Reviewed</span>
      }
    }
    @if (canFlag() && flag()?.state !== 'Open') {
      <button mat-button type="button" (click)="open()" data-testid="wrong-series">
        <mat-icon>outlined_flag</mat-icon> Wrong series?
      </button>
    }
  `,
  styles: [`
    :host { display: inline-flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .state { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; color: #ffcc80; }
    .state.done { color: #9a9aa8; }
    button mat-icon { margin-right: 4px; }
  `],
})
export class WrongSeriesFlagComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly dialog = inject(MatDialog);

  /** The node whose information is shown (the server resolves its anchor). */
  readonly nodeId = input.required<string>();
  /** The series title, for the dialog. */
  readonly title = input('');

  readonly canFlag = signal(false);
  readonly flag = signal<MetadataMyFlagDto | null>(null);

  ngOnInit(): void {
    this.api.getMyFlag(this.nodeId()).subscribe({
      next: (s) => {
        this.canFlag.set(s.canFlag);
        this.flag.set(s.flag ?? null);
      },
      error: () => this.canFlag.set(false),
    });
  }

  async open(): Promise<void> {
    const { FlagDialogComponent } = await import('./flag-dialog.component');
    const phone = isPhone();
    const ref = this.dialog.open<unknown, FlagDialogData, FlagDialogResult>(FlagDialogComponent, {
      data: { nodeId: this.nodeId(), title: this.title() },
      width: phone ? '100vw' : '480px',
      maxWidth: '100vw',
      ariaLabel: 'Wrong series?',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    ref.afterClosed().subscribe((flag) => {
      if (flag) this.flag.set(flag);
    });
  }
}
