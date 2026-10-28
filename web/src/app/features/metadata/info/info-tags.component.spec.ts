import { ChangeDetectorRef, Component, signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { MatDialogRef } from '@angular/material/dialog';
import { Subject, of, throwError } from 'rxjs';

import { AuthService } from '../../../core/auth/auth.service';
import { CreatorAdminService } from './creator-admin/creator-admin.service';
import { InfoApiService } from './info-api.service';
import { InfoTagsComponent } from './info-tags.component';
import { FacetBucketPageDto, InfoPanelDto } from './info-types';
import { bucket, chip, emptyPanel, panel } from './info.testing';

@Component({ standalone: true, template: '' })
class SearchStubComponent {}

@Component({
  standalone: true,
  imports: [InfoTagsComponent],
  template: `<app-info-tags [nodeId]="nodeId()" [libraryId]="libraryId()" (loaded)="loaded.push($event)" />`,
})
class HostComponent {
  readonly nodeId = signal<string | null>('n1');
  readonly libraryId = signal<string | null>(null);
  readonly loaded: (InfoPanelDto | null)[] = [];
}

/** The Info panel's Tags block (1.45.0, D1 2.4 / 2.6), on lane Q's `InfoPanelDto`. */
describe('InfoTagsComponent', () => {
  const dialogRef = { close: vi.fn() };

  function render(
    answer: InfoPanelDto | 'error',
    opts: { admin?: boolean; inDialog?: boolean; pages?: (FacetBucketPageDto | 'error' | Subject<FacetBucketPageDto>)[] } = {},
  ) {
    dialogRef.close.mockClear();
    const reply = () => (answer === 'error' ? throwError(() => ({ status: 404 })) : of(answer));
    const pages = [...(opts.pages ?? [])];
    const api = {
      getInfo: vi.fn(reply),
      getLibraryInfo: vi.fn(reply),
      getBucket: vi.fn(() => {
        const next = pages.shift();
        if (next === undefined || next === 'error') return throwError(() => ({ status: 500 }));
        return next instanceof Subject ? next.asObservable() : of(next);
      }),
    };
    const creatorAdmin = { rename: vi.fn(() => Promise.resolve(null)), merge: vi.fn(() => Promise.resolve(null)), split: vi.fn(() => Promise.resolve(null)) };
    const providers: unknown[] = [
      provideNoopAnimations(),
      provideRouter([{ path: 'search', component: SearchStubComponent }]),
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

  // 1.49.0 (lane T, owner decision 9 a): over the search page a chip ADDS its tag; the text, the read filter and the other tags stay.
  it('adds the chip\'s tag to the search it is opened over, keeping q, read and the other tags', async () => {
    const { chips, fixture } = render(panel({ buckets: [bucket({ bucket: 'Genres', chips: [chip({ id: 'fv_g', display: 'Comedy', filter: 'genre:fv_g' })] })] }));
    expect(chips()[0].getAttribute('href')).toBe('/search?f=genre:fv_g'); // not on the search page: just the tag
    await TestBed.inject(Router).navigateByUrl('/search?q=pluto&read=unread&f=creator:fv_a&f=-genre:fv_g');
    fixture.debugElement.query(By.directive(InfoTagsComponent)).injector.get(ChangeDetectorRef).markForCheck();
    fixture.detectChanges();
    const href = decodeURIComponent(chips()[0].getAttribute('href')!);
    expect(href.startsWith('/search?')).toBe(true);
    const params = new URLSearchParams(href.slice('/search?'.length));
    expect(params.get('q')).toBe('pluto');
    expect(params.get('read')).toBe('unread');
    // The excluded copy of the same tag is replaced by the included one; the creator tag stays.
    expect(params.getAll('f')).toEqual(['creator:fv_a', 'genre:fv_g']);
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
    expect(admin.creatorAdmin.merge).toHaveBeenCalledWith(expect.objectContaining({ id: 'fv_a', display: 'Synthetic Author' }));
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
  // ---- 1.47.0 (lane P) ----

  it('loads a library root\'s tags when given a library instead of a node', () => {
    const { fixture, api } = render(panel({ mode: 'Container', nodeId: 'L1', worksBelow: 3 }));
    fixture.componentInstance.nodeId.set(null);
    fixture.componentInstance.libraryId.set('L1');
    fixture.detectChanges();
    expect(api.getLibraryInfo).toHaveBeenCalledWith('L1');
    expect(api.getInfo).toHaveBeenCalledTimes(1); // only the first render's node
  });

  it('"+N more" is a button that adds the rest of the bucket in place, from the shown chips on', () => {
    const shown = Array.from({ length: 12 }, (_, i) => chip({ id: `fv_${i}`, display: `Genre ${i}`, here: 2, filter: `genre:fv_${i}` }));
    const rest = [chip({ id: 'fv_12', display: 'Genre 12', here: 1, filter: 'genre:fv_12' }), chip({ id: 'fv_13', display: 'Genre 13', here: 1, filter: 'genre:fv_13' })];
    const { fixture, el, api, chips } = render(panel({ mode: 'Container', buckets: [bucket({ bucket: 'Genres', chips: shown, more: 2 })] }), {
      pages: [{ bucket: 'Genres', chips: rest, offset: 12, total: 14, nextOffset: null }],
    });
    const more = el.querySelector<HTMLButtonElement>('[data-testid="info-more"]')!;
    expect(more.tagName).toBe('BUTTON');
    expect(more.getAttribute('aria-label')).toBe('Show 2 more in Genres');
    more.click();
    fixture.detectChanges();
    expect(api.getBucket).toHaveBeenCalledWith({ nodeId: 'n1' }, 'Genres', 12);
    expect(chips().map((c) => c.getAttribute('data-value'))).toEqual([...shown, ...rest].map((c) => c.id));
    expect(chips()[13].getAttribute('href')).toBe('/search?f=genre:fv_13');
    expect(el.querySelector('[data-testid="info-more"]')).toBeNull();
  });

  it('"+N more" keeps the button while more remain, asks the library route for a library root, and survives a failure', () => {
    const shown = [chip({ id: 'fv_a' }), chip({ id: 'fv_b', display: 'Second' })];
    const pending = new Subject<FacetBucketPageDto>();
    const { fixture, el, api, chips } = render(panel({ mode: 'Container', nodeId: 'L1', buckets: [bucket({ chips: shown, more: 3 })] }), {
      pages: ['error', pending],
    });
    fixture.componentInstance.nodeId.set(null);
    fixture.componentInstance.libraryId.set('L1');
    fixture.detectChanges();
    const button = () => el.querySelector<HTMLButtonElement>('[data-testid="info-more"]');

    button()!.click(); // the first request fails: nothing changes, the button is usable again
    fixture.detectChanges();
    expect(chips().length).toBe(2);
    expect(button()!.disabled).toBe(false);

    button()!.click();
    fixture.detectChanges();
    expect(api.getBucket).toHaveBeenLastCalledWith({ libraryId: 'L1' }, 'Creators', 2);
    expect(button()!.disabled).toBe(true); // while the page loads
    pending.next({ bucket: 'Creators', chips: [chip({ id: 'fv_c', display: 'Third' })], offset: 2, total: 5, nextOffset: 3 });
    pending.complete();
    fixture.detectChanges();
    expect(chips().length).toBe(3);
    expect(button()!.textContent!.trim()).toBe('+2 more');
    expect(button()!.disabled).toBe(false);
  });
});
