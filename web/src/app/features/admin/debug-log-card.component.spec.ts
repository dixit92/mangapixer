import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatSelect, MatSelectChange } from '@angular/material/select';

import { DebugLogCardComponent, INHERIT } from './debug-log-card.component';
import { routes } from '../../app.routes';

/**
 * Debug log UI (1.17.0): per-category runtime log-level control. The API is
 * mocked at the HTTP layer so the real ApiService request shape (partial PUT
 * with only `categories`) is what gets asserted.
 */
describe('DebugLogCardComponent', () => {
  const URL = '/api/v1/operations/logging';
  let httpMock: HttpTestingController;

  const state = (overrides: Record<string, string> = {}, level = 'Information') => ({
    level,
    categories: ['Scanning', 'Media', 'Reading'].map((name) => ({
      name,
      level: overrides[name] ?? level,
      inherited: !(name in overrides),
    })),
  });

  const change = (value: string, source?: Partial<MatSelect>) =>
    ({ value, source } as MatSelectChange);

  function create() {
    TestBed.configureTestingModule({
      imports: [DebugLogCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(DebugLogCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  function createLoaded(initial = state()) {
    const fixture = create();
    httpMock.expectOne(URL).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock?.verify());

  it('loads the global level and every category on init', () => {
    const fixture = createLoaded(state({ Media: 'Debug' }));
    const c = fixture.componentInstance;

    expect(c.loading()).toBe(false);
    expect(c.globalLevel()).toBe('Information');
    expect(c.categories().map((x) => x.name)).toEqual(['Scanning', 'Media', 'Reading']);

    const rows = fixture.nativeElement.querySelectorAll('.category-row') as NodeListOf<HTMLElement>;
    expect(rows.length).toBe(3);
    expect(rows[1].getAttribute('data-category')).toBe('Media');
    expect(rows[1].querySelector('.state')?.textContent?.trim()).toBe('Override');
    expect(rows[0].querySelector('.state')?.textContent?.trim()).toBe('Inherited');
  });

  it('maps an inherited category to the Inherit option and an override to its level', () => {
    const c = createLoaded(state({ Media: 'Debug' })).componentInstance;
    expect(c.selectValue(c.categories()[0])).toBe(INHERIT);
    expect(c.selectValue(c.categories()[1])).toBe('Debug');
  });

  it('PUTs only the changed category (no global level) and reflects the server response', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    c.setCategoryLevel(c.categories()[0], change('Debug'));
    expect(c.isSaving('Scanning')).toBe(true);

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ categories: [{ name: 'Scanning', level: 'Debug' }] });
    req.flush(state({ Scanning: 'Debug' }));
    fixture.detectChanges();

    expect(c.isSaving('Scanning')).toBe(false);
    expect(c.categories()[0]).toEqual({ name: 'Scanning', level: 'Debug', inherited: false });
    const row = fixture.nativeElement.querySelector('[data-category="Scanning"] .state') as HTMLElement;
    expect(row.textContent?.trim()).toBe('Override');
  });

  it('sends a null level to clear an override when Inherit is chosen', () => {
    const c = createLoaded(state({ Media: 'Debug' })).componentInstance;

    c.setCategoryLevel(c.categories()[1], change(INHERIT));

    const req = httpMock.expectOne(URL);
    expect(req.request.body).toEqual({ categories: [{ name: 'Media', level: null }] });
    req.flush(state());
    expect(c.categories()[1].inherited).toBe(true);
  });

  it('does not send a request when the value is unchanged', () => {
    const c = createLoaded(state({ Media: 'Debug' })).componentInstance;
    c.setCategoryLevel(c.categories()[1], change('Debug'));
    httpMock.expectNone(URL);
  });

  it('ignores a second change for a category whose save is still in flight', () => {
    const c = createLoaded().componentInstance;
    c.setCategoryLevel(c.categories()[0], change('Debug'));
    c.setCategoryLevel(c.categories()[0], change('Verbose'));

    const reqs = httpMock.match(URL);
    expect(reqs.length).toBe(1);
    reqs[0].flush(state({ Scanning: 'Debug' }));
  });

  it('reverts the select and surfaces the server message when the save fails', () => {
    const c = createLoaded().componentInstance;
    const source = { value: 'Debug' } as Partial<MatSelect>;

    c.setCategoryLevel(c.categories()[0], change('Debug', source));
    httpMock.expectOne(URL).flush(
      { error: 'invalid_level', message: 'nope', detail: null, correlationId: null },
      { status: 400, statusText: 'Bad Request' });

    expect(source.value).toBe(INHERIT);
    expect(c.categories()[0].inherited).toBe(true);
    expect(c.isSaving('Scanning')).toBe(false);
    expect(c.error()).toBe('nope');
  });

  it('PUTs only the global level (no categories) when the global level is changed', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    c.setGlobalLevel(change('Debug'));
    expect(c.savingGlobal()).toBe(true);

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ level: 'Debug' });
    req.flush(state({}, 'Debug'));
    fixture.detectChanges();

    expect(c.savingGlobal()).toBe(false);
    expect(c.globalLevel()).toBe('Debug');
  });

  it('shows an error instead of controls when the initial load fails', () => {
    const fixture = create();
    httpMock.expectOne(URL).flush(
      { error: 'forbidden', message: 'Admins only', detail: null, correlationId: null },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(fixture.componentInstance.loadFailed()).toBe(true);
    expect(fixture.nativeElement.querySelectorAll('.category-row').length).toBe(0);
    expect((fixture.nativeElement.querySelector('.error') as HTMLElement).textContent).toContain('Admins only');
  });

  // Wiring invariant (AGENTS.md): the component must be reachable, not just pass
  // its own tests. Asserts against the REAL route table.
  describe('wiring', () => {
    const adminChildren = () => routes.find((r) => r.path === '')?.children ?? [];

    it('is routed at admin/logging behind the admin guard', async () => {
      const route = adminChildren().find((r) => r.path === 'admin/logging');
      expect(route).toBeTruthy();
      expect(route!.canActivate?.length).toBe(1);
      const admin = adminChildren().find((r) => r.path === 'admin');
      expect(route!.canActivate![0]).toBe(admin!.canActivate![0]);
      expect(await (route!.loadComponent!() as Promise<unknown>)).toBe(DebugLogCardComponent);
    });
  });
});
