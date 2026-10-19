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
import { bucket, chip, emptyPanel, panel } from './info.testing';

@Component({
  standalone: true,
  imports: [InfoTagsComponent],
  template: `<app-info-tags [nodeId]="nodeId()" (loaded)="loaded.push($event)" />`,
})
class HostComponent {
  readonly nodeId = signal('n1');
  readonly loaded: (InfoPanelDto | null)[] = [];
}

/** The Info panel's Tags block (1.45.0, D1 2.4 / 2.6), on lane Q's `InfoPanelDto`. */
describe('InfoTagsComponent', () => {
  const dialogRef = { close: vi.fn() };

  function render(answer: InfoPanelDto | 'error', opts: { admin?: boolean; inDialog?: boolean } = {}) {
    dialogRef.close.mockClear();
    const api = { getInfo: vi.fn(() => (answer === 'error' ? throwError(() => ({ status: 404 })) : of(answer))) };
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
        bucket({ bucket: 'Genres', chips: [chip({ id: 'fv_g', display: 'Comedy', filter: 'genre:fv_g' })] }),
        bucket({ bucket: 'About', chips: [chip({ id: 'fv_s', display: 'Starlight Academy', filter: 'about:fv_s' })] }),
        bucket({ bucket: 'Creators', chips: [chip()] }),
        bucket({ bucket: 'Year', chips: [chip({ id: 'fv_y', display: '2004', filter: 'year:fv_y' })] }),
      ],
    }));
    const rows = Array.from(el.querySelectorAll('[data-testid="info-bucket"]'));
    expect(rows.map((r) => r.getAttribute('data-bucket'))).toEqual(['Creators', 'Year', 'Genres', 'About']);
    expect(rows.map((r) => r.querySelector('.label')!.textContent!.trim())).toEqual(['Creators', 'Year started', 'Genres', 'About']);
    // About last, as "Fan works of <series>" (owner Q8).
    expect(rows[3].querySelector('.name')!.textContent).toBe('Fan works of Starlight Academy');
  });

  it('links each chip to the search with the chip\'s own filter', () => {
    const { chips } = render(panel({ buckets: [bucket({ bucket: 'Genres', chips: [chip({ id: 'fv_g', display: 'Comedy', filter: 'genre:fv_g' })] })] }));
    expect(chips()[0].getAttribute('href')).toBe('/search?f=genre:fv_g');
  });

  it('shows a count only when it is 2 or more, in its own span; a count of 1 stays quiet but the chip still links', () => {
    const { chips } = render(panel({
      buckets: [bucket({ chips: [chip({ id: 'fv_a', display: 'Two Works', count: 2 }), chip({ id: 'fv_b', display: 'One Work' })] })],
    }));
    const [two, one] = chips();
    expect(two.querySelector('[data-testid="info-chip-count"]')!.textContent).toBe('(2)');
    expect(one.querySelector('[data-testid="info-chip-count"]')).toBeNull();
    expect(one.getAttribute('href')).toBe('/search?f=creator:fv_b');
    expect(two.getAttribute('aria-label')).toBe('Two Works, 2 works');
  });

  it('counts "N here" on a container and says how many more values a bucket has', () => {
    const { el, chips } = render(panel({
      mode: 'Container',
      worksBelow: 40,
      buckets: [bucket({ bucket: 'Genres', chips: [chip({ id: 'fv_g', display: 'Comedy', count: 30, here: 12, filter: 'genre:fv_g' })], more: 13 })],
    }));
    expect(chips()[0].querySelector('.count')!.textContent).toBe('12 here');
    expect(el.querySelector('[data-testid="info-more"]')!.textContent).toBe('+13 more');
  });

  it('marks a conflicting value amber with the Conflict wording, and shows the role of a creator', () => {
    const { chips } = render(panel({
      buckets: [bucket({ chips: [
        chip({ id: 'fv_d', display: 'Declared Artist', conflict: true, roles: 'Artist' }),
        chip({ id: 'fv_w', display: 'Web Artist', roles: 'Writer, Artist' }),
      ] })],
    }));
    const [declared, web] = chips();
    expect(declared.parentElement!.classList).toContain('conflict');
    expect(declared.getAttribute('aria-label')).toBe('Declared Artist, Art, Conflict: the folder\'s declared value and the web record disagree');
    expect(web.parentElement!.classList).not.toContain('conflict');
    expect(web.querySelector('.role')!.textContent).toBe('Story & art');
  });

  it('lists the places of a work kept in several folders', () => {
    const { el } = render(panel({
      work: {
        title: 'Series A', locationCount: 2, locations: [
          { nodeId: 'f1', libraryId: 'L1', libraryName: 'Manga', trail: [{ id: 'f1', displayName: 'Series A' }] },
          { nodeId: 'f2', libraryId: 'L2', libraryName: 'Manga 2', trail: [{ id: 'x', displayName: 'Archive' }, { id: 'f2', displayName: 'Series A 101-200' }] },
        ],
      },
    }));
    const places = el.querySelectorAll('[data-testid="info-locations"] a');
    expect(el.querySelector('[data-testid="info-locations"] .label')!.textContent).toBe('2 locations');
    expect(Array.from(places).map((a) => a.textContent)).toEqual(['Manga › Series A', 'Manga 2 › Archive › Series A 101-200']);
    expect(places[1].getAttribute('href')).toBe('/libraries/L2/browse/f2');
  });

  it('closes the overlay it is in when a chip is followed', () => {
    const { chips } = render(panel(), { inDialog: true });
    chips()[0].click();
    expect(dialogRef.close).toHaveBeenCalled();
  });

  it('gives admins a menu on creator chips (Rename, Merge into..., Split...) and readers none', async () => {
    const answer = panel({
      buckets: [
        bucket({ chips: [chip()] }),
        bucket({ bucket: 'Genres', chips: [chip({ id: 'fv_g', display: 'Comedy', filter: 'genre:fv_g' })] }),
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
    expect(admin.creatorAdmin.merge).toHaveBeenCalledWith('fv_a', 'Synthetic Author');
  });

  it('reloads the tags after an admin change was saved', async () => {
    const { fixture, api, creatorAdmin, el } = render(panel(), { admin: true });
    creatorAdmin.rename.mockResolvedValueOnce({ id: 'cr_1' } as never);
    el.querySelector<HTMLButtonElement>('[data-testid="info-chip-menu"]')!.click();
    fixture.detectChanges();
    (document.querySelector('[data-testid="creator-rename"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.getInfo).toHaveBeenCalledTimes(2);
  });

  it('shows nothing and reports null when nothing is indexed or the server cannot answer', () => {
    const empty = render(emptyPanel());
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
