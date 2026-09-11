import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { FolderRollupBadgeComponent, folderRollupView } from './folder-rollup-badge.component';
import { FolderReadRollup } from '../../core/api/api-types';

/** Host that drives the badge through its signal input, as the browse list will. */
@Component({
  standalone: true,
  imports: [FolderRollupBadgeComponent],
  template: `<div class="cover"><app-folder-rollup-badge [rollup]="rollup()" /></div>`,
})
class HostComponent {
  readonly rollup = signal<FolderReadRollup | null>(null);
}

/**
 * Folder read rollup badge (1.6.0): a standalone presentational component that
 * renders the derived per-folder `readRollup` from the browse response.
 *  - Read    -> green "Read" badge
 *  - Reading -> purple "Reading" badge
 *  - Unread / null -> no badge (unread archive cards show nothing either)
 */
describe('FolderRollupBadgeComponent', () => {
  function create(rollup: FolderReadRollup | null) {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.rollup.set(rollup);
    fixture.detectChanges();
    return fixture;
  }

  function badge(fixture: ReturnType<typeof create>): HTMLElement | null {
    return fixture.nativeElement.querySelector('app-folder-rollup-badge .badge');
  }

  it('renders a green Read badge when every descendant is read', () => {
    const fixture = create('Read');
    const el = badge(fixture);
    expect(el).not.toBeNull();
    expect(el!.classList.contains('read')).toBe(true);
    expect(el!.classList.contains('reading')).toBe(false);
    expect(el!.textContent).toContain('Read');
    expect(el!.getAttribute('aria-label')).toBe('All items read');
  });

  it('renders a Reading badge when the folder is partially read', () => {
    const fixture = create('Reading');
    const el = badge(fixture);
    expect(el).not.toBeNull();
    expect(el!.classList.contains('reading')).toBe(true);
    expect(el!.classList.contains('read')).toBe(false);
    expect(el!.textContent!.trim()).toBe('Reading');
    expect(el!.getAttribute('aria-label')).toBe('Partially read');
  });

  it('renders nothing for Unread (matches the unread archive-card convention)', () => {
    expect(badge(create('Unread'))).toBeNull();
  });

  it('renders nothing for null (folder has nothing to roll up)', () => {
    expect(badge(create(null))).toBeNull();
  });

  it('updates in place when the input changes (e.g. after a bulk mark-read)', () => {
    const fixture = create('Unread');
    expect(badge(fixture)).toBeNull();

    fixture.componentInstance.rollup.set('Read');
    fixture.detectChanges();
    expect(badge(fixture)!.classList.contains('read')).toBe(true);

    fixture.componentInstance.rollup.set('Reading');
    fixture.detectChanges();
    expect(badge(fixture)!.classList.contains('reading')).toBe(true);
  });
});

describe('folderRollupView', () => {
  it('maps Read and Reading to distinct badge kinds and everything else to null', () => {
    expect(folderRollupView('Read')?.kind).toBe('read');
    expect(folderRollupView('Reading')?.kind).toBe('reading');
    expect(folderRollupView('Unread')).toBeNull();
    expect(folderRollupView(null)).toBeNull();
  });
});
