import { Component } from '@angular/core';

import { LibrarySidebarComponent } from '../shared/library-sidebar.component';

/**
 * Phone-only library navigation page (1.10.0, F3).
 *
 * On phone breakpoints, `layout.component` stops mounting `app-library-sidebar`
 * inside the shell (where it used to be squeezed into a cramped horizontal rail
 * at the top) and shows a nav control in the toolbar that routes here instead.
 * This page just reuses the same sidebar content in `pageMode`, which swaps its
 * narrow/sticky/collapsible shell presentation for a plain full-width
 * scrollable list appropriate for a dedicated page.
 *
 * Desktop and iPad are unaffected: this route exists, but the only thing that
 * links to it is the phone-only nav control in `layout.component`, and the
 * shell's own persistent sidebar keeps rendering there exactly as before.
 */
@Component({
  selector: 'app-mobile-library-nav',
  standalone: true,
  imports: [LibrarySidebarComponent],
  template: `
    <h1 class="page-title">Libraries</h1>
    <app-library-sidebar [pageMode]="true"></app-library-sidebar>
  `,
  styles: [`
    :host { display: block; }
    .page-title { margin: 4px 0 12px; font-size: 20px; font-weight: 500; }
  `],
})
export class MobileLibraryNavComponent {}
