import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';

import { ApiService } from '../../core/api/api.service';
import { LibraryDto } from '../../core/api/api-types';
import { LibraryIconComponent } from '../../shared/library-icon/library-icon.component';

/**
 * Library list component. Shows all libraries the user can access.
 */
@Component({
  selector: 'app-library-list',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatChipsModule, LibraryIconComponent],
  template: `
    <h2>Libraries</h2>
    @if (loading()) {
      <p>Loading...</p>
    } @else if (libraries().length === 0) {
      <p>No libraries available.</p>
    } @else {
      <div class="library-grid">
        @for (lib of libraries(); track lib.id) {
          <mat-card [routerLink]="['/libraries', lib.id]" class="library-card">
            <mat-card-content>
              <app-library-icon [name]="lib.name" [icon]="lib.icon" [size]="48" />
              <h3>{{ lib.name }}</h3>
              @if (lib.itemCount !== null) {
                <p>{{ lib.itemCount }} items</p>
              }
              @if (lib.isScanning) {
                <mat-chip-set>
                  <mat-chip>Scanning...</mat-chip>
                </mat-chip-set>
              }
            </mat-card-content>
          </mat-card>
        }
      </div>
    }
  `,
  styles: [`
    .library-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(250px, 1fr));
      gap: 16px;
    }
    .library-card { cursor: pointer; }
    h3 { margin: 8px 0 4px 0; }
    p { margin: 0; color: #666; font-size: 14px; }
  `],
})
export class LibraryListComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);

  ngOnInit(): void {
    this.api.getLibraries().subscribe({
      next: (libs) => {
        this.libraries.set(libs);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
