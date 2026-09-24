# Reader

Select any archive in a library to open it in the reader. This page covers the reader's layouts, controls and how it remembers where you are.

![The reader on desktop: a single page centered on a dark background, the toolbar above and the page slider below](../assets/screenshots/docs-reader-desktop.png)

![The reader toolbar on desktop: back arrow and page counter on the left; previous and next chapter, reading mode, image fit, direction, page transition, help and fullscreen on the right](../assets/screenshots/docs-reader-toolbar-desktop.png)

## Toolbar

On desktop and tablet, the toolbar at the top holds:

- **Back to folder**.
- The page counter (`12 / 40`, or `12-13 / 40` while a double page is showing).
- **Favorite** (the star): adds the open chapter to your [favorites](library-layout.md#favorites).
- **Previous chapter** / **Next chapter**. Their tooltips name the neighboring archive. They are disabled at either end of the folder.
- **Reading mode**.
- **Image fit**, or the **Page width** slider in vertical mode.
- The reading-direction toggle.
- **Page transition**, or **Tap to scroll** in vertical mode.
- **Rendering** (the magic-wand icon): the picture-quality options described in [Image quality](#image-quality).
- **Reading help** (`?`).
- **Fullscreen**.

In a normal window the controls stay visible. In fullscreen they hide after 3 seconds without input. Tap the center of the page, press `m`, or (with a mouse) move to the top of the screen to bring them back.

The bottom bar has a **page slider**: tap to jump, or drag to scrub, with a bubble showing where you will land. When the controls are hidden, a thin progress line shows how far through the chapter you are.

### Fullscreen on iPad and iPhone

In Safari, **Fullscreen** switches the reader to its own immersive in-page mode instead of Safari's fullscreen: Safari's fullscreen would leave a system close button and the status bar sitting over the page, and the page has no way to hide either. Immersive mode hides the reader's own toolbar and bars the same way fullscreen does elsewhere, without any of that.

For a reader with no Safari bars at all, add MangaPixer to the Home Screen (**Share** > **Add to Home Screen**). The installed app has no browser bars and an opaque black status bar. The same goes for an app installed from Chrome on Android. In an installed app, the reader opens in immersive mode, with its own bars hidden; tap the center of the page to bring them back.

### On phones

On screens narrower than 600 px, the toolbar keeps only **Next chapter**, **Fullscreen** and **Reader options** (⋮). **Reader options** opens a bottom sheet with every other setting:

- **Layout**
- **Image fit**
- **Reading direction**
- **Page transition**
- **Rendering**, **Page quality** and **Downscale filter** (see [Image quality](#image-quality))
- In vertical mode, **Page width** and **Tap to scroll** replace the paged-only groups.
- **Previous chapter**, **Next chapter** and **Reading help**.

Changing a setting keeps the sheet open.

![The Reader options bottom sheet on a phone, with the Layout, Image fit, Reading direction and Page transition groups and the chapter buttons](../assets/screenshots/docs-reader-phone-options.png)

## Page modes

Choose a mode from **Reading mode** (on phones: **Layout**):

| Mode | Phone label | What it does |
|---|---|---|
| **Auto (orientation)** | Auto | Double page in landscape, single page in portrait. Switches live when you rotate the device or resize the window. |
| **Single page** | Single page | One page at a time. |
| **Double page** | Double page | Two pages side by side: 1-2, 3-4, … |
| **Double page (shifted)** | Double, shifted | Pairing moved by one page: the cover shows alone, then 2-3, 4-5, … Use this when a two-page spread is split across the wrong pair. |
| **Vertical (webtoon)** | Vertical | One continuous vertical strip you scroll through. |

Details:

- **Wide pages are never paired.** A page at least 1.2 times wider than it is tall (usually a scanned spread) is shown alone at full width in both double modes, and pairing restarts after it.
- **Narrow portrait screens show single pages.** If you pick a double mode on a narrow portrait screen (under 600 px wide), pages still show one at a time. Double page returns when you rotate to landscape or widen the window.
- **Your layout choice is remembered by this browser** (Auto, Single or Double). It applies to every chapter you open on that device, and is not saved to your account. The double-page pairing is saved differently: see [Fixing double-page pairing](#fixing-double-page-pairing).
- **Choosing Vertical from the menu lasts for the current chapter only.** To make a series always open in vertical mode, an admin sets its reading direction to **Vertical** (see [Reading direction](#reading-direction)).

### Fixing double-page pairing

Scanned releases often insert extra pages (credits, colour pages), so from some point on the pages pair up wrongly: the left half of a spread ends up next to the right half of the previous one. You can fix this anywhere in an archive:

- **Pick the other double-page mode.** The highlighted entry, **Double page** or **Double page (shifted)**, shows how the spread on screen is paired. Picking the other one shifts the pairing by one page from this spread on, up to the next wide page. On the first page this is the classic "cover alone" setting.
- **Press `o`.** The same as picking the other mode.
- **Or use `d`.** At a wrongly paired spread, press `d` for single page, go to the next page, and press `d` again: the pairing now starts at that page.

Things to know:

- **The pairing is saved for the archive, for everyone.** It is stored on the server as a property of the file, so every user who can read the archive sees the same pairing, and anyone who can read it can change it.
- **A changed file starts over.** If the archive is replaced or modified, its saved pairing is dropped.
- **Archives you have not adjusted** use the setting you last picked on the first page of an archive (cover alone or not), remembered by this browser.
- If the pairing cannot be saved (for example, you are offline), it still applies until you leave the chapter, and the reader says so.

## Image fit

**Image fit** has four options:

- **Fit screen** (default): the whole page fits on screen.
- **Fit width**
- **Fit height**
- **Original size**

The fit resets to **Fit screen** each time you open the reader.

In vertical mode, **Image fit** is replaced by a **Page width** slider from 15% to 100% of the screen width. The default is 70%, and your browser remembers the setting. Pages in the strip sit edge to edge, with no gap.

## Image quality

Scanned pages rarely match your screen pixel for pixel, so every page is either shrunk or enlarged before you see it. Manga and comics are mostly line art and screentones (fine dot patterns), and both suffer when a browser resizes them with its default method: lines go soft and screentones can shimmer into moire patterns. The **Rendering** menu (magic-wand icon; on phones, in **Reader options**) holds three settings that control this. All three are remembered by this browser, not your account.

**Page quality**

- **Auto** (default): the reader works out how large each page will actually appear on your screen, including its pixel density, and asks the server for a copy at about that size. The server shrinks the page once, with a high-quality filter, and caches the result. Pages load faster and look cleaner than when the browser shrinks a full-size scan.
- **Full**: always download the original page. Use this if you zoom in a lot, or want the exact source pixels.

The server never enlarges a page: if your screen needs more pixels than the scan has, you get the original. Animated pages are always sent as they are.

**Downscale filter** (Auto page quality only)

How the server shrinks a page:

| Filter | Look | Good for |
|---|---|---|
| **Sharp** | Crispest lines; may show moire on screentones | Clean digital releases with little screentone |
| **Balanced** (default) | A middle ground between the other two | Most scans |
| **Soft** | Smoothest screentones; lines slightly softer | Heavily screentoned print scans |

Press `s` to cycle through the filters while reading. With **Full** page quality nothing is shrunk, so the filter is greyed out ("Applies to Auto page quality").

**Rendering**

- **Smooth** (default): the browser's normal image scaling.
- **Enhance**: when a page is shown *larger* than its original resolution, it is redrawn on your device's graphics chip with Anime4K, an upscaler designed for anime and line art, to keep lines sharp. Pages that are not being enlarged are left alone, and nothing is sent to the server.

Enhance needs a browser with WebGPU. The Rendering button's tooltip says whether WebGPU is ready, and without it Enhance is greyed out ("Enhance needs WebGPU"). It works in single and double page modes; in vertical mode it is greyed out ("Enhance is for paged views"). Press `e` to switch between Smooth and Enhance. Enhance uses the graphics chip, so it can use more battery on phones and tablets.

Enhance is built on [Anime4K](https://github.com/bloc97/Anime4K) by bloc97, through the [anime4k-webgpu](https://github.com/Anime4KWebBoost/Anime4K-WebGPU) port. See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Reading direction

The reader supports left-to-right (comics), right-to-left (manga) and vertical (webtoon) content. In right-to-left mode:

- The arrow keys swap: `←` moves forward.
- The side tap zones swap.
- Swipes are mirrored.
- The page slider fills from the right.

**Where the direction comes from.** When you open an archive, the server picks its mode from the nearest setting it finds:

1. the folder's direction,
2. then the library's direction,
3. otherwise left-to-right.

Admins set these (readers cannot):

- **Per library:** **MangaPixer Administration** > **Libraries**, the **Direction** menu on each row: **Inherit**, **Left-to-right**, **Right-to-left** or **Vertical**.
- **Per folder:** in the library view, choose **Select**, pick one or more folders, then **Direction**. The same options appear there, with **Inherit (clear)** removing the folder's own setting. Admins see a small `LTR` / `RTL` / `Vertical` chip on folders that have their own direction.

The toolbar toggle (tooltip **Right-to-left (manga)** / **Left-to-right**) flips the direction for the chapter you are reading. It is not saved. The library sidebar and home cards show a small icon with each library's direction.

## Page transition

**Page transition** controls the animation when you turn a page in single or double mode:

- **Slide** (default): the new page slides in.
- **Reveal**: the new page is wiped in over the old one.
- **None**: pages change instantly.

The choice is remembered by this browser. Animations are skipped in vertical mode, while scrubbing the slider, and when the page is zoomed. They are also turned off entirely if your operating system asks for reduced motion.

## Keyboard shortcuts

| Key | Action |
|---|---|
| `←` / `→` | Previous / next page (swapped in right-to-left). At the end or start of a chapter, moves to the next or previous chapter. |
| `Home` / `End` | First / last page |
| `f` | Toggle fullscreen |
| `d` | Switch between single and double page. Keeps the archive's pairing; switching to double page makes the page you are on start a spread. |
| `o` | Double page: shift the pairing by one page from the current spread (saved for the archive, see [Fixing double-page pairing](#fixing-double-page-pairing)) |
| `s` | Cycle the downscale filter: Sharp, Balanced, Soft |
| `e` | Switch Rendering between Smooth and Enhance (when Enhance is available) |
| `m` | Show / hide the controls |
| `Esc` | Close help. Otherwise exit fullscreen, or go back to the folder if not in fullscreen. |
| `?` | Show / hide the help overlay |

- In vertical mode only `m`, `s`, `?` and `Esc` (to close help) apply. Scroll with the mouse wheel, trackpad or the usual scroll keys.
- Shortcuts are ignored while you type in a text field.
- When the page slider has keyboard focus, the arrow keys step one page and `Home` / `End` jump to the ends.

## Touch and mouse

**Single and double page:**

- **Tap the left or right 30% of the screen** to turn the page. In right-to-left mode the sides swap.
- **Tap the center** to show or hide the controls.
- **Swipe left or right anywhere on the page** to turn the page. Start the swipe inside the page: the very edge of the screen belongs to the browser's back/forward gesture. Swipes are ignored while the page is zoomed.

**Vertical mode:**

- **Scroll** freely. This is always available.
- **Tap to scroll** adds tap zones. Options: **Off**, **80%**, **90%** (default) or **100%** of a screen per step. The setting is remembered by this browser.
  - Tap the **bottom third** to move forward one step.
  - Tap the **top third** to move back one step.
  - Tap the **middle** to show or hide the controls.
  - A horizontal swipe does the same (swipe left to go forward).
- With **Tap to scroll** off, a tap anywhere shows or hides the controls.
- At the bottom of the strip, **Previous chapter** and **Next chapter** buttons appear. Vertical mode never moves to another chapter on its own.

## Help overlay

Select **Reading help** (`?`) or press `?` to see the controls for the current mode, drawn over the page. The overlay opens automatically the first time you use the reader on each device. Tap anywhere to close it.

![The help overlay drawn over a page: tap zones on the left, center and right, the swipe band across the middle, and the Reader controls card listing the keyboard shortcuts](../assets/screenshots/docs-reader-help-overlay.png)

## Moving between chapters

In single and double page mode:

- Turning past the **last page** opens the next archive in the same folder.
- Going back from the **first page** opens the previous archive, on its last page.
- A short message names the chapter you moved to. At either end of the folder you see "You’ve reached the end. No next chapter in this folder." or "You’re at the start. No previous chapter in this folder."

"Next" follows the folder's Name order (see [Library layout](library-layout.md#folders-and-archives)). Chapter moves replace the browser history entry, so **Back** always returns to the folder.

## Loading and prefetch

The reader loads ahead so page turns feel instant:

- **Single and double page:** the next 6 pages and the previous 2.
- **Vertical:** the next 4 pages below your scroll position.
- Prefetch stays within the current chapter.

On the server, the page you are looking at always takes priority over prefetching, and prefetching takes priority over background work such as analyzing new archives. Extracted pages are cached (see the cache budget in [Configuration](configuration.md#storage)), so rereading is fast.

## Where you resume

The server saves your position as you read, including when you let go of the page slider. When you open an archive again:

| Archive state | Opens on |
|---|---|
| Never opened | Page 1 |
| Started, not finished | The page where you left off |
| Finished (you reached the last page) | Page 1 |
| Marked read, but you last stopped partway through | Where you left off, or page 1 if **Always open read archives from the start** is on |

**Always open read archives from the start** is in **Settings** > **Reading**. It is off by default and saved to your account.

## Read and unread

**Reaching the last page marks an archive as read.** In vertical mode, scrolling to the very bottom counts. The read mark is sticky: going back to an earlier page later does not make the archive unread again. Arriving on a chapter's last page through **Previous chapter** does not mark it read.

There is no mark-read button inside the reader. To mark archives or whole folders yourself, use the library view:

1. Choose **Select**.
2. Pick items. Shift-click selects a range, and on touch screens a long press offers **Select to here**. The **Select** menu also offers **Select all**, **Select all unread** and **Select all read**.
3. Choose **Mark read** or **Mark unread**, then **Done**.

- Selecting a folder applies to every archive below it, at any depth.
- **Mark unread** fully resets an archive: it clears both the read mark and your position, so it opens on page 1 next time.
- Cards show **✓ Read** or **Reading** badges. Folders show **✓ Read** when everything inside is read and **Reading** when it is partly read.
- On the home page, the **×** on a **Continue reading** card (**Remove from Continue reading**) hides it from that row without marking it read.

## Incognito and your reading history

Incognito hides the libraries you marked **Private** from Home, search and browsing. **It does not pause progress tracking.** Your position and read marks are still saved while Incognito is on, including in Private libraries. See [Users and access](users-and-access.md#private-libraries-and-incognito).
