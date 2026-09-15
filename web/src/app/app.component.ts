import { Component, inject, signal, OnInit } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { CommonModule } from '@angular/common';
import { ApiService } from './core/api/api.service';

/**
 * App shell. Renders the routed outlet plus a small footer showing the product
 * version. The version comes from GET /api/v1/system/info
 * (unauthenticated, sourced from Version.props at build time). The footer is
 * minimal and unobtrusive; it degrades to nothing if the request fails, so it
 * never blocks the app. The reader's full-screen overlay (position:fixed
 * inset:0) naturally hides the footer while reading.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, CommonModule],
  template: `
    <router-outlet></router-outlet>
    @if (version()) {
      <footer class="app-footer" aria-label="Application version">
        MangaPlex {{ version() }}
      </footer>
    }
  `,
  styles: [`
    .app-footer {
      position: fixed;
      bottom: 0;
      left: 0;
      right: 0;
      padding: 2px 8px;
      font-size: 11px;
      color: rgba(255, 255, 255, 0.35);
      text-align: right;
      pointer-events: none;
      z-index: 1;
    }
  `],
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly version = signal<string | null>(null);

  ngOnInit(): void {
    this.api.getSystemInfo().subscribe({
      next: (info) => this.version.set(info.version),
      error: () => { /* leave version null — footer stays hidden */ },
    });
  }
}
