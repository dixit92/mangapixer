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
        path: 'search',
        loadComponent: () =>
          import('./features/search/search.component').then((m) => m.SearchComponent),
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
