import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../../core/auth/auth.service';
import { CreatorAdminService } from './creator-admin/creator-admin.service';
import { InfoApiService } from './info-api.service';
import { InfoTagsComponent } from './info-tags.component';
import { InfoPanelDto } from './info-types';
import { bucket, chip, panel } from './info.testing';

@Component({
  standalone: true,
  imports: [InfoTagsComponent],
  template: `<app-info-tags [nodeId]="nodeId()" (loaded)="loaded.push($event)" />`,
})
class HostComponent {
  readonly nodeId = signal('n1');
  readonly loaded: (InfoPanelDto | null)[] = [];
}

/** The Info panel's Tags block (1.45.0, D1 2.4 / 2.6). */
describe('InfoTagsComponent', () => {
  const dialogRef = { close: vi.fn() };

  function render(answer: InfoPanelDto | 'error', opts: { admin?: boolean; inDialog?: boolean; full?: InfoPanelDto['buckets'][number] } = {}) {
    dialogRef.close.mockClear();
    const api = {
      getInfo: vi.fn(() => (answer === 'error' ? throwError(() => ({ status: 404 })) : of(answer))),
      getInfoBucket: vi.fn(() => of(opts.full ?? bucket())),
    };
    const creatorAdmin = { rename: vi.fn(() => Promise.resolve(null)), merge: vi.fn(() => Promise.resolve(null)), split: vi.fn(() => Promise.resolve(null)) };
    const providers: unknown[] = [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: InfoApiService, useValue: api },
      { provide: AuthService, useValue: { isAdmin: () => !!opts.admin } },
      { provide: CreatorAdminService, useValue: creatorAdmin },
    ];
    if (opts.inDialog) providers.push({ provide: MatDialogRef, useValue: dialogRef });
    TestBed.configureTestingModule({ imports: [HostComponent], providers: providers as never[] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const chips = () => Array.from(el.querySelectorAll<HTMLAnchorElement>('[data-testid="info-chip"]'));
    return { fixture, el, api, creatorAdmin, chips };
  }

  it('shows one row per bucket in the owner\'s order with its label, whatever order the server sent', () => {
    const { el } = render(panel({
      buckets: [
        bucket({ bucket: 'Genres', chips: [chip({ valueId: 'fv_g', display: 'Comedy' })] }),
        bucket({ bucket: 'About', chips: [chip({ valueId: 'fv_s', display: 'Starlight Academy' })] }),
        bucket({ bucket: 'Creators', chips: [chip()] }),
        bucket({ bucket: 'Year', chips: [chip({ valueId: 'fv_y', display: '2004' })] }),
      ],
    }));
    const rows = Array.from(el.querySelectorAll('[data-testid="info-bucket"]'));
    expect(rows.map((r) => r.getAttribute('data-bucket'))).toEqual(['Creators', 'Year', 'Genres', 'About']);
    expect(rows.map((r) => r.querySelector('.label')!.textContent!.trim())).toEqual(['Creators', 'Year started', 'Genres', 'About']);
    // About last, as "Fan works of <series>" (owner Q8).
    expect(rows[3].querySelector('.name')!.textContent).toBe('Fan works of Starlight Academy');
  });

  it('shows a count only when it is 2 or more, in its own span; a count of 1 stays quiet but the chip still links', () => {
    const { chips } = render(panel({
      buckets: [bucket({ chips: [chip({ valueId: 'fv_a', display: 'Two Works', count: 2 }), chip({ valueId: 'fv_b', display: 'One Work', count: 1 })] })],
    }));
    const [two, one] = chips();
    expect(two.querySelector('[data-testid="info-chip-count"]')!.textContent).toBe('(2)');
    expect(one.querySelector('[data-testid="info-chip-count"]')).toBeNull();
    expect(one.getAttribute('href')).toBe('/search?f=creator:fv_b');
    expect(two.getAttribute('aria-label')).toBe('Two Works, 2 works');
  });

  it('counts "N here" on a container, with "More" for the rest of a bucket', () => {
    const full = bucket({ bucket: 'Genres', chips: Array.from({ length: 14 }, (_, i) => chip({ valueId: `fv_${i}`, display: `Genre ${i}` })) });
    const { el, api, chips, fixture } = render(panel({
      container: true,
      works: 40,
      buckets: [bucket({ bucket: 'Genres', chips: [chip({ valueId: 'fv_g', display: 'Comedy', count: 12 })], more: 13 })],
    }), { full });
    expect(chips()[0].querySelector('.count')!.textContent).toBe('12 here');
    const more = el.querySelector<HTMLButtonElement>('[data-testid="info-more"]')!;
    expect(more.textContent).toContain('+13');

    more.click();
    fixture.detectChanges();
    expect(api.getInfoBucket).toHaveBeenCalledWith('n1', 'Genres');
    expect(chips().length).toBe(14);
    expect(el.querySelector('[data-testid="info-more"]')).toBeNull();
  });

  it('marks a conflicting value amber with the Conflict wording, and shows the role of a creator', () => {
    const { chips } = render(panel({
      buckets: [bucket({ chips: [
        chip({ valueId: 'fv_d', display: 'Declared Artist', conflict: true, sources: ['Declared'], role: 'Artist' }),
        chip({ valueId: 'fv_w', display: 'Web Artist', role: 'StoryAndArt' }),
      ] })],
    }));
    const [declared, web] = chips();
    expect(declared.parentElement!.classList).toContain('conflict');
    expect(declared.getAttribute('aria-label')).toBe('Declared Artist, Art, Conflict: declared here, the web record says otherwise');
    expect(web.parentElement!.classList).not.toContain('conflict');
    expect(web.querySelector('.role')!.textContent).toBe('Story & art');
  });

  it('closes the overlay it is in when a chip is followed', () => {
    const { chips } = render(panel(), { inDialog: true });
    chips()[0].click();
    expect(dialogRef.close).toHaveBeenCalled();
  });

  it('gives admins a menu on creator chips (Rename, Merge into..., Split...) and readers none', async () => {
    const answer = panel({
      buckets: [
        bucket({ chips: [chip({ creatorId: 'cr_1' })] }),
        bucket({ bucket: 'Genres', chips: [chip({ valueId: 'fv_g', display: 'Comedy' })] }),
      ],
    });
    const reader = render(answer);
    expect(reader.el.querySelector('[data-testid="info-chip-menu"]')).toBeNull();
    TestBed.resetTestingModule();

    const admin = render(answer, { admin: true });
    const menus = admin.el.querySelectorAll<HTMLButtonElement>('[data-testid="info-chip-menu"]');
    expect(menus.length).toBe(1);
    expect(menus[0].getAttribute('aria-label')).toBe('Creator actions for Synthetic Author');
    menus[0].click();
    admin.fixture.detectChanges();
    const items = Array.from(document.querySelectorAll('.mat-mdc-menu-panel [mat-menu-item]')).map((b) => b.textContent!.replace(/\s+/g, ' ').trim());
    expect(items).toEqual(['edit Rename…', 'call_merge Merge into…', 'call_split Split…']);
    (document.querySelector('[data-testid="creator-merge"]') as HTMLButtonElement).click();
    await admin.fixture.whenStable();
    expect(admin.creatorAdmin.merge).toHaveBeenCalledWith('cr_1', 'Synthetic Author');
  });

  it('reloads the tags after an admin change was saved', async () => {
    const { fixture, api, creatorAdmin, el } = render(panel({ buckets: [bucket({ chips: [chip({ creatorId: 'cr_1' })] })] }), { admin: true });
    creatorAdmin.rename.mockResolvedValueOnce({ id: 'cr_1' } as never);
    el.querySelector<HTMLButtonElement>('[data-testid="info-chip-menu"]')!.click();
    fixture.detectChanges();
    (document.querySelector('[data-testid="creator-rename"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.getInfo).toHaveBeenCalledTimes(2);
  });

  it('shows nothing and reports null when nothing is indexed or the server cannot answer', () => {
    const empty = render(panel({ works: 0, buckets: [] }));
    expect(empty.el.querySelector('[data-testid="info-tags"]')).toBeNull();
    expect(empty.fixture.componentInstance.loaded).toEqual([null]);
    TestBed.resetTestingModule();
    const failed = render('error');
    expect(failed.el.querySelector('[data-testid="info-tags"]')).toBeNull();
    expect(failed.fixture.componentInstance.loaded).toEqual([null]);
  });

  it('loads again for another node, and binds names as text', () => {
    const { fixture, api, chips } = render(panel({ buckets: [bucket({ chips: [chip({ display: '<b>Not markup</b>' })] })] }));
    expect(chips()[0].querySelector('.name')!.textContent).toBe('<b>Not markup</b>');
    expect(chips()[0].querySelector('b')).toBeNull();
    fixture.componentInstance.nodeId.set('n2');
    fixture.detectChanges();
    expect(api.getInfo).toHaveBeenLastCalledWith('n2');
  });
});
