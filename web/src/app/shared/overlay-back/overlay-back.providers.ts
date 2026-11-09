import { Injectable, Injector, Provider, inject } from '@angular/core';
import { LocationStrategy, PathLocationStrategy } from '@angular/common';
import { OverlayKeyboardDispatcher, OverlayRef } from '@angular/cdk/overlay';
import { MatDialog } from '@angular/material/dialog';

import { BackSurface, OverlayBackService } from './overlay-back.service';

/**
 * The overlay layer and the router, wired to `OverlayBackService` (1.52.0, m1).
 *
 * Every CDK overlay (Material's menus, select lists, dialogs and bottom sheets, and the app's own popovers) registers with the
 * overlay keyboard dispatcher while it is attached; this subclass reports those with a backdrop - the surfaces a tap outside closes -
 * as Back-closable. Tooltips, snack bars and hover cards have no backdrop and are left alone. Back closes a surface the way a tap
 * outside does: a click on its backdrop (so each component's own close path runs: focus goes back to its trigger, a select keeps its
 * value, a sheet animates out). A dialog opened with `disableClose` refuses; Back then keeps the page and the dialog.
 */
@Injectable()
export class OverlayBackKeyboardDispatcher extends OverlayKeyboardDispatcher {
  private readonly injector = inject(Injector);
  private readonly surfaces = new Map<OverlayRef, BackSurface>();
  private adding = false;
  private service: OverlayBackService | null = null;

  override add(overlayRef: OverlayRef): void {
    // `add` removes the overlay first (to move it to the top); that is not a close.
    this.adding = true;
    try {
      super.add(overlayRef);
    } finally {
      this.adding = false;
    }
    if (this.surfaces.has(overlayRef) || !overlayRef.getConfig().hasBackdrop) return;
    const surface: BackSurface = { close: () => closeLikeATapOutside(overlayRef, this.injector) };
    this.surfaces.set(overlayRef, surface);
    this.back().opened(surface);
  }

  override remove(overlayRef: OverlayRef): void {
    super.remove(overlayRef);
    if (this.adding) return;
    const surface = this.surfaces.get(overlayRef);
    if (!surface) return;
    this.surfaces.delete(overlayRef);
    this.back().closed(surface);
  }

  private back(): OverlayBackService {
    return (this.service ??= this.injector.get(OverlayBackService));
  }
}

/** Closes an overlay as a tap on its backdrop would; false when it cannot be closed that way. */
export function closeLikeATapOutside(overlayRef: OverlayRef, injector: Injector): boolean {
  const dialogId = overlayRef.overlayElement?.querySelector('.mat-mdc-dialog-container')?.id;
  if (dialogId) {
    const dialog = injector.get(MatDialog, null)?.openDialogs.find((d) => d.id === dialogId);
    if (dialog?.disableClose) return false;
  }
  const backdrop = overlayRef.backdropElement;
  if (!backdrop) return false;
  backdrop.click();
  return true;
}

/**
 * The router's history writes and the app's own Back, routed through `OverlayBackService`: a URL is never pushed on top of a
 * surface's entry (it replaces it), and `Location.back()` steps past those entries to the real previous page.
 */
@Injectable()
export class OverlayBackLocationStrategy extends PathLocationStrategy {
  private readonly overlays = inject(OverlayBackService);

  override pushState(state: unknown, title: string, url: string, queryParams: string): void {
    this.overlays.routerPush(
      () => super.pushState(state, title, url, queryParams),
      () => super.replaceState(state, title, url, queryParams));
  }

  override replaceState(state: unknown, title: string, url: string, queryParams: string): void {
    this.overlays.routerReplace(() => super.replaceState(state, title, url, queryParams));
  }

  override back(): void {
    this.overlays.appStep(-1, (d) => (d === -1 ? super.back() : super.historyGo(d)));
  }

  override historyGo(relativePosition = 0): void {
    this.overlays.appStep(relativePosition, (d) => super.historyGo(d));
  }
}

/** Back closes the open surface only (m1). Add once, to the app's providers. */
export function provideOverlayBack(): Provider[] {
  return [
    { provide: OverlayKeyboardDispatcher, useClass: OverlayBackKeyboardDispatcher },
    { provide: LocationStrategy, useClass: OverlayBackLocationStrategy },
  ];
}
