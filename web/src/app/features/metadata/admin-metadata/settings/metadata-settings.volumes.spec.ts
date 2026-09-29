import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { MetadataSettingsDto } from '../../../../core/api/api-types';
import { settings } from '../metadata-admin.testing';
import { MetadataSettingsComponent } from './metadata-settings.component';

/** The Volumes view controls of /admin/metadata (1.29.0): the global default and one library's override. */
describe('MetadataSettingsComponent Volumes view (1.29.0)', () => {
  const SETTINGS = '/api/v1/admin/metadata/settings';
  const LIBRARY = '/api/v1/admin/metadata/libraries/lib1';
  let http: HttpTestingController;

  function create(initial: MetadataSettingsDto = settings()) {
    TestBed.configureTestingModule({
      imports: [MetadataSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(MetadataSettingsComponent);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(initial);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => http?.verify());

  it('shows the global default as on unless the server says otherwise', () => {
    const on = create();
    expect(on.el.querySelector('[data-testid="md-volumes-default"]')).not.toBeNull();
    expect(on.c.settings()?.virtualVolumesEnabled).toBeUndefined();
    expect(on.el.querySelector('[data-testid="md-volumes-default"] input')!.hasAttribute('checked') || (on.el.querySelector('[data-testid="md-volumes-default"] input') as HTMLInputElement).checked).toBe(true);
  });

  it('turning the default off is ONE settings PUT with that field only', () => {
    const { c } = create(settings({ virtualVolumesEnabled: true }));

    c.setVirtualVolumes(false);

    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ virtualVolumesEnabled: false });
    put.flush(settings({ virtualVolumesEnabled: false }));
    expect(c.settings()?.virtualVolumesEnabled).toBe(false);
  });

  it('a library override is On or Off, and "Default" resets it', () => {
    const { c } = create();
    const lib = c.settings()!.libraries[0];

    c.setVolumesView(lib, 'On');
    let put = http.expectOne({ method: 'PUT', url: LIBRARY });
    expect(put.request.body).toEqual({ virtualVolumes: 'On' });
    put.flush(settings());

    c.setVolumesView(lib, 'Off');
    put = http.expectOne({ method: 'PUT', url: LIBRARY });
    expect(put.request.body).toEqual({ virtualVolumes: 'Off' });
    put.flush(settings());

    c.setVolumesView(lib, 'default');
    put = http.expectOne({ method: 'PUT', url: LIBRARY });
    expect(put.request.body).toEqual({ resetVirtualVolumes: true });
    put.flush(settings());
  });

  it('renders each library\'s Volumes view select', () => {
    const { el } = create(settings({ libraries: [{ libraryId: 'lib1', name: 'Library One', fetchEnabled: false, showSeriesInfo: true, precedence: null, linkCount: 2, virtualVolumes: 'Off' }] }));

    expect(el.querySelector('[data-testid="md-lib-volumes"]')).not.toBeNull();
  });
});
