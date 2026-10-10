import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { MetadataReviewStateService } from './metadata-review-state.service';

/**
 * ONE admin banner for both metadata consents (1.28.0, owner decision 2026-09-28): an update that changed what may
 * be sent, or to which sites, does not carry the old consent over. While "Fetch from the web" or Automatic matching
 * was on under an older consent version (the server derives it from the stored switches and accepted versions), the
 * fetcher stays off and this banner asks the admin to review the allowed sites and accept again in Metadata Manager
 * > Settings. Reads the shared settings snapshot (a local GET, no provider call).
 */
@Component({
  selector: 'app-consent-renewal-banner',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (message(); as m) {
      <div class="renew" role="status" data-testid="consent-renewal-banner">
        <mat-icon aria-hidden="true">gpp_maybe</mat-icon>
        <p>{{ m }} Review the allowed sites and turn it back on in Metadata Manager › Settings.</p>
        @if (inPage()) {
          <button mat-flat-button type="button" (click)="openSettings.emit()" data-testid="consent-renewal-open">Review settings</button>
        } @else {
          <a mat-flat-button routerLink="/admin/metadata" data-testid="consent-renewal-open">Review settings</a>
        }
      </div>
    }
  `,
  styles: [`
    .renew { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 12px; margin: 8px 0 12px; padding: 10px 14px;
      border-radius: 10px; background: rgb(var(--mp-warn-strong-rgb) / 0.1); border: 1px solid rgb(var(--mp-warn-strong-rgb) / 0.35); color: var(--mp-warn); }
    .renew mat-icon { color: var(--mp-warn-strong); flex: none; }
    p { margin: 0; flex: 1 1 260px; font-size: 14px; }
  `],
})
export class ConsentRenewalBannerComponent implements OnInit {
  private readonly state = inject(MetadataReviewStateService);

  /** Inside Metadata Manager: the button switches to the Settings tab instead of navigating. */
  readonly inPage = input(false);
  readonly openSettings = output<void>();

  readonly message = computed(() => {
    const s = this.state.settings();
    if (!s) return null;
    const fetch = !!s.consentRenewalNeeded;
    const auto = !!s.autoConsentRenewalNeeded;
    if (fetch && auto) {
      return 'An update changed what MangaPixer may send to metadata sites, so fetching from the web and Automatic matching are paused until you accept again.';
    }
    if (fetch) return 'An update changed what MangaPixer may send to metadata sites, so fetching from the web is paused until you accept again.';
    if (auto) return 'An update changed what Automatic matching sends, so it is paused until you accept again.';
    return null;
  });

  ngOnInit(): void {
    if (!this.state.settings()) this.state.refreshSettings();
  }
}
