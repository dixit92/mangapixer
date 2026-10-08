import { Routes } from '@angular/router';

import { authGuard, adminGuard, setupGuard, loginGuard, passwordChangeGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./layout/layout.component').then((m) => m.LayoutComponent),
    canActivateChild: [authGuard],
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./features/home/home.component').then((m) => m.HomeComponent),
      },
      {
        path: 'libraries',
        loadComponent: () =>
          import('./features/library/library-list.component').then((m) => m.LibraryListComponent),
      },
      {
        path: 'libraries/:libraryId',
        loadComponent: () =>
          import('./features/library/library-browse.component').then((m) => m.LibraryBrowseComponent),
      },
      {
        path: 'libraries/:libraryId/browse',
        loadComponent: () =>
          import('./features/library/library-browse.component').then((m) => m.LibraryBrowseComponent),
      },
      {
        path: 'libraries/:libraryId/browse/:nodeId',
        loadComponent: () =>
          import('./features/library/library-browse.component').then((m) => m.LibraryBrowseComponent),
      },
      {
        // Volumes view (1.29.0): one virtual volume stack of a folder.
        path: 'libraries/:libraryId/browse/:nodeId/volume/:key',
        loadComponent: () =>
          import('./features/library/volume-stack-view.component').then((m) => m.VolumeStackViewComponent),
      },
      {
        // Tankoubon stacks (1.37.0): the stories of one collected volume in a folder that is neither a series nor a collection.
        path: 'libraries/:libraryId/browse/:nodeId/collection/:key',
        loadComponent: () =>
          import('./features/library/collection-stack-view.component').then((m) => m.CollectionStackViewComponent),
      },
      {
        path: 'favorites',
        loadComponent: () =>
          import('./features/favorites/favorites.component').then((m) => m.FavoritesComponent),
      },
      {
        path: 'search',
        loadComponent: () =>
          import('./features/search/search.component').then((m) => m.SearchComponent),
      },
      {
        // Phone-only library nav page (1.10.0, F3) - see layout.component's
        // `isPhone`/toolbar nav control and mobile-library-nav.component.
        path: 'library-nav',
        loadComponent: () =>
          import('./layout/mobile-library-nav.component').then((m) => m.MobileLibraryNavComponent),
      },
      {
        path: 'settings',
        loadComponent: () =>
          import('./features/auth/settings.component').then((m) => m.SettingsComponent),
      },
      {
        path: 'admin',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/admin/admin.component').then((m) => m.AdminComponent),
      },
      {
        // Move conflicts (1.31.0): items moved to another library while both copies had their own state.
        path: 'admin/move-conflicts',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/admin/move-conflicts/move-conflicts.component').then((m) => m.MoveConflictsComponent),
      },
      {
        // Series metadata admin page (stage 2): Settings / Review / Flags / Runs.
        path: 'admin/metadata',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/metadata/admin-metadata/admin-metadata.component').then((m) => m.AdminMetadataComponent),
      },
      {
        // WIRING HOOK (1.17.0 DEBUGUI lane): per-category debug log-level UI.
        // Integrator: keep this route and/or embed <app-debug-log-card /> in AdminComponent.
        path: 'admin/logging',
        canActivate: [adminGuard],
        loadComponent: () =>
          import('./features/admin/debug-log-card.component').then((m) => m.DebugLogCardComponent),
      },
      {
        // Series page (1.24.0): the canonical home of a series; any node id redirects
        // to its anchor inside the component.
        path: 'series/:nodeId',
        loadComponent: () =>
          import('./features/metadata/series-page.component').then((m) => m.SeriesPageComponent),
      },
      {
        path: 'reader/:itemId',
        loadComponent: () =>
          import('./features/reader/reader.component').then((m) => m.ReaderComponent),
      },
    ],
  },
  {
    path: 'setup',
    canActivate: [setupGuard],
    loadComponent: () =>
      import('./features/auth/setup.component').then((m) => m.SetupComponent),
  },
  {
    path: 'login',
    canActivate: [loginGuard],
    loadComponent: () =>
      import('./features/auth/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'password-change',
    canActivate: [passwordChangeGuard],
    loadComponent: () =>
      import('./features/auth/password-change.component').then((m) => m.PasswordChangeComponent),
  },
  {
    path: 'activate',
    loadComponent: () =>
      import('./features/auth/activate.component').then((m) => m.ActivateComponent),
  },
];
