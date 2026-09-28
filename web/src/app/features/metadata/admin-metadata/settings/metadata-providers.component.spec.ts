import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { MetadataSettingsDto } from '../../../../core/api/api-types';
import { settings } from '../metadata-admin.testing';
import { MetadataProvidersComponent } from './metadata-providers.component';

const SETTINGS = '/api/v1/admin/metadata/settings';

/** The provider allowlist chips (1.28.0): remove sends the full removed set; removed sites can be added back. */
describe('MetadataProvidersComponent', () => {
  let http: HttpTestingController;

  function create(initial: MetadataSettingsDto) {
    TestBed.configureTestingModule({
      imports: [MetadataProvidersComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(MetadataProvidersComponent);
    fixture.componentRef.setInput('settings', initial);
    fixture.detectChanges();
    http = TestBed.inject(HttpTestingController);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, changed };
  }

  afterEach(() => http?.verify());

  it('shows each allowed site with its hosts and what is sent', () => {
    const { el } = create(settings());
    const chips = el.querySelectorAll('[data-testid="md-provider-chip"]');
    expect(chips).toHaveLength(2);
    expect(chips[1].textContent).toContain('graphql.anilist.co');
    expect(chips[1].textContent).toContain('A linked title.');
    expect(el.querySelector('[data-testid="md-providers-removed"]')).toBeNull();
  });

  it('removing a site sends the removed set and emits the saved settings', () => {
    const { el, changed } = create(settings());
    (el.querySelectorAll('[data-testid="md-provider-remove"]')[1] as HTMLButtonElement).click();
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ removedProviders: ['anilist'] });
    const saved = settings();
    saved.providers = saved.providers!.map((p) => ({ ...p, allowed: p.id !== 'anilist' }));
    put.flush(saved);
    expect(changed).toHaveBeenCalledWith(saved);
  });

  it('lists removed sites with Add back, and warns when MangaUpdates is out', () => {
    const initial = settings();
    initial.providers = initial.providers!.map((p) => ({ ...p, allowed: false }));
    const { el } = create(initial);
    expect(el.querySelector('[data-testid="md-providers-none"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="md-providers-mu-note"]')).not.toBeNull();
    (el.querySelectorAll('[data-testid="md-provider-add"]')[0] as HTMLButtonElement).click();
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ removedProviders: ['anilist'] });
  });
});
