# MangaPixer compared

MangaPixer is one of several self-hosted servers for comics and manga. This page explains where it differs from [Komga](https://komga.org/), [Kavita](https://www.kavitareader.com/) and [YACReader](https://www.yacreader.com/), so you can pick the one that fits how your collection is organized. All four are free and open source, and all of them are good projects: if MangaPixer is not the right fit, one of the others probably is.

The descriptions of the other projects summarize their own documentation as of September 2026. Check their sites for the current details.

## At a glance

| | MangaPixer | Komga | Kavita | YACReader |
|---|---|---|---|---|
| What it is | Server with a web reader | Server with a web reader | Server with a web reader | Desktop reader and library, with a server for its mobile apps |
| Library structure | Your folder tree as it is, at any depth | Each folder that holds files becomes a series | One folder per series, no files at the library root; volumes and chapters come from file names | Your folder tree as it is |
| Reading | In the browser: desktop, tablet, phone | In the browser, and in apps through OPDS and sync features | In the browser, and in apps through OPDS | In the desktop app and the YACReader mobile apps |
| Formats | CBZ/ZIP and CBR/RAR ([details](library-layout.md#supported-archive-formats)) | Comics, manga, PDF and EPUB | Comics, manga, PDF and EPUB | Comics, manga and PDF |
| Metadata | Optional: `ComicInfo.xml`, and MangaUpdates if an admin allows it | Rich metadata management | Rich metadata management | Comic information and tags in the library |
| License | MIT | MIT | GPL-3.0 | GPL-3.0 |

## Choose MangaPixer if

- **Your folders are already the way you want them.** MangaPixer shows your folder tree as the library, whatever its depth or naming: series folders, volume subfolders, loose archives next to folders. Nothing needs renaming or restructuring first. See [Library layout](library-layout.md).
- **You want your files left alone.** MangaPixer never writes to your library folders; the Docker setup mounts them read-only. Everything it creates lives in its own data folders. See [How MangaPixer works](how-it-works.md).
- **You read in a browser on several devices.** The reader is built for desktop, tablet and phone, with manga (right-to-left), double-page and vertical webtoon modes and image upscaling on your device. See [Reader](reader.md).
- **Several people share one server.** Each user has their own progress and read marks; admins decide who sees which library, and readers can keep libraries private. See [Users and access](users-and-access.md).

## Choose another server if

- **Your library is organized around metadata**, or you want to edit and curate metadata heavily: Komga and Kavita both do this well.
- **You need PDF or EPUB**, OPDS for third-party reading apps, or e-reader sync: Komga and Kavita support some or all of these; MangaPixer does not yet.
- **You mostly read on one computer** with a desktop app: YACReader is a long-standing, folder-based desktop reader, and MangaPixer can [import its read progress](backup-and-restore.md#importing-reading-progress-from-yacreader).
