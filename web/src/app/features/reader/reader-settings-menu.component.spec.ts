import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ReaderSettingsMenuComponent } from './reader-settings-menu.component';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';

describe('ReaderSettingsMenuComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderSettingsMenuComponent],
      providers: [provideNoopAnimations()],
    });
    localStorage.clear();
    const fixture = TestBed.createComponent(ReaderSettingsMenuComponent);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, prefs: TestBed.inject(ReaderPreferencesService) };
  }

  it('renders a page-transition trigger button', () => {
    const { fixture } = create();
    const btn = (fixture.nativeElement as HTMLElement).querySelector('button[aria-label="Page transition"]');
    expect(btn).toBeTruthy();
  });

  it('choose() writes the preference through the shared service', () => {
    const { c, prefs } = create();
    c.choose('reveal');
    expect(prefs.pageAnimation()).toBe('reveal');
    c.choose('none');
    expect(prefs.pageAnimation()).toBe('none');
  });

  it('emits opened/closed for chrome pinning', () => {
    const { c } = create();
    let opened = 0;
    let closed = 0;
    c.opened.subscribe(() => opened++);
    c.closed.subscribe(() => closed++);
    c.opened.emit();
    c.closed.emit();
    expect(opened).toBe(1);
    expect(closed).toBe(1);
  });
});
