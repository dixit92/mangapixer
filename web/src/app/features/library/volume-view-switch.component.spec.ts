import { TestBed } from '@angular/core/testing';

import { VolumeViewSwitchComponent } from './volume-view-switch.component';

describe('VolumeViewSwitchComponent (1.29.0)', () => {
  function setup(active: boolean, disabled = false) {
    const fixture = TestBed.createComponent(VolumeViewSwitchComponent);
    fixture.componentRef.setInput('active', active);
    fixture.componentRef.setInput('disabled', disabled);
    fixture.componentRef.setInput('disabledHint', 'Needs the Name sort');
    const picks: boolean[] = [];
    fixture.componentInstance.changed.subscribe((v) => picks.push(v));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, picks, volumes: el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement, folders: el.querySelector('[data-testid="view-folders"]') as HTMLButtonElement };
  }

  it('marks the active segment and emits only a change', () => {
    const { volumes, folders, picks } = setup(true);
    expect(volumes.getAttribute('aria-pressed')).toBe('true');
    expect(folders.getAttribute('aria-pressed')).toBe('false');

    volumes.click(); // already active: nothing
    expect(picks).toEqual([]);
    folders.click();
    expect(picks).toEqual([false]);
  });

  it('switches back to Volumes from Folders', () => {
    const { volumes, picks } = setup(false);
    volumes.click();
    expect(picks).toEqual([true]);
  });

  it('reads Chapters for a webtoon without a volume list (1.34.0)', () => {
    const fixture = TestBed.createComponent(VolumeViewSwitchComponent);
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('chapters', true);
    fixture.detectChanges();
    const first = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="view-volumes"]') as HTMLButtonElement;
    expect(first.textContent).toContain('Chapters');
    expect(first.textContent).not.toContain('Volumes');
    expect(first.title).toBe('Show the chapters in order');
  });

  it('reads Volumes by default', () => {
    const { volumes } = setup(true);
    expect(volumes.textContent).toContain('Volumes');
    expect(volumes.title).toBe('Group chapters into volumes');
  });

  it('is inert with a hint while a sort or filter makes the list flat', () => {
    const { volumes, folders, picks } = setup(true, true);
    expect(volumes.disabled).toBe(true);
    expect(folders.disabled).toBe(true);
    expect(folders.title).toBe('Needs the Name sort');
    folders.click();
    expect(picks).toEqual([]);
  });
});
