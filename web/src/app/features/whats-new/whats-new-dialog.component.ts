import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

import { WhatsNewInlineDto, WhatsNewSectionDto } from '../../core/api/api-types';

/** What the dialog shows: the running version and the sections (newest first); `error` when the notes could not be loaded. */
export interface WhatsNewDialogData {
  current: string;
  sections: WhatsNewSectionDto[];
  error?: boolean;
}

/**
 * "What's new in MangaPixer <version>" (1.53.0): the release notes an admin has not seen yet, after an update - each version with its
 * date, its groups (Added, Changed, Fixed ...) as sub-headings and the bullets. Text only: every part renders as text (no HTML), bold /
 * code as `<strong>` / `<code>`, and a link only ever opens an in-app page (it closes the dialog first). One action, "Got it"; Escape
 * and a tap outside close it the same way. The card family of the app's other dialogs.
 */
@Component({
  selector: 'app-whats-new-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title class="title" data-testid="whats-new-title">What's new in MangaPixer {{ data.current }}</h2>
    <mat-dialog-content class="body" data-testid="whats-new-body">
      @if (data.error) {
        <p class="empty" data-testid="whats-new-error">The release notes could not be loaded. Try again later.</p>
      } @else if (data.sections.length === 0) {
        <p class="empty" data-testid="whats-new-empty">There are no release notes for this version.</p>
      }
      @for (section of data.sections; track section.version) {
        <section class="release" data-testid="whats-new-section" [attr.data-version]="section.version">
          @if (data.sections.length > 1) {
            <h3 class="version">{{ section.version }}@if (section.date) {<span class="date"> · {{ section.date }}</span>}</h3>
          } @else if (section.date) {
            <p class="date only" data-testid="whats-new-date">Released {{ section.date }}</p>
          }
          @for (group of section.groups; track $index) {
            <h4 class="group">{{ group.name }}</h4>
            <ul class="items">
              @for (item of group.items; track $index) {
                <li>
                  @for (part of item.parts; track $index) {
                    @if (part.link) {
                      <a class="link" [class.bold]="part.bold" [attr.href]="part.link" (click)="follow($event, part)">{{ part.text }}</a>
                    } @else if (part.code) {
                      <code [class.bold]="part.bold">{{ part.text }}</code>
                    } @else if (part.bold) {
                      <strong>{{ part.text }}</strong>
                    } @else {
                      <span>{{ part.text }}</span>
                    }
                  }
                </li>
              }
            </ul>
          }
        </section>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button type="button" class="got-it" mat-dialog-close cdkFocusInitial data-testid="whats-new-got-it">Got it</button>
    </mat-dialog-actions>
  `,
  styles: [`
    :host { display: block; color: var(--mp-text); }
    .title { font-size: 20px; font-weight: 600; line-height: 1.3; }
    .body { font-size: 14px; line-height: 1.5; color: var(--mp-text); }
    .release + .release { margin-top: 20px; padding-top: 16px; border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.08); }
    .version { margin: 0 0 4px; font-size: 16px; font-weight: 600; color: var(--mp-text-strong); }
    .date { font-weight: 400; color: var(--mp-text-muted); font-size: 13px; }
    .date.only { margin: 0 0 4px; }
    .group { margin: 14px 0 4px; font-size: 14px; font-weight: 600; color: var(--mp-text-muted); }
    .items { margin: 0; padding-left: 20px; }
    .items li { margin: 0 0 6px; overflow-wrap: anywhere; }
    code {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px;
      padding: 0 4px; border-radius: 4px; background: rgb(var(--mp-ink-rgb) / 0.08);
    }
    .link { color: var(--mp-accent); }
    .bold, strong { font-weight: 600; }
    .empty { margin: 0; color: var(--mp-text-secondary); }
    .got-it { min-height: 40px; }
  `],
})
export class WhatsNewDialogComponent {
  readonly data = inject<WhatsNewDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<WhatsNewDialogComponent>>(MatDialogRef);
  private readonly router = inject(Router);

  /** An in-app link: close the dialog (that marks it seen), then open the page in the app. */
  follow(event: MouseEvent, part: WhatsNewInlineDto): void {
    if (!part.link || !part.link.startsWith('/') || part.link.startsWith('//')) return;
    event.preventDefault();
    this.dialogRef.close();
    void this.router.navigateByUrl(part.link);
  }
}
