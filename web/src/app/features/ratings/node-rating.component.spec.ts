import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { NodeRatingComponent } from './node-rating.component';
import { RatingsStateService } from './ratings-state.service';

/** 1.52.0 lane R: "Your rating" of one node - read from the node, editable once known. */
describe('NodeRatingComponent', () => {
  function create(getNode: ReturnType<typeof vi.fn>, known?: number | null) {
    const ratings = { changed$: of(), setRating: vi.fn(() => of(undefined)) };
    TestBed.configureTestingModule({
      imports: [NodeRatingComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: { getNode } }, { provide: RatingsStateService, useValue: ratings }],
    });
    const fixture = TestBed.createComponent(NodeRatingComponent);
    fixture.componentRef.setInput('nodeId', 'n1');
    fixture.componentRef.setInput('name', 'Akira');
    if (known !== undefined) fixture.componentRef.setInput('known', known);
    fixture.detectChanges();
    return { fixture, ratings, el: fixture.nativeElement as HTMLElement };
  }

  it('reads the viewer rating from the node and names the slider after it', () => {
    const getNode = vi.fn(() => of({ id: 'n1', myRating: 7 }));
    const { el } = create(getNode);
    expect(getNode).toHaveBeenCalledWith('n1');
    const slider = el.querySelector('[role="slider"]')!;
    expect(slider.getAttribute('aria-valuetext')).toBe('3.5 of 5 stars');
    expect(slider.getAttribute('aria-label')).toBe('Your rating: Akira');
    expect(slider.hasAttribute('aria-disabled')).toBe(false);
    expect(el.textContent).toContain('Your rating');
  });

  it('uses a known rating without reading the node', () => {
    const getNode = vi.fn();
    const { el } = create(getNode, 4);
    expect(getNode).not.toHaveBeenCalled();
    expect(el.querySelector('[role="slider"]')!.getAttribute('aria-valuenow')).toBe('4');
  });

  it('stays disabled when the node cannot be read (no write over an unknown value)', () => {
    const { el, ratings } = create(vi.fn(() => throwError(() => new Error('404'))));
    const slider = el.querySelector<HTMLElement>('[role="slider"]')!;
    expect(slider.getAttribute('aria-disabled')).toBe('true');
    el.querySelector<HTMLElement>('[data-testid="rating-half-6"]')!.click();
    expect(ratings.setRating).not.toHaveBeenCalled();
  });
});
