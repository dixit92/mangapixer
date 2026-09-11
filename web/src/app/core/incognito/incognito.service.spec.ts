import { TestBed } from '@angular/core/testing';

import { IncognitoService } from './incognito.service';

describe('IncognitoService', () => {
  let svc: IncognitoService;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
    svc = TestBed.inject(IncognitoService);
  });

  it('defaults to ON at session start', () => {
    expect(svc.isIncognito()).toBe(true);
  });

  it('persists the choice to sessionStorage and reads it back on a new instance', () => {
    svc.setIncognito(false);
    expect(sessionStorage.getItem('mangaplex.incognito')).toBe('0');
    // A reload constructs a fresh service in the same session: it must stay OFF.
    expect(new IncognitoService().isIncognito()).toBe(false);
  });

  it('toggles between states', () => {
    svc.toggle();
    expect(svc.isIncognito()).toBe(false);
    svc.toggle();
    expect(svc.isIncognito()).toBe(true);
  });

  it('sets an explicit state', () => {
    svc.setIncognito(false);
    expect(svc.isIncognito()).toBe(false);
    svc.setIncognito(true);
    expect(svc.isIncognito()).toBe(true);
  });
});
