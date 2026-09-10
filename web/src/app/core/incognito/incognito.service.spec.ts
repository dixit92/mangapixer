import { TestBed } from '@angular/core/testing';

import { IncognitoService } from './incognito.service';

describe('IncognitoService', () => {
  let svc: IncognitoService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    svc = TestBed.inject(IncognitoService);
  });

  it('defaults to ON at session start', () => {
    expect(svc.isIncognito()).toBe(true);
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
