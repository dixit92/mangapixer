import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, Subject, of, throwError } from 'rxjs';

import { StarToggleComponent } from './star-toggle.component';
import { FavoritesStateService, FavoriteChange } from '../../core/favorites/favorites-state.service';

/** Host that drives the star through its signal inputs, as browse/search will. */
@Component({
  standalone: true,
  imports: [StarToggleComponent],
  template: `<div class="cover"><app-star-toggle [nodeId]="nodeId()" [favorite]="favorite()" /></div>`,
})
class HostComponent {
  readonly nodeId = signal('node-1');
  readonly favorite = signal(false);
}

/** Fake state service: records persistence calls and lets a test drive `changed$`. */
class FakeFavoritesStateService {
  readonly changedSubject = new Subject<FavoriteChange>();
  readonly changed$ = this.changedSubject.asObservable();
  result: Observable<void> = of(undefined);
  setFavorite = vi.fn((_nodeId: string, _favorite: boolean) => this.result);
}

/**
 * Reusable star favorite toggle (1.21.0): select/deselect, optimistic update with
 * revert-on-error, keyboard-accessible button semantics (aria-pressed / aria-label),
 * and cross-instance sync via `FavoritesStateService.changed$`.
 */
describe('StarToggleComponent', () => {
  let fake: FakeFavoritesStateService;

  function create(favorite = false) {
    fake = new FakeFavoritesStateService();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideNoopAnimations(),
        { provide: FavoritesStateService, useValue: fake },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.favorite.set(favorite);
    fixture.detectChanges();
    return fixture;
  }

  function button(fixture: ReturnType<typeof create>): HTMLButtonElement {
    return fixture.nativeElement.querySelector('app-star-toggle .star-btn');
  }

  function icon(fixture: ReturnType<typeof create>): string {
    return fixture.nativeElement.querySelector('app-star-toggle mat-icon')!.textContent!.trim();
  }

  it('renders an empty star with correct aria when not favorited', () => {
    const fixture = create(false);
    expect(icon(fixture)).toBe('star_border');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('false');
    expect(button(fixture).getAttribute('aria-label')).toBe('Add to favorites');
  });

  it('renders a filled star with correct aria when favorited', () => {
    const fixture = create(true);
    expect(icon(fixture)).toBe('star');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('true');
    expect(button(fixture).getAttribute('aria-label')).toBe('Remove from favorites');
  });

  it('selects optimistically and persists on click', () => {
    const fixture = create(false);
    button(fixture).click();
    fixture.detectChanges();

    expect(fake.setFavorite).toHaveBeenCalledWith('node-1', true);
    expect(icon(fixture)).toBe('star');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('true');
  });

  it('deselects a favorited node on click', () => {
    const fixture = create(true);
    button(fixture).click();
    fixture.detectChanges();

    expect(fake.setFavorite).toHaveBeenCalledWith('node-1', false);
    expect(icon(fixture)).toBe('star_border');
  });

  it('reverts the optimistic flip when persistence fails', () => {
    const fixture = create(false);
    fake.result = throwError(() => new Error('boom'));
    button(fixture).click();
    fixture.detectChanges();

    // Flipped to star, then reverted back to star_border on error.
    expect(icon(fixture)).toBe('star_border');
    expect(button(fixture).getAttribute('aria-pressed')).toBe('false');
  });

  it('syncs in place when the same node is toggled elsewhere', () => {
    const fixture = create(false);
    fake.changedSubject.next({ nodeId: 'node-1', favorite: true });
    fixture.detectChanges();
    expect(icon(fixture)).toBe('star');

    // A different node's change is ignored.
    fake.changedSubject.next({ nodeId: 'other', favorite: false });
    fixture.detectChanges();
    expect(icon(fixture)).toBe('star');
  });

  it('updates when the favorite input changes (parent re-fetch)', () => {
    const fixture = create(false);
    expect(icon(fixture)).toBe('star_border');
    fixture.componentInstance.favorite.set(true);
    fixture.detectChanges();
    expect(icon(fixture)).toBe('star');
  });
});
