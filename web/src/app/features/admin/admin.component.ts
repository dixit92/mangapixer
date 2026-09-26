import { Component, inject, signal, computed, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import {
  AdminUserDto,
  DirectoryListingDto,
  LibraryDto,
  ReaderMode,
  RegisterLibraryRequest,
  CreateUserRequest,
  RotatingBackupStatusDto,
  RotatingBackupFileDto,
  AuditEventDto,
  SystemPlatform,
  YacReaderDetectDto,
  YacReaderImportPreviewDto,
} from '../../core/api/api-types';
import { libraryPathCopy } from './library-path-copy';
import { DebugLogCardComponent } from './debug-log-card.component';
import { UpdateCheckCardComponent } from './update-check-card.component';
import { MetadataSettingsCardComponent } from './metadata-settings-card/metadata-settings-card.component';
import { BackupSettingsCardComponent } from './backup-settings-card.component';
import { LibraryIconComponent } from '../../shared/library-icon/library-icon.component';
import { LibraryIconPickerComponent } from './library-icon-picker/library-icon-picker.component';
import { LibraryScanScheduleComponent } from './library-scan-schedule/library-scan-schedule.component';
import { AnalyticsCardComponent } from './analytics-card/analytics-card.component';

/**
 * Admin component. Shows library and user administration.
 * No file delete/rename controls are provided.
 *
 * Scan controls (audit defect D39): each library shows a live status chip;
 * while a scan runs, the row polls library state and offers a Cancel control.
 * User rows expose an inline library-access (grants) panel.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [
    CommonModule,
    DebugLogCardComponent,
    UpdateCheckCardComponent,
    MetadataSettingsCardComponent,
    AnalyticsCardComponent,
    BackupSettingsCardComponent,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatListModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    LibraryIconComponent,
    LibraryIconPickerComponent,
    LibraryScanScheduleComponent,
  ],
  template: `
    <h2>Administration</h2>

    <!-- Libraries -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Libraries</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (!loadingLibs() && libraries().length > 0) {
          <div class="lib-actions-bar">
            <button mat-stroked-button type="button" (click)="scanAll()"
                    [disabled]="scanAllBusy()"
                    matTooltip="Scan every registered library now; libraries already scanning are skipped"
                    aria-label="Scan all libraries">
              <mat-icon>{{ scanAllBusy() ? 'hourglass_empty' : 'refresh' }}</mat-icon>
              {{ scanAllBusy() ? 'Starting…' : 'Scan all libraries' }}
            </button>
          </div>
        }
        @if (loadingLibs()) {
          <p>Loading...</p>
        } @else if (libraries().length === 0) {
          <p>No libraries registered.</p>
        } @else {
          <mat-list>
            @for (lib of libraries(); track lib.id) {
              <mat-list-item>
                <span matListItemIcon><app-library-icon [name]="lib.name" [icon]="lib.icon" [size]="24" /></span>
                <div matListItemTitle>{{ lib.name }}</div>
                <div matListItemLine>
                  @if (lib.itemCount !== null) { {{ lib.itemCount }} items }
                  @if (lib.lastScanCompleted && !lib.isScanning) {
                    · last scan {{ lib.lastScanCompleted | date:'short' }}
                  }
                </div>
                <span matListItemMeta class="lib-meta">
                  <mat-form-field appearance="fill" class="dir-select"
                                  floatLabel="always" subscriptSizing="dynamic">
                    <mat-label>Direction</mat-label>
                    <mat-select [value]="lib.defaultReaderMode"
                                (selectionChange)="setLibraryDirection(lib, $event.value)">
                      @for (opt of directionOptions; track opt.label) {
                        <mat-option [value]="opt.value">{{ opt.label }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                  <button mat-icon-button type="button" (click)="regenerateThumbnails(lib)"
                          [disabled]="thumbBusy().has(lib.id)"
                          matTooltip="Regenerate thumbnails" aria-label="Regenerate thumbnails">
                    <mat-icon>{{ thumbBusy().has(lib.id) ? 'hourglass_empty' : 'image' }}</mat-icon>
                  </button>
                  @if (yacDetected(lib.id)?.detected) {
                    <button mat-icon-button type="button" (click)="openYacImport(lib)"
                            matTooltip="Import YACReader reading progress"
                            aria-label="Import YACReader reading progress">
                      <mat-icon>sync_alt</mat-icon>
                    </button>
                  }
                  @if (lib.isScanning) {
                    <span class="chip scanning">
                      <mat-spinner diameter="14"></mat-spinner> Scanning…
                    </span>
                    <button mat-stroked-button color="warn" type="button"
                            [disabled]="!scanRunId(lib.id) || cancelling().has(lib.id)"
                            (click)="cancelScan(lib.id)">
                      Cancel
                    </button>
                  } @else {
                    <button mat-icon-button type="button" (click)="triggerScan(lib.id)"
                            matTooltip="Scan now" aria-label="Scan now">
                      <mat-icon>refresh</mat-icon>
                    </button>
                  }
                  <button mat-icon-button type="button" (click)="openIconPicker(lib)"
                          matTooltip="Change icon" aria-label="Change icon">
                    <mat-icon>palette</mat-icon>
                  </button>
                  <button mat-icon-button type="button" (click)="openRename(lib)"
                          matTooltip="Rename library" aria-label="Rename library">
                    <mat-icon>edit</mat-icon>
                  </button>
                  <button mat-icon-button type="button" (click)="openDelete(lib)"
                          [disabled]="lib.isScanning || anyScanning()"
                          [matTooltip]="anyScanning() ? 'Cannot remove a library while a scan is running' : 'Remove library from MangaPixer'"
                          aria-label="Remove library">
                    <mat-icon>delete_outline</mat-icon>
                  </button>
                </span>
              </mat-list-item>
              <app-library-scan-schedule [library]="lib" />

              @if (iconPickerLibId() === lib.id) {
                <div class="lib-panel">
                  <app-library-icon-picker [name]="lib.name" [current]="lib.icon"
                    (picked)="setLibraryIcon(lib, $event)" (cancelled)="closeLibPanels()" />
                </div>
              }

              @if (renamePanelLibId() === lib.id) {
                <div class="lib-panel">
                  <mat-form-field appearance="fill" class="rename-field" subscriptSizing="dynamic">
                    <mat-label>Library name</mat-label>
                    <input matInput [(ngModel)]="renameDraft" (keyup.enter)="saveRename(lib)"
                           maxlength="200" aria-label="Library name">
                  </mat-form-field>
                  <div class="lib-panel-actions">
                    <button mat-raised-button color="primary" type="button"
                            [disabled]="libActionBusy().has(lib.id) || !renameDraft().trim() || renameDraft().trim() === lib.name"
                            (click)="saveRename(lib)">Save</button>
                    <button mat-button type="button" (click)="closeLibPanels()">Cancel</button>
                  </div>
                </div>
              }

              @if (deletePanelLibId() === lib.id) {
                <div class="lib-panel danger">
                  <div class="lib-panel-msg">
                    <mat-icon>warning</mat-icon>
                    <span>Remove <strong>{{ lib.name }}</strong> from MangaPixer? This deletes
                      MangaPixer's record and reading progress for this library — your files on
                      disk are <strong>not</strong> touched.</span>
                  </div>
                  <div class="lib-panel-actions">
                    <button mat-raised-button color="warn" type="button"
                            [disabled]="libActionBusy().has(lib.id)"
                            (click)="confirmDelete(lib)">Remove library</button>
                    <button mat-button type="button" (click)="closeLibPanels()">Cancel</button>
                  </div>
                </div>
              }

              @if (yacPanelLibId() === lib.id) {
                <div class="yac-panel">
                  <div class="yac-head">
                    <mat-icon>sync_alt</mat-icon>
                    <span>Import YACReader progress into <strong>your</strong> account</span>
                    @if (yacDetected(lib.id)?.dbVersion; as v) { <span class="yac-ver">db v{{ v }}</span> }
                  </div>
                  @if (yacBusy() && !yacPreview()) {
                    <p class="yac-muted">Analyzing YACReader library…</p>
                  } @else if (yacPreview(); as p) {
                    <p class="yac-stats">
                      {{ p.totalComics }} comics · {{ p.mapped }} matched · {{ p.unmapped }} unmatched ·
                      {{ p.conflicts }} already have progress
                    </p>
                    <p class="yac-muted">
                      Only reading progress is imported (covers/metadata are not). Matched by file
                      path. Read state is sticky.
                    </p>
                    <mat-checkbox [checked]="yacOverwrite()"
                                  (change)="toggleYacOverwrite($event.checked)">
                      Overwrite items that already have MangaPixer progress
                    </mat-checkbox>
                    <div class="yac-actions">
                      <button mat-raised-button color="primary" type="button"
                              [disabled]="yacBusy() || p.toImport === 0"
                              (click)="applyYacImport()">
                        {{ p.toImport === 0 ? 'Nothing to import' : 'Import ' + p.toImport + ' item(s)' }}
                      </button>
                      <button mat-button type="button" (click)="closeYacImport()">Cancel</button>
                    </div>
                  }
                </div>
              }
            }
          </mat-list>
        }

        <mat-divider></mat-divider>
        <h4>Register New Library</h4>
        @if (anyScanning()) {
          <p class="scan-notice">
            <mat-icon inline>info</mat-icon>
            A library scan is in progress — registering a new library is paused until it finishes.
          </p>
        }
        <div class="register-form">
          <mat-form-field appearance="outline" floatLabel="always">
            <mat-label>Display Name</mat-label>
            <input matInput [(ngModel)]="newLibName" placeholder="My Manga Collection">
          </mat-form-field>
          <mat-form-field appearance="outline" floatLabel="always">
            <mat-label>{{ pathCopy().label }}</mat-label>
            <input matInput [(ngModel)]="newLibPath" [placeholder]="pathCopy().placeholder">
          </mat-form-field>
          <div class="register-actions">
            <button mat-stroked-button type="button" class="browse-btn" (click)="toggleBrowser()">
              <mat-icon>folder_open</mat-icon> {{ browserOpen() ? 'Hide browser' : 'Browse…' }}
            </button>
            <button mat-raised-button color="primary" (click)="registerLibrary()"
                    [disabled]="!newLibName() || !newLibPath() || anyScanning()">
              Register
            </button>
          </div>
        </div>

        @if (browserOpen()) {
          <div class="browser">
            @if (browseLoading()) {
              <p>Loading…</p>
            } @else if (listing() && !listing()!.available) {
              <p class="browser-hint">{{ pathCopy().noBrowseRootHint }}</p>
            } @else if (listing()) {
              <div class="browser-bar">
                <button mat-icon-button type="button" (click)="browseUp()"
                        [disabled]="listing()!.parent === null" aria-label="Up one folder">
                  <mat-icon>arrow_upward</mat-icon>
                </button>
                <span class="browser-path" [title]="listing()!.current || ''">{{ listing()!.current }}</span>
                <button mat-flat-button color="primary" type="button" (click)="useCurrentFolder()">
                  Use this folder
                </button>
              </div>
              @if (listing()!.entries.length === 0) {
                <p class="browser-hint">No subfolders here.</p>
              } @else {
                <mat-list class="browser-list">
                  @for (entry of listing()!.entries; track entry.path) {
                    <mat-list-item>
                      <mat-icon matListItemIcon>folder</mat-icon>
                      <button type="button" matListItemTitle class="browser-entry"
                              (click)="entry.hasChildren ? browseInto(entry.path) : useFolder(entry.path)">
                        {{ entry.name }}
                      </button>
                      <span matListItemMeta class="browser-actions">
                        <button mat-button type="button" (click)="useFolder(entry.path)">Select</button>
                        @if (entry.hasChildren) {
                          <button mat-icon-button type="button"
                                  (click)="browseInto(entry.path)" aria-label="Open folder">
                            <mat-icon>chevron_right</mat-icon>
                          </button>
                        }
                      </span>
                    </mat-list-item>
                  }
                </mat-list>
              }
            }
          </div>
        }
      </mat-card-content>
    </mat-card>

    <!-- Users -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Users</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loadingUsers()) {
          <p>Loading...</p>
        } @else {
          <mat-list>
            @for (user of users(); track user.id) {
              <mat-list-item>
                <mat-icon matListItemIcon>{{ user.isAdmin ? 'admin_panel_settings' : 'person' }}</mat-icon>
                <div matListItemTitle>{{ user.username }}</div>
                <div matListItemLine>
                  {{ user.isAdmin ? 'Admin' : 'Reader' }}
                  @if (user.isPendingActivation) { — Pending Activation }
                  @if (!user.isActive) { — Disabled }
                </div>
                <span matListItemMeta class="user-meta">
                  <button mat-icon-button type="button" (click)="toggleGrants(user)"
                          matTooltip="Library access" aria-label="Library access">
                    <mat-icon>{{ grantsOpenUserId() === user.id ? 'expand_less' : 'library_books' }}</mat-icon>
                  </button>
                  <button mat-icon-button type="button" (click)="resetPassword(user.id)"
                          matTooltip="Reset password" aria-label="Reset password">
                    <mat-icon>vpn_key</mat-icon>
                  </button>
                  @if (user.isPendingActivation) {
                    <button mat-icon-button type="button" (click)="reissueActivationLink(user)"
                            [disabled]="userActionBusy().has(user.id)"
                            matTooltip="Reissue activation link" aria-label="Reissue activation link">
                      <mat-icon>mail</mat-icon>
                    </button>
                  }
                  <button mat-icon-button type="button" (click)="openDeleteUser(user)"
                          matTooltip="Delete user" aria-label="Delete user">
                    <mat-icon>delete_outline</mat-icon>
                  </button>
                </span>
              </mat-list-item>

              @if (deleteUserPanelId() === user.id) {
                <div class="lib-panel danger">
                  <div class="lib-panel-msg">
                    <mat-icon>warning</mat-icon>
                    <span>Delete <strong>{{ user.username }}</strong>? This permanently removes
                      their account, active sessions, library access, and reading history. This
                      cannot be undone.</span>
                  </div>
                  <div class="lib-panel-actions">
                    <button mat-raised-button color="warn" type="button"
                            [disabled]="userActionBusy().has(user.id)"
                            (click)="confirmDeleteUser(user)">Delete user</button>
                    <button mat-button type="button" (click)="deleteUserPanelId.set(null)">Cancel</button>
                  </div>
                </div>
              }

              @if (grantsOpenUserId() === user.id) {
                <div class="grants">
                  @if (user.isAdmin) {
                    <p class="grants-hint">
                      <mat-icon inline>info</mat-icon>
                      Admins can access every library. Grants apply to reader accounts only.
                    </p>
                  } @else if (grantsLoading()) {
                    <p class="grants-hint">Loading access…</p>
                  } @else if (libraries().length === 0) {
                    <p class="grants-hint">No libraries to grant.</p>
                  } @else {
                    <p class="grants-hint">Select which libraries {{ user.username }} can read:</p>
                    @for (lib of libraries(); track lib.id) {
                      <mat-checkbox
                        [checked]="grantedLibIds().has(lib.id)"
                        [disabled]="grantBusy().has(lib.id)"
                        (change)="setGrant(user.id, lib.id, $event.checked)">
                        {{ lib.name }}
                      </mat-checkbox>
                    }
                  }
                </div>
              }
            }
          </mat-list>
        }

        <mat-divider></mat-divider>
        <h4>Create New User</h4>
        <mat-form-field appearance="outline">
          <mat-label>Username</mat-label>
          <input matInput [(ngModel)]="newUsername">
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Password (optional)</mat-label>
          <input matInput type="password" [(ngModel)]="newUserPassword">
          <mat-hint>Leave blank to generate an activation link instead.</mat-hint>
        </mat-form-field>
        <p>
          <mat-checkbox [(ngModel)]="newUserIsAdmin">Admin role</mat-checkbox>
        </p>
        <button mat-raised-button color="primary" (click)="createUser()" [disabled]="!newUsername()">
          Create User
        </button>
        @if (activationUrl()) {
          <div class="activation-link-box">
            <p>Activation link (share with the user — single-use, expires in 48h):</p>
            <code class="activation-url">{{ activationUrl() }}</code>
            <button mat-stroked-button (click)="copyActivationUrl()">Copy link</button>
          </div>
        }
      </mat-card-content>
    </mat-card>

    <!-- Backups (was "Diagnostics"; the global log-level control was removed - per-category
         logging lives in the Debug Logging card, avoiding an accidental global-DEBUG log explosion) -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Backups</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (backupLoading()) {
          <p>Loading…</p>
        } @else if (backupStatus(); as status) {
          <p class="backup-info">
            @if (!status.enabled) {
              Scheduled backups are disabled.
            } @else {
              Every {{ intervalLabel() }} · keeping last {{ status.retentionCount }}
            }
            <br>
            @if (status.lastSuccessUtc) {
              Last backup {{ status.lastSuccessUtc | date:'short' }}
              ({{ status.retainedCount }} on disk).
            } @else if (status.lastFailureUtc) {
              Last attempt failed — check server logs.
            } @else {
              No backup taken yet.
            }
          </p>
        }
        <button mat-raised-button color="primary" type="button"
                (click)="runBackupNow()"
                [disabled]="backupBusy() || backupLoading()">
          {{ backupBusy() ? 'Backing up…' : 'Back up now' }}
        </button>

        <!-- Restore: pick an on-disk snapshot, or upload an external backup.
             Both stage the restore; the swap applies on the next server
             restart (the live DB is never overwritten while in use). -->
        <h4>Restore from a snapshot</h4>
        @if (backupFilesLoading()) {
          <p>Loading snapshots…</p>
        } @else if (backupFiles().length === 0) {
          <p class="backup-info">{{ backupStatus()?.locationStatus === 'unavailable' ? 'Backup location unavailable.' : 'No snapshots on disk yet. Take a backup first.' }}</p>
        } @else {
          <mat-list class="snapshot-list">
            @for (f of backupFiles(); track f.fileName) {
              <mat-list-item>
                <span matListItemTitle>{{ f.fileName }}</span>
                <span matListItemLine>
                  {{ f.timestampUtc | date:'short' }} · {{ formatSize(f.byteSize) }}
                </span>
                <span matListItemMeta class="snapshot-actions">
                  <button mat-stroked-button type="button"
                          (click)="restoreFromSnapshot(f.fileName)"
                          [disabled]="restoreBusy()">
                    Restore
                  </button>
                </span>
              </mat-list-item>
            }
          </mat-list>
        }

        <h4>Restore from an uploaded file</h4>
        <p class="backup-info">
          Upload a MangaPixer SQLite backup. It is validated before anything is
          replaced; the restore applies on the next restart.
        </p>
        <input #restoreFile type="file" accept=".db,application/octet-stream"
               (change)="onRestoreFileSelected(restoreFile)"
               [disabled]="restoreBusy()">

        @if (restoreMessage(); as msg) {
          <p class="restore-message">{{ msg }}</p>
        }
      </mat-card-content>
    </mat-card>
    <app-backup-settings-card [snapshotCount]="backupFiles().length" (changed)="refreshBackups()" />

    <!-- Audit trail (1.18.0): read side of the previously write-only audit store. -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Audit trail</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (auditLoading()) {
          <p>Loading…</p>
        } @else if (auditEvents().length === 0) {
          <p class="backup-info">No audit events recorded yet.</p>
        } @else {
          <mat-list class="audit-list">
            @for (e of auditEvents(); track e.id) {
              <mat-list-item>
                <span matListItemTitle>{{ e.action }} · {{ e.result }}</span>
                <span matListItemLine>
                  {{ e.timestamp | date:'short' }}
                  @if (e.actorUserName) { · by {{ e.actorUserName }} }
                  @if (e.targetUserId !== null) { · target #{{ e.targetUserId }} }
                </span>
              </mat-list-item>
            }
          </mat-list>
          <div class="audit-pager">
            <button mat-stroked-button type="button"
                    (click)="auditPrevPage()" [disabled]="auditPage() <= 1 || auditLoading()">
              Previous
            </button>
            <span>Page {{ auditPage() }} of {{ auditTotalPages() }}</span>
            <button mat-stroked-button type="button"
                    (click)="auditNextPage()" [disabled]="auditPage() >= auditTotalPages() || auditLoading()">
              Next
            </button>
          </div>
        }
      </mat-card-content>
    </mat-card>

    <!-- Series metadata (1.24.0 lane B2): consent-gated web fetch, budget, per-library toggles -->
    <app-metadata-settings-card />

    <!-- Update Checker (opt-in, off by default) -->
    <app-update-check-card />

    <!-- Admin Analytics dashboard v1 (1.22.0 lane E) -->
    <app-analytics-card />

    <!-- Logging (1.17.0 DEBUGUI lane): a debugging tool, so it is the last card (owner, 2026-09-26) -->
    <app-debug-log-card />
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    mat-form-field { margin-right: 12px; width: 200px; }
    mat-divider { margin: 16px 0; }
    h4 { margin: 8px 0; }
    /* Register form: let the path/name fields span a sensible width (matching the
       browser panel below) instead of the cramped default 200px shared with the
       compact user-creation inputs. */
    .scan-notice {
      display: flex; align-items: center; gap: 6px;
      font-size: 13px; opacity: 0.85; margin: 0 0 8px;
    }
    /* "Scan all libraries" action bar (1.8.1 spacing fix): more breathing room
       ABOVE so it sits clearly below the "Libraries" card title, and less BELOW so
       it reads as attached to the library list it acts on (was 0 top / 12px bottom). */
    .lib-actions-bar { margin: 16px 0 4px; }
    .register-form { max-width: 640px; }
    .register-form mat-form-field { display: block; width: 100%; margin-right: 0; }
    .register-actions { display: flex; gap: 12px; margin-top: 4px; }
    .browse-btn { margin-right: 12px; }
    .lib-meta, .user-meta { display: inline-flex; align-items: center; gap: 8px; }
    /* The Direction dropdown + action buttons live in the list-item meta slot,
       which is taller than a default list line. Let those rows grow and keep the
       meta vertically centered so the dropdowns line up on first paint (they used
       to stagger before the mat-form-field settled its height). */
    ::ng-deep .mat-mdc-list-item:has(.lib-meta, .snapshot-actions) {
      height: auto !important;
      min-height: 76px;
    }
    .lib-meta, .snapshot-actions { align-self: center; }
    /* Restore buttons (Backups > "Restore from a snapshot"): a fixed trailing
       column, vertically centered against the two-line title/timestamp text,
       so the buttons line up regardless of file name or timestamp length
       (owner-reported misalignment after 1.18.0). */
    .snapshot-actions {
      display: inline-flex;
      align-items: center;
      justify-content: flex-end;
      min-width: 88px;
      flex: 0 0 auto;
    }
    .dir-select { width: 150px; }
    .yac-panel {
      margin: 4px 0 12px 56px; padding: 12px 16px;
      border: 1px solid rgba(124, 77, 255, 0.5); border-radius: 8px;
      background: rgba(124, 77, 255, 0.06);
    }
    .yac-head { display: flex; align-items: center; gap: 8px; font-weight: 500; }
    .yac-head mat-icon { color: #b39dff; }
    .yac-ver { font-size: 12px; opacity: 0.7; }
    .yac-stats { margin: 8px 0 4px; font-size: 14px; }
    .yac-muted { margin: 0 0 8px; font-size: 12px; opacity: 0.7; }
    .yac-actions { display: flex; align-items: center; gap: 8px; margin-top: 10px; }
    .lib-panel {
      margin: 4px 0 12px 56px; padding: 12px 16px; border-radius: 8px;
      border: 1px solid rgba(255,255,255,0.14); background: rgba(255,255,255,0.03);
    }
    .lib-panel.danger { border-color: rgba(244, 67, 54, 0.5); background: rgba(244, 67, 54, 0.06); }
    .lib-panel-msg { display: flex; align-items: flex-start; gap: 8px; font-size: 14px; line-height: 1.4; }
    .lib-panel-msg mat-icon { color: #ff8a80; flex: 0 0 auto; }
    .lib-panel-actions { display: flex; align-items: center; gap: 8px; margin-top: 10px; }
    .rename-field { width: 320px; max-width: 100%; }
    .chip {
      display: inline-flex; align-items: center; gap: 6px;
      font-size: 12px; font-weight: 600; padding: 3px 10px; border-radius: 12px;
    }
    .chip.scanning { background: rgba(124, 77, 255, 0.18); color: #b39ddb; }
    .chip mat-spinner { display: inline-block; }
    .grants {
      padding: 8px 16px 16px 72px;
      display: flex; flex-direction: column; gap: 4px;
    }
    .grants-hint {
      font-size: 13px; opacity: 0.8; margin: 0 0 4px;
      display: flex; align-items: center; gap: 6px;
    }
    .backup-info { margin: 0 0 12px; font-size: 13px; opacity: 0.9; }
    .snapshot-list, .audit-list { max-height: 320px; overflow-y: auto; }
    .restore-message { margin: 12px 0 0; font-size: 13px; }
    .audit-pager { display: flex; align-items: center; gap: 12px; margin-top: 12px; font-size: 13px; }
    .browser {
      margin-top: 12px;
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 8px;
      padding: 8px 12px;
      max-width: 640px;
    }
    .browser-bar {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .browser-path {
      flex: 1;
      font-family: monospace;
      font-size: 13px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      opacity: 0.85;
    }
    .browser-hint { font-size: 13px; opacity: 0.8; }
    .browser-list { max-height: 260px; overflow-y: auto; }
    /* Was a plain <div> with a (click) handler (a11y warning: not focusable, no
       keyboard equivalent). It is a real primary action - click the folder name to
       browse into it (or select it, if it has no subfolders) - so it is now a real
       <button>, which is natively focusable and keyboard-activatable; this CSS resets
       the native button chrome so it still reads as list-item title text, not a button. */
    .browser-entry {
      cursor: pointer;
      display: block;
      width: 100%;
      background: none;
      border: none;
      padding: 0;
      margin: 0;
      font: inherit;
      color: inherit;
      text-align: left;
    }
    .browser-actions { display: inline-flex; align-items: center; gap: 4px; }
    .activation-link-box {
      margin-top: 16px; padding: 12px 16px; border-radius: 8px;
      border: 1px solid rgba(76, 175, 80, 0.5); background: rgba(76, 175, 80, 0.06);
    }
    .activation-link-box p { margin: 0 0 8px; font-size: 14px; }
    .activation-url {
      display: block; word-break: break-all; font-size: 13px;
      padding: 8px; background: rgba(0,0,0,0.2); border-radius: 4px; margin-bottom: 8px;
    }
  `],
})
export class AdminComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly snackBar = inject(MatSnackBar);

  // Thumbnail regeneration (1.2.0): per-library in-flight guard.
  readonly thumbBusy = signal<Set<string>>(new Set());

  // Library rename / delete (post-1.2.0): inline panels + per-library busy guard.
  readonly renamePanelLibId = signal<string | null>(null);
  readonly renameDraft = signal('');
  readonly deletePanelLibId = signal<string | null>(null);
  readonly libActionBusy = signal<Set<string>>(new Set());

  // Library icon picker (1.22.0): inline panel, mirrors the rename/delete pattern.
  readonly iconPickerLibId = signal<string | null>(null);

  // YACReader import (1.2.0): per-library detection + a small inline import panel.
  readonly yacDetect = signal<Map<string, YacReaderDetectDto>>(new Map());
  readonly yacPanelLibId = signal<string | null>(null);
  readonly yacPreview = signal<YacReaderImportPreviewDto | null>(null);
  readonly yacOverwrite = signal(false);
  readonly yacBusy = signal(false);

  readonly loadingLibs = signal(true);
  readonly loadingUsers = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly users = signal<AdminUserDto[]>([]);

  readonly newLibName = signal('');
  readonly newLibPath = signal('');

  // Platform-aware register-form copy (added in 1.13.0): null until
  // GET /system/info returns (or on an older server that lacks the field),
  // which falls back to the historical container wording — never blocks the
  // form on this best-effort load.
  readonly platform = signal<SystemPlatform | null>(null);
  readonly pathCopy = computed(() => libraryPathCopy(this.platform()));

  // Directory browser state for the library-registration path picker.
  readonly browserOpen = signal(false);
  readonly browseLoading = signal(false);
  readonly listing = signal<DirectoryListingDto | null>(null);

  // Scan control state (D39). Maps libraryId -> running scanRunId so Cancel can
  // target the active run. `cancelling` guards double-cancel clicks.
  private readonly runningScans = signal<Map<string, string>>(new Map());
  readonly cancelling = signal<Set<string>>(new Set());

  // Scan-all (1.8.0): in-flight guard for the "Scan all libraries" action.
  readonly scanAllBusy = signal(false);

  /** True while any library is scanning — register is paused then (SQLite single-writer). */
  readonly anyScanning = computed(() =>
    this.libraries().some((l) => l.isScanning) || this.runningScans().size > 0);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  // Grants panel state (D39).
  readonly grantsOpenUserId = signal<string | null>(null);
  readonly grantsLoading = signal(false);
  readonly grantedLibIds = signal<Set<string>>(new Set());
  readonly grantBusy = signal<Set<string>>(new Set());

  // User delete / activation-reissue (1.17.0): inline danger-confirm panel for
  // delete, mirroring the library delete pattern; reissue has no confirm step
  // (non-destructive, just replaces an unused link).
  readonly deleteUserPanelId = signal<string | null>(null);
  readonly userActionBusy = signal<Set<string>>(new Set());

  readonly newUsername = signal('');
  readonly newUserPassword = signal('');
  readonly newUserIsAdmin = signal(false);
  readonly activationUrl = signal<string | null>(null);

  // Log-level control (section 9)
  // Global default reading direction per library (1.2.0). null = inherit.
  readonly directionOptions: { value: ReaderMode | null; label: string }[] = [
    { value: null, label: 'Inherit' },
    { value: 'PagedLtr', label: 'Left-to-right' },
    { value: 'PagedRtl', label: 'Right-to-left' },
    { value: 'VerticalWebtoon', label: 'Vertical' },
  ];

  // Rotating database backups status (1.2.0).
  readonly backupLoading = signal(true);
  readonly backupBusy = signal(false);
  readonly backupStatus = signal<RotatingBackupStatusDto | null>(null);

  // Backup restore (1.18.0): on-disk snapshot list + upload restore.
  readonly backupFiles = signal<RotatingBackupFileDto[]>([]);
  readonly backupFilesLoading = signal(true);
  readonly restoreBusy = signal(false);
  readonly restoreMessage = signal<string | null>(null);

  // Audit trail (1.18.0).
  readonly auditEvents = signal<AuditEventDto[]>([]);
  readonly auditLoading = signal(true);
  readonly auditPage = signal(1);
  readonly auditTotal = signal(0);
  readonly auditPageSize = 50;
  readonly auditTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.auditTotal() / this.auditPageSize)));

  ngOnInit(): void {
    this.loadLibraries();
    this.loadUsers();
    this.loadBackupStatus();
    this.loadBackupFiles();
    this.loadAuditTrail();
    this.loadPlatform();
  }

  /** Best-effort; an older server without the field, or a failed request, just keeps the fallback copy. */
  private loadPlatform(): void {
    this.api.getSystemInfo().subscribe({
      next: (info) => this.platform.set(info.platform ?? null),
      error: () => { /* keep the fallback (container) wording */ },
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  scanRunId(libraryId: string): string | undefined {
    return this.runningScans().get(libraryId);
  }

  private loadLibraries(): void {
    this.api.getAllLibraries().subscribe({
      next: (libs) => {
        this.libraries.set(libs);
        this.loadingLibs.set(false);
        // Backfill scanRunIds for any library already scanning (e.g. a scan
        // started before this page loaded, or from another tab), so Cancel works.
        for (const lib of libs) {
          if (lib.isScanning && !this.runningScans().get(lib.id)) {
            this.backfillScanRunId(lib.id);
          }
        }
        this.detectYacForAll(libs);
        this.syncPolling();
      },
      error: () => this.loadingLibs.set(false),
    });
  }

  // --- Thumbnail regeneration (1.2.0) ---

  /**
   * Enqueues durable thumbnail (re)generation for every item in a library that
   * lacks a current thumbnail. Runs in the background on the server; returns the
   * queued count immediately.
   */
  regenerateThumbnails(lib: LibraryDto): void {
    this.thumbBusy.update((s) => new Set(s).add(lib.id));
    this.api.regenerateThumbnails(lib.id).subscribe({
      next: (r) => {
        this.thumbBusy.update((s) => { const n = new Set(s); n.delete(lib.id); return n; });
        this.snackBar.open(
          r.queuedCount > 0
            ? `Generating ${r.queuedCount} thumbnail(s) in the background…`
            : 'All thumbnails are already up to date.',
          'Close', { duration: 4000 });
      },
      error: (err) => {
        this.thumbBusy.update((s) => { const n = new Set(s); n.delete(lib.id); return n; });
        this.snackBar.open(`Thumbnail regenerate failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  // --- Library rename / delete (post-1.2.0, admin-only) ---

  /** Open the inline rename panel for a library (closes the delete panel). */
  openRename(lib: LibraryDto): void {
    this.deletePanelLibId.set(null);
    this.iconPickerLibId.set(null);
    this.renameDraft.set(lib.name);
    this.renamePanelLibId.set(lib.id);
  }

  /** Open the inline delete-confirm panel for a library (closes the other panels). */
  openDelete(lib: LibraryDto): void {
    this.renamePanelLibId.set(null);
    this.iconPickerLibId.set(null);
    this.deletePanelLibId.set(lib.id);
  }

  /** Open the inline icon picker for a library (closes the other panels). */
  openIconPicker(lib: LibraryDto): void {
    this.renamePanelLibId.set(null);
    this.deletePanelLibId.set(null);
    this.iconPickerLibId.set(lib.id);
  }

  /** Close all inline library panels. */
  closeLibPanels(): void {
    this.renamePanelLibId.set(null);
    this.deletePanelLibId.set(null);
    this.iconPickerLibId.set(null);
  }

  /** Set (or clear, when icon is null) the library's admin-picked icon. */
  setLibraryIcon(lib: LibraryDto, icon: string | null): void {
    this.api.setLibraryIcon(lib.id, icon).subscribe({
      next: (updated) => {
        this.libraries.update(libs => libs.map(l => l.id === lib.id ? updated : l));
        this.closeLibPanels();
        this.snackBar.open(`Icon updated for "${lib.name}"`, 'Close', { duration: 2500 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 }),
    });
  }

  private setLibBusy(id: string, busy: boolean): void {
    this.libActionBusy.update((s) => {
      const n = new Set(s);
      if (busy) n.add(id); else n.delete(id);
      return n;
    });
  }

  /** Persist a new display name for the library, then refresh the list. */
  saveRename(lib: LibraryDto): void {
    const name = this.renameDraft().trim();
    if (!name || name === lib.name) return;
    this.setLibBusy(lib.id, true);
    this.api.updateLibrary(lib.id, { displayName: name }).subscribe({
      next: () => {
        this.setLibBusy(lib.id, false);
        this.closeLibPanels();
        this.loadLibraries();
        this.snackBar.open('Library renamed.', 'Close', { duration: 3000 });
      },
      error: (err) => {
        this.setLibBusy(lib.id, false);
        this.snackBar.open(`Rename failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  /**
   * Delete a library's MangaPixer metadata (never the source files). The server
   * refuses with 409 while a scan is running; surface that clearly if it races.
   */
  confirmDelete(lib: LibraryDto): void {
    this.setLibBusy(lib.id, true);
    this.api.unregisterLibrary(lib.id).subscribe({
      next: () => {
        this.setLibBusy(lib.id, false);
        this.closeLibPanels();
        this.loadLibraries();
        this.snackBar.open(`Removed "${lib.name}" from MangaPixer. Source files are untouched.`,
          'Close', { duration: 4000 });
      },
      error: (err) => {
        this.setLibBusy(lib.id, false);
        const msg = err?.error === 'scan_in_progress'
          ? 'Cannot remove a library while a scan is running. Try again once it finishes.'
          : `Remove failed: ${err.message}`;
        this.snackBar.open(msg, 'Close', { duration: 5000 });
      },
    });
  }

  // --- YACReader import (1.2.0) ---

  /** Detect a YACReader library inside each MangaPixer library's root, in parallel. */
  private detectYacForAll(libs: LibraryDto[]): void {
    for (const lib of libs) {
      this.api.detectYacReader(lib.id).subscribe({
        next: (d) => this.yacDetect.update((m) => new Map(m).set(lib.id, d)),
        error: () => { /* detection best-effort; no button if it fails */ },
      });
    }
  }

  yacDetected(libId: string): YacReaderDetectDto | undefined {
    return this.yacDetect().get(libId);
  }

  /** Open the import panel for a library and run a dry-run preview into the admin's account. */
  openYacImport(lib: LibraryDto): void {
    this.yacPanelLibId.set(lib.id);
    this.yacPreview.set(null);
    this.yacOverwrite.set(false);
    this.refreshYacPreview(lib.id);
  }

  closeYacImport(): void {
    this.yacPanelLibId.set(null);
    this.yacPreview.set(null);
  }

  /** Re-run the preview (e.g. after toggling Overwrite, which changes the counts). */
  refreshYacPreview(libId: string): void {
    const userId = this.auth.currentUser()?.id;
    if (!userId) return;
    this.yacBusy.set(true);
    this.api.previewYacReaderImport({ libraryId: libId, targetUserId: userId, overwrite: this.yacOverwrite() })
      .subscribe({
        next: (p) => { this.yacPreview.set(p); this.yacBusy.set(false); },
        error: (err) => { this.yacBusy.set(false); this.snackBar.open(`Preview failed: ${err.message}`, 'Close', { duration: 5000 }); },
      });
  }

  toggleYacOverwrite(value: boolean): void {
    this.yacOverwrite.set(value);
    const libId = this.yacPanelLibId();
    if (libId) this.refreshYacPreview(libId);
  }

  /** Apply the import into the current admin's account. */
  applyYacImport(): void {
    const libId = this.yacPanelLibId();
    const userId = this.auth.currentUser()?.id;
    if (!libId || !userId) return;
    this.yacBusy.set(true);
    this.api.applyYacReaderImport({ libraryId: libId, targetUserId: userId, overwrite: this.yacOverwrite() })
      .subscribe({
        next: (r) => {
          this.yacBusy.set(false);
          this.snackBar.open(
            `Imported ${r.imported} of ${r.mapped} mapped (${r.readMarks} read, ${r.skipped} skipped).`,
            'Close', { duration: 5000 });
          this.closeYacImport();
        },
        error: (err) => { this.yacBusy.set(false); this.snackBar.open(`Import failed: ${err.message}`, 'Close', { duration: 5000 }); },
      });
  }

  /** Refresh library rows without touching the loading flag (used while polling). */
  private refreshLibraries(): void {
    this.api.getAllLibraries().subscribe({
      next: (libs) => {
        const previouslyScanning = new Set(
          this.libraries().filter(l => l.isScanning).map(l => l.id),
        );
        this.libraries.set(libs);
        // Detect scans that just finished to clear their running state + notify.
        for (const id of previouslyScanning) {
          const now = libs.find(l => l.id === id);
          if (!now || !now.isScanning) {
            this.clearRunningScan(id);
            this.snackBar.open('Scan finished', 'Close', { duration: 3000 });
          }
        }
        this.syncPolling();
      },
    });
  }

  private backfillScanRunId(libraryId: string): void {
    this.api.getScanHistory(libraryId).subscribe({
      next: (runs) => {
        const running = runs.find(r => r.status === 'running');
        if (running) {
          this.runningScans.update(m => new Map(m).set(libraryId, running.id));
        }
      },
    });
  }

  /** Starts the poll loop if any library is scanning; stops it once none are. */
  private syncPolling(): void {
    const anyScanning = this.libraries().some(l => l.isScanning);
    if (anyScanning && this.pollTimer === null) {
      this.pollTimer = setInterval(() => this.refreshLibraries(), 2500);
    } else if (!anyScanning) {
      this.stopPolling();
    }
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private clearRunningScan(libraryId: string): void {
    this.runningScans.update(m => {
      const next = new Map(m);
      next.delete(libraryId);
      return next;
    });
    this.cancelling.update(s => {
      const next = new Set(s);
      next.delete(libraryId);
      return next;
    });
  }

  private loadUsers(): void {
    this.api.listUsers().subscribe({
      next: (users) => {
        this.users.set(users);
        this.loadingUsers.set(false);
      },
      error: () => this.loadingUsers.set(false),
    });
  }

  registerLibrary(): void {
    const request: RegisterLibraryRequest = {
      displayName: this.newLibName(),
      rootPath: this.newLibPath(),
    };
    this.api.registerLibrary(request).subscribe({
      next: (lib) => {
        this.libraries.update(libs => [...libs, lib]);
        this.newLibName.set('');
        this.newLibPath.set('');
        this.snackBar.open(`Library "${lib.name}" registered`, 'Close', { duration: 3000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  /** Set (or clear, when mode is null) the library's global default reading direction. */
  setLibraryDirection(lib: LibraryDto, mode: ReaderMode | null): void {
    const call = mode
      ? this.api.setLibraryReaderDefault(lib.id, mode)
      : this.api.clearLibraryReaderDefault(lib.id);
    call.subscribe({
      next: (updated) => {
        this.libraries.update(libs => libs.map(l => l.id === lib.id ? updated : l));
        this.snackBar.open(`Reading direction updated for "${lib.name}"`, 'Close', { duration: 2500 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 }),
    });
  }

  toggleBrowser(): void {
    const opening = !this.browserOpen();
    this.browserOpen.set(opening);
    if (opening && this.listing() === null) {
      // Start at the current typed path if any, else the browse root.
      this.load(this.newLibPath() || undefined);
    }
  }

  browseInto(path: string): void {
    this.load(path);
  }

  browseUp(): void {
    const parent = this.listing()?.parent;
    if (parent) this.load(parent);
  }

  useFolder(path: string): void {
    this.newLibPath.set(path);
    this.browserOpen.set(false);
  }

  useCurrentFolder(): void {
    const current = this.listing()?.current;
    if (current) this.useFolder(current);
  }

  private load(path?: string): void {
    this.browseLoading.set(true);
    this.api.browseLibraryPaths(path).subscribe({
      next: (listing) => {
        this.listing.set(listing);
        this.browseLoading.set(false);
      },
      error: (err) => {
        this.browseLoading.set(false);
        this.snackBar.open(`Browse failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  triggerScan(libraryId: string): void {
    this.api.triggerScan(libraryId).subscribe({
      next: (res) => {
        this.runningScans.update(m => new Map(m).set(libraryId, res.scanRunId));
        // Optimistically flip the row to scanning so the chip appears immediately;
        // the poll loop keeps it accurate from here.
        this.libraries.update(libs =>
          libs.map(l => l.id === libraryId ? { ...l, isScanning: true } : l));
        this.snackBar.open('Scan started', 'Close', { duration: 3000 });
        this.syncPolling();
      },
      error: (err) => this.snackBar.open(`Scan failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  /**
   * Trigger a scan for every registered library at once (1.8.0). Libraries
   * already scanning are skipped server-side; surface the started/skipped
   * counts, then refresh the rows so the scanning chips reflect server state.
   * Scan-run ids are backfilled per newly-scanning library so Cancel works.
   */
  scanAll(): void {
    if (this.scanAllBusy() || this.libraries().length === 0) return;
    this.scanAllBusy.set(true);
    this.api.scanAllLibraries().subscribe({
      next: (r) => {
        this.scanAllBusy.set(false);
        const skipped = r.skippedCount;
        const started = r.startedCount;
        const msg = started > 0
          ? `Started ${started} scan${started === 1 ? '' : 's'}${skipped > 0 ? `, skipped ${skipped} already running` : ''}.`
          : `All ${skipped} librar${skipped === 1 ? 'y is' : 'ies are'} already scanning.`;
        this.snackBar.open(msg, 'Close', { duration: 4000 });
        // Refresh rows (without re-running YAC detection) and backfill scan-run
        // ids for any library that is now scanning but not yet tracked, so the
        // Cancel control works immediately. The poll loop keeps state accurate.
        this.api.getAllLibraries().subscribe({
          next: (libs) => {
            this.libraries.set(libs);
            for (const lib of libs) {
              if (lib.isScanning && !this.runningScans().get(lib.id)) {
                this.backfillScanRunId(lib.id);
              }
            }
            this.syncPolling();
          },
        });
      },
      error: (err) => {
        this.scanAllBusy.set(false);
        this.snackBar.open(`Scan-all failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  cancelScan(libraryId: string): void {
    const runId = this.runningScans().get(libraryId);
    if (!runId) return;
    this.cancelling.update(s => new Set(s).add(libraryId));
    this.api.cancelScan(runId).subscribe({
      next: () => {
        this.snackBar.open('Cancelling scan…', 'Close', { duration: 3000 });
        // The poll loop will observe isScanning flip to false and clear state.
      },
      error: (err) => {
        this.cancelling.update(s => {
          const next = new Set(s);
          next.delete(libraryId);
          return next;
        });
        this.snackBar.open(`Cancel failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  // --- User grants (D39) ---

  toggleGrants(user: AdminUserDto): void {
    if (this.grantsOpenUserId() === user.id) {
      this.grantsOpenUserId.set(null);
      return;
    }
    this.grantsOpenUserId.set(user.id);
    this.grantedLibIds.set(new Set());
    if (user.isAdmin) return; // Admins access all; no grants to load.

    this.grantsLoading.set(true);
    this.api.getUserGrants(user.id).subscribe({
      next: (grants) => {
        this.grantedLibIds.set(new Set(grants.libraryIds));
        this.grantsLoading.set(false);
      },
      error: (err) => {
        this.grantsLoading.set(false);
        this.snackBar.open(`Failed to load access: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  setGrant(userId: string, libraryId: string, granted: boolean): void {
    this.grantBusy.update(s => new Set(s).add(libraryId));
    const call = granted
      ? this.api.grantAccess(userId, libraryId)
      : this.api.revokeAccess(userId, libraryId);
    call.subscribe({
      next: () => {
        this.grantedLibIds.update(s => {
          const next = new Set(s);
          if (granted) next.add(libraryId); else next.delete(libraryId);
          return next;
        });
        this.clearGrantBusy(libraryId);
      },
      error: (err) => {
        this.clearGrantBusy(libraryId);
        // Force a checkbox re-render at the previous state by cloning the set.
        this.grantedLibIds.update(s => new Set(s));
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  private clearGrantBusy(libraryId: string): void {
    this.grantBusy.update(s => {
      const next = new Set(s);
      next.delete(libraryId);
      return next;
    });
  }

  createUser(): void {
    const password = this.newUserPassword() || undefined;
    const request: CreateUserRequest = {
      username: this.newUsername(),
      password,
      isAdmin: this.newUserIsAdmin(),
    };
    this.activationUrl.set(null);
    this.api.createUser(request).subscribe({
      next: (res) => {
        this.users.update(users => [...users, res.user]);
        this.newUsername.set('');
        this.newUserPassword.set('');
        this.newUserIsAdmin.set(false);
        if (res.activationUrl) {
          this.activationUrl.set(res.activationUrl);
          this.snackBar.open(`User "${res.user.username}" created — copy the activation link below`, 'Close', { duration: 8000 });
        } else {
          this.snackBar.open(`User "${res.user.username}" created with password`, 'Close', { duration: 3000 });
        }
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  copyActivationUrl(): void {
    const url = this.activationUrl();
    if (url) {
      navigator.clipboard.writeText(url).then(
        () => this.snackBar.open('Activation link copied', 'Close', { duration: 2000 }),
        () => this.snackBar.open('Copy failed — select and copy manually', 'Close', { duration: 3000 }),
      );
    }
  }

  resetPassword(userId: string): void {
    this.api.resetUserPassword(userId).subscribe({
      next: (res) => {
        this.snackBar.open(`Temporary password: ${res.temporaryPassword}`, 'Close', { duration: 10000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  openDeleteUser(user: AdminUserDto): void {
    this.deleteUserPanelId.set(user.id);
  }

  confirmDeleteUser(user: AdminUserDto): void {
    this.userActionBusy.update((s) => new Set(s).add(user.id));
    this.api.deleteUser(user.id).subscribe({
      next: () => {
        this.userActionBusy.update((s) => {
          const next = new Set(s);
          next.delete(user.id);
          return next;
        });
        this.deleteUserPanelId.set(null);
        this.users.update((users) => users.filter((u) => u.id !== user.id));
        this.snackBar.open(`Deleted "${user.username}".`, 'Close', { duration: 4000 });
      },
      error: (err) => {
        this.userActionBusy.update((s) => {
          const next = new Set(s);
          next.delete(user.id);
          return next;
        });
        const msg = err?.error === 'last_admin'
          ? 'Cannot delete the last active admin.'
          : `Delete failed: ${err.message}`;
        this.snackBar.open(msg, 'Close', { duration: 5000 });
      },
    });
  }

  reissueActivationLink(user: AdminUserDto): void {
    this.userActionBusy.update((s) => new Set(s).add(user.id));
    this.activationUrl.set(null);
    this.api.reissueActivation(user.id).subscribe({
      next: (res) => {
        this.userActionBusy.update((s) => {
          const next = new Set(s);
          next.delete(user.id);
          return next;
        });
        this.activationUrl.set(res.activationUrl);
        this.snackBar.open(`New activation link for "${user.username}" — the previous link no longer works`, 'Close', { duration: 8000 });
      },
      error: (err) => {
        this.userActionBusy.update((s) => {
          const next = new Set(s);
          next.delete(user.id);
          return next;
        });
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  // --- Rotating database backups (1.2.0) ---

  /** Reloads the Backups status + snapshot list (after a backup settings change). */
  refreshBackups(): void { this.loadBackupStatus(); this.loadBackupFiles(); }

  private loadBackupStatus(): void {
    this.api.getRotatingBackupStatus().subscribe({
      next: (status) => {
        this.backupStatus.set(status);
        this.backupLoading.set(false);
      },
      error: () => this.backupLoading.set(false),
    });
  }

  /** Humanized schedule label, e.g. "24h" or "90 min". */
  intervalLabel(): string {
    const hours = this.backupStatus()?.intervalHours ?? 24;
    return hours >= 1
      ? `${Math.round(hours)} h`
      : `${Math.max(1, Math.round(hours * 60))} min`;
  }

  runBackupNow(): void {
    this.backupBusy.set(true);
    this.api.runRotatingBackupNow().subscribe({
      next: (status) => {
        this.backupStatus.set(status);
        this.backupBusy.set(false);
        this.snackBar.open('Database backup created', 'Close', { duration: 3000 });
        // A fresh snapshot is now restorable — refresh the picker.
        this.loadBackupFiles();
      },
      error: (err) => {
        this.backupBusy.set(false);
        this.snackBar.open(`Backup failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  // --- Backup restore (1.18.0) ---

  private loadBackupFiles(): void {
    this.backupFilesLoading.set(true);
    this.api.listRotatingBackups().subscribe({
      next: (list) => {
        this.backupFiles.set(list.files);
        this.backupFilesLoading.set(false);
      },
      error: () => this.backupFilesLoading.set(false),
    });
  }

  /** Human-readable byte size, e.g. "1.4 MB". */
  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    const units = ['KB', 'MB', 'GB'];
    let value = bytes / 1024;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${value.toFixed(1)} ${units[unit]}`;
  }

  restoreFromSnapshot(fileName: string): void {
    this.restoreBusy.set(true);
    this.restoreMessage.set(null);
    this.api.restoreFromBackup(fileName).subscribe({
      next: (res) => {
        this.restoreBusy.set(false);
        this.restoreMessage.set(res.message ?? 'Restore staged. Restart the server to complete it.');
        this.snackBar.open('Restore staged — restart to apply', 'Close', { duration: 5000 });
      },
      error: (err) => {
        this.restoreBusy.set(false);
        this.snackBar.open(`Restore failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  onRestoreFileSelected(input: HTMLInputElement): void {
    const file = input.files?.[0];
    if (!file) return;
    this.restoreBusy.set(true);
    this.restoreMessage.set(null);
    this.api.restoreFromUpload(file).subscribe({
      next: (res) => {
        this.restoreBusy.set(false);
        input.value = '';
        this.restoreMessage.set(res.message ?? 'Restore staged. Restart the server to complete it.');
        this.snackBar.open('Restore staged — restart to apply', 'Close', { duration: 5000 });
      },
      error: (err) => {
        this.restoreBusy.set(false);
        input.value = '';
        this.snackBar.open(`Restore failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  // --- Audit trail (1.18.0) ---

  private loadAuditTrail(): void {
    this.auditLoading.set(true);
    this.api.getAuditTrail(this.auditPage(), this.auditPageSize).subscribe({
      next: (result) => {
        this.auditEvents.set(result.items);
        this.auditTotal.set(result.totalCount);
        this.auditLoading.set(false);
      },
      error: () => this.auditLoading.set(false),
    });
  }

  auditNextPage(): void {
    if (this.auditPage() >= this.auditTotalPages()) return;
    this.auditPage.update((p) => p + 1);
    this.loadAuditTrail();
  }

  auditPrevPage(): void {
    if (this.auditPage() <= 1) return;
    this.auditPage.update((p) => p - 1);
    this.loadAuditTrail();
  }
}
