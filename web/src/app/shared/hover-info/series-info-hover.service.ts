import { ComponentRef, Injectable, Injector, inject, signal } from '@angular/core';
import {
  OverlayRef,
  createCloseScrollStrategy,
  createFlexibleConnectedPositionStrategy,
  createOverlayRef,
} from '@angular/cdk/overlay';
import { ComponentPortal } from '@angular/cdk/portal';
import { NavigationStart, Router } from '@angular/router';
import { Subscription, filter } from 'rxjs';

import { SeriesInfoDto } from '../../core/api/api-types';
import { hasSeriesContent } from '../../features/metadata/series-info-labels';
import { SeriesInfoCacheService } from './series-info-cache.service';
import { SeriesInfoHoverPreferenceService } from './series-info-hover-preference.service';
import type { SeriesInfoPopoverComponent } from './series-info-popover.component';

/** Only devices whose primary pointer hovers precisely (mouse, trackpad). */
export const HOVER_QUERY = '(hover: hover) and (pointer: fine)';

/** How long the pointer must rest before anything is fetched or shown (owner, 2026-09-27). */
export const HOVER_OPEN_DELAY_MS = 400;

/** Grace after leaving, so the pointer can cross a gap (cover -> title, card -> popover). */
export const HOVER_CLOSE_GRACE_MS = 200;

export function canHover(): boolean {
  return typeof matchMedia === 'function' && matchMedia(HOVER_QUERY).matches;
}

/**
 * Series information on hover (1.27.0): the one controller behind every
 * `appSeriesInfoHover` zone, so at most one popover is open app-wide.
 *
 * - `enter(nodeId, anchor)` starts a {@link HOVER_OPEN_DELAY_MS} timer; nothing is
 *   fetched before it fires, so sweeping the pointer across a grid sends no request.
 *   Then the summary comes from {@link SeriesInfoCacheService} (one local GET the first
 *   time) and opens beside the anchor if the pointer is still there and the node has
 *   something to show.
 * - `leave(nodeId)` closes after {@link HOVER_CLOSE_GRACE_MS}; entering another zone
 *   of the same node, or the popover itself, within the grace keeps it open.
 * - Closes on Esc, scroll, any pointer press, and navigation. Never opens on keyboard
 *   focus (the (i) and its side panel serve keyboard users) or without a hovering fine
 *   pointer (touch: no change).
 * - Honours the per-user "Series information on hover" option (server-side, default
 *   ON) through `SeriesInfoHoverPreferenceService`.
 */
@Injectable({ providedIn: 'root' })
export class SeriesInfoHoverService {
  private readonly injector = inject(Injector);
  private readonly cache = inject(SeriesInfoCacheService);
  private readonly preference = inject(SeriesInfoHoverPreferenceService);

  private target: { nodeId: string; anchor: HTMLElement } | null = null;
  private openTimer: ReturnType<typeof setTimeout> | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;
  private fetch: Subscription | null = null;

  private overlayRef: OverlayRef | null = null;
  private popover: ComponentRef<SeriesInfoPopoverComponent> | null = null;
  private openNodeId: string | null = null;

  /** The node whose popover is showing (tests, e2e). */
  readonly openFor = signal<string | null>(null);

  constructor() {
    this.preference.userChanged$.subscribe(() => {
      this.cache.clear();
      this.close();
    });
    inject(Router).events.pipe(filter((e) => e instanceof NavigationStart)).subscribe(() => this.close());
    if (typeof document !== 'undefined') {
      document.addEventListener('keydown', (e) => { if (e.key === 'Escape') this.close(); });
      document.addEventListener('pointerdown', (e) => {
        const pane = this.overlayRef?.overlayElement;
        if (!pane || !pane.contains(e.target as Node)) this.close();
      }, true);
    }
  }

  /** Hover zones call this when they appear: loads the user's option once. */
  ensurePreference(): void {
    this.preference.ensureLoaded();
  }

  enter(nodeId: string, anchor: HTMLElement): void {
    if (!this.preference.enabled() || !canHover()) return;
    if (this.target?.nodeId === nodeId) {
      // Another zone of the same item (cover -> title), or back from the popover.
      this.clearCloseTimer();
      return;
    }
    this.close();
    this.target = { nodeId, anchor };
    this.openTimer = setTimeout(() => this.resolve(), HOVER_OPEN_DELAY_MS);
  }

  leave(nodeId: string): void {
    if (this.target?.nodeId !== nodeId) return;
    this.scheduleClose();
  }

  close(): void {
    this.clearCloseTimer();
    if (this.openTimer) clearTimeout(this.openTimer);
    this.openTimer = null;
    this.fetch?.unsubscribe();
    this.fetch = null;
    this.target = null;
    this.detach();
  }

  private resolve(): void {
    this.openTimer = null;
    const target = this.target;
    if (!target) return;
    this.fetch = this.cache.get(target.nodeId).subscribe({
      next: (info) => {
        if (this.target === target) void this.show(target, info);
      },
      error: () => { if (this.target === target) this.close(); },
    });
  }

  private async show(target: { nodeId: string; anchor: HTMLElement }, info: SeriesInfoDto): Promise<void> {
    if (!hasSeriesContent(info) || !target.anchor.isConnected) {
      this.close();
      return;
    }
    const { SeriesInfoPopoverComponent } = await import('./series-info-popover.component');
    if (this.target !== target) return;

    const position = createFlexibleConnectedPositionStrategy(this.injector, target.anchor)
      .withPositions([
        { originX: 'end', originY: 'top', overlayX: 'start', overlayY: 'top', offsetX: 8 },
        { originX: 'start', originY: 'top', overlayX: 'end', overlayY: 'top', offsetX: -8 },
        { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 8 },
        { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -8 },
      ])
      .withViewportMargin(8)
      .withPush(true);
    this.overlayRef = createOverlayRef(this.injector, {
      positionStrategy: position,
      scrollStrategy: createCloseScrollStrategy(this.injector),
      hasBackdrop: false,
      panelClass: 'series-info-hover-pane',
    });
    this.overlayRef.detachments().subscribe(() => {
      if (this.openNodeId !== null) this.close();
    });
    this.popover = this.overlayRef.attach(new ComponentPortal(SeriesInfoPopoverComponent, null, this.injector));
    this.popover.setInput('info', info);
    this.popover.changeDetectorRef.detectChanges(); // render now, so the overlay measures its real size
    this.popover.instance.pointerEnter.subscribe(() => this.clearCloseTimer());
    this.popover.instance.pointerLeave.subscribe(() => this.scheduleClose());
    this.openNodeId = target.nodeId;
    this.openFor.set(target.nodeId);
  }

  private detach(): void {
    const ref = this.overlayRef;
    this.overlayRef = null;
    this.popover = null;
    this.openNodeId = null;
    this.openFor.set(null);
    ref?.dispose();
  }

  private scheduleClose(): void {
    this.clearCloseTimer();
    this.closeTimer = setTimeout(() => this.close(), HOVER_CLOSE_GRACE_MS);
  }

  private clearCloseTimer(): void {
    if (this.closeTimer) clearTimeout(this.closeTimer);
    this.closeTimer = null;
  }
}
