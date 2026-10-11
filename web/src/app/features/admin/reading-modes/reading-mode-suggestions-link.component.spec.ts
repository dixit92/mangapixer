import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ReadingModeSuggestionsLinkComponent } from './reading-mode-suggestions-link.component';

/** The admin page's link to the reading mode suggestions (1.41.0): always shown, no request of its own. */
describe('ReadingModeSuggestionsLinkComponent', () => {
  it('links to the suggestions page', () => {
    TestBed.configureTestingModule({ imports: [ReadingModeSuggestionsLinkComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(ReadingModeSuggestionsLinkComponent);
    fixture.detectChanges();
    const link = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="reading-modes-link"]')!;
    expect(link.getAttribute('href')).toBe('/admin/reading-modes');
    expect(link.textContent).toContain('Reading mode suggestions');
  });
});
