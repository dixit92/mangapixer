import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';

import { ApiService } from '../../core/api/api.service';
import { LibraryDto } from '../../core/api/api-types';

/**
 * Admin component. Shows library administration options.
 * No file delete/rename controls are provided.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatButtonModule, MatListModule, MatIconModule],
  template: `
    <h2>Administration</h2>

    <mat-card>
      <mat-card-header>
        <mat-card-title>Libraries</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p>Loading...</p>
        } @else if (libraries().length === 0) {
          <p>No libraries registered.</p>
        } @else {
          <mat-list>
            @for (lib of libraries(); track lib.id) {
              <mat-list-item>
                <mat-icon matListItemIcon>folder</mat-icon>
                <div matListItemTitle>{{ lib.name }}</div>
                <div matListItemLine>
                  @if (lib.itemCount !== null) { {{ lib.itemCount }} items }
                  @if (lib.isScanning) { — Scanning... }
                </div>
              </mat-list-item>
            }
          </mat-list>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`mat-card { margin-bottom: 16px; }`],
})
export class AdminComponent implements OnInit {
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
