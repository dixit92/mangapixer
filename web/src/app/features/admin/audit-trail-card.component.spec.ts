import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { AuditTrailCardComponent } from './audit-trail-card.component';
import { ApiService } from '../../core/api/api.service';
import { AuditEventDto, AuditTrailPageDto } from '../../core/api/api-types';

/** Audit trail card (1.18.0; its own component since 1.36.0): paged read of the audit store. */
describe('AuditTrailCardComponent', () => {
  const event = (id: number, overrides: Partial<AuditEventDto> = {}): AuditEventDto => ({
    id, action: 'user.create', result: 'success', actorUserId: 1, actorUserName: 'admin', targetUserId: 7,
    timestamp: '2026-10-07T10:00:00Z', correlationId: null, ...overrides,
  });
  const pageOf = (items: AuditEventDto[], totalCount: number, page = 1): AuditTrailPageDto =>
    ({ items, totalCount, page, pageSize: AuditTrailCardComponent.PageSize });

  function create(getAuditTrail: ReturnType<typeof vi.fn>) {
    TestBed.configureTestingModule({
      imports: [AuditTrailCardComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: { getAuditTrail } }],
    });
    const fixture = TestBed.createComponent(AuditTrailCardComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('loads the first page of 50 and lists the events', () => {
    const get = vi.fn().mockReturnValue(of(pageOf([event(1), event(2, { actorUserName: null, targetUserId: null })], 2)));
    const fixture = create(get);

    expect(get).toHaveBeenCalledWith(1, 50);
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Audit trail');
    expect(text).toContain('user.create · success');
    expect(text).toContain('by admin');
    expect(text).toContain('target #7');
    expect(text).toContain('Page 1 of 1');
    expect(fixture.nativeElement.querySelectorAll('mat-list-item').length).toBe(2);
  });

  it('shows the empty state without a pager', () => {
    const fixture = create(vi.fn().mockReturnValue(of(pageOf([], 0))));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('No audit events recorded yet.');
    expect(el.querySelector('.audit-pager')).toBeNull();
  });

  it('pages forward and back within the total', () => {
    const get = vi.fn().mockImplementation((page: number) => of(pageOf([event(page)], 120, page)));
    const fixture = create(get);
    const card = fixture.componentInstance;
    expect(card.totalPages()).toBe(3);

    card.prevPage(); // already on the first page: no request
    expect(get).toHaveBeenCalledTimes(1);
    card.nextPage();
    card.nextPage();
    card.nextPage(); // past the last page: no request
    expect(get).toHaveBeenCalledTimes(3);
    expect(get).toHaveBeenLastCalledWith(3, 50);
    card.prevPage();
    expect(get).toHaveBeenLastCalledWith(2, 50);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Page 2 of 3');
  });
});
