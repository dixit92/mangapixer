# Third-Party Notices

MangaPixer is licensed under the MIT License - see [LICENSE](LICENSE). This file
attributes the third-party software that MangaPixer **ships** (inside the
container image / publish output) and the software it uses **only to build and
test**. Each third-party component remains under its own license.

Copies of these notices ship with the binaries. The container image carries them
in `/app/licenses/`: this file, `LICENSE`, the Angular build's
`3rdpartylicenses.txt` (license texts of the npm packages bundled into the web
app), and Magick.NET's `Notice.txt` as `Magick.NET-Notice.txt` (ImageMagick and
the native libraries bundled in it). The Windows distribution
(`scripts/Publish-Windows.ps1`, and the MSI built from it) carries this file and
`LICENSE` in its install folder, next to `MangaPixer.Tray.exe`.

Entry format: `name` - version - license (SPDX where one exists) - upstream URL.

**Inventory basis** - MangaPixer 1.12.0, commit `8c08627`, generated 2026-09-14
from tool output (not written by hand):

| Ecosystem | Source | Count |
|---|---|---|
| NuGet, all | `dotnet list MangaPixer.slnx package --include-transitive --format json` | 104 package versions (103 IDs; `System.Security.Cryptography.Pkcs` resolves at two versions) |
| NuGet, shipped | `"type": "package"` entries in the `*.deps.json` of a Release `dotnet publish` of `MangaPixer.Server` + `MangaPixer.MediaWorker` (what `deploy/Dockerfile` copies) | 79 |
| NuGet, build/test only | all minus shipped | 25 |
| npm, runtime | `npm --prefix web ls --omit=dev --all` (`--prod` is a deprecated alias) | 19 |
| npm, all | `npm --prefix web ls --all` | 880 unique name@version (959 installed paths) |
| npm, build/test only | all minus runtime | 861 |

License values come from each package's own metadata: the `<license>` element
of the `.nuspec` in the NuGet cache, and the `license` field of
`web/node_modules/<pkg>/package.json`. Container facts were read from the
1.12.0 release image with `dpkg-query`.

## Review before publishing

<!-- TODO(owner): resolve or accept each item below before the repository or an image goes public. -->

These are the items that are copyleft, non-standard, missing an SPDX expression,
or where MangaPixer does not yet carry the notice that a license asks for.

1. **Weak-copyleft code inside Magick.NET's native library (shipped).**
   `Magick.NET-Q8-AnyCPU` 14.17.1 ships a single native binary per platform
   (`runtimes/<rid>/native/Magick.Native-Q8-*`). Its `Notice.txt` lists ImageMagick and
   36 bundled libraries, and 12 of them are LGPL- or MPL-licensed:
   cairo 1.18.4 (MPL-1.1), libcroco 0.6.13 (LGPL-2.0), libde265 1.1.1 (LGPL-3.0), fribidi 1.0.16 (LGPL-2.1-or-later), gdk-pixbuf 2.44.8 (LGPL-2.1-or-later), glib 2.64.3 (LGPL-2.1-or-later), libheif 1.23.2 (LGPL-3.0), liblqr 0.4.2 (LGPL-3.0), liblzma 5.8.3 (LGPL-2.1-or-later), pango 1.45.3 (LGPL-2.0), libraw 0.22.2 (LGPL-2.1), librsvg 2.40.20 (LGPL-2.0).
   Redistributing the image or installer means redistributing these. LGPL terms
   cover license text, notices, and the recipient's ability to replace or relink
   the library. Get a legal read on whether the upstream Magick.NET distribution
   already satisfies that, or whether MangaPixer has to ship extra material.
2. **Which license texts ship with the binaries.** MIT, BSD, Apache-2.0, OFL-1.1
   and LGPL all ask for the notice or license to go with binary copies.
   `deploy/Dockerfile` copies this file, `LICENSE`, the Angular build's
   `3rdpartylicenses.txt` and Magick.NET's `Notice.txt` into `/app/licenses/`
   (the build fails if `Notice.txt` cannot be found in the restored package).
   The Windows distribution ships this file and `LICENSE` only. Remaining gaps to
   accept or close: `dotnet publish` still copies no per-package NuGet license
   files (this file lists each package with its license and upstream URL
   instead), and the Windows distribution does not yet carry
   `3rdpartylicenses.txt` or Magick.NET's `Notice.txt`.
3. **`Microsoft.EntityFrameworkCore.Design` ships in the image.** It is design-time
   tooling (its nuspec sets `developmentDependency`), yet the server publish output
   contains it and 27 packages that come in only through it
   (Roslyn, MSBuild, Humanizer, Mono.TextTemplating, Newtonsoft.Json, System.Composition
   and more; see section 1b). All are MIT, so there is no license conflict, but they
   grow the image and the attribution list. Marking the reference
   `PrivateAssets="all"` would likely drop them. That is a code change, outside this lane.
4. **The Roboto font is OFL-1.1, not Apache-2.0.** `@fontsource/roboto` 5.3.0
   declares `OFL-1.1`, and its LICENSE is the SIL Open Font License 1.1. The
   `.woff`/`.woff2` files ship in the image under `wwwroot/media/`. OFL
   requires the copyright notice and license to go with the font files; see item 2.
5. **`SQLite` 3.53.4 (the shipped native `e_sqlite3`) has no SPDX expression.**
   Its nuspec points to a LICENSE.txt that says "SQLite is Public Domain"
   (https://sqlite.org/copyright.html). It is listed below as `blessing`, the SPDX ID
   for the SQLite dedication. No attribution is required.
6. **`xunit.abstractions` 2.0.3 (test only) has no SPDX expression.** Its nuspec has
   only a deprecated `licenseUrl`
   (https://raw.githubusercontent.com/xunit/xunit/master/license.txt), which is
   upstream xUnit's Apache-2.0 license. Not distributed.
7. **Build-only npm packages with non-mainstream licenses** (not distributed; for information):
   `@csstools/color-helpers` 6.1.1 (MIT-0), `@csstools/css-syntax-patches-for-csstree` 1.1.12 (MIT-0), `argparse` 2.0.1 (Python-2.0), `caniuse-lite` 1.0.30001810 (CC-BY-4.0), `lightningcss` 1.33.0 (MPL-2.0), `lightningcss-win32-x64-msvc` 1.33.0 (MPL-2.0), `lru-cache` 11.5.2 (BlueOak-1.0.0), `mdn-data` 2.27.1 (CC0-1.0), `minimatch` 10.2.6 (BlueOak-1.0.0), `sax` 1.6.1 (BlueOak-1.0.0).
   The only copyleft one is MPL-2.0 (`lightningcss`, file-level weak copyleft). It is
   a build-time CSS tool and none of its code reaches the bundle.
8. **The container image redistributes an Ubuntu 24.04 userland.** It includes
   GPL-licensed packages (bash, coreutils and others; see section 5). That is normal for
   a published image, but GPL terms on source availability apply to whoever
   distributes the image. `scripts/Package-Release.ps1` already produces an SPDX
   SBOM that lists every OS package.
9. **License metadata elsewhere in the repo (resolved).** `deploy/compose.yaml` and
   `deploy/compose.unraid.yaml` label the image
   `org.opencontainers.image.licenses: "MIT"`, and `Directory.Build.props` sets
   `<Authors>` and `<Copyright>` to match `LICENSE`, so the assemblies carry that
   copyright metadata. `<Company/>` stays empty.
10. **Inventory note:** `npm ls --all` exits with `ELSPROBLEMS` because two dev-tree
    packages are "invalid", meaning the installed version is outside a dependent's range:
    `chokidar@5.0.0` and `@noble/hashes@1.4.0`. This affects version ranges only,
    not licenses, and both are build-only.

## 1. .NET packages - shipped (79)

These are in the Release publish output of the server and/or the media worker.
The ASP.NET Core and .NET runtime assemblies themselves come from the base image
(section 5), not from NuGet.

### 1a. Application runtime (51)

- `Magick.NET-Q8-AnyCPU` - 14.17.1 - Apache-2.0 - https://github.com/dlemstra/Magick.NET
- `Magick.NET.Core` - 14.17.1 - Apache-2.0 - https://github.com/dlemstra/Magick.NET
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.AspNetCore.OpenApi` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Data.Sqlite.Core` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.EntityFrameworkCore.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.EntityFrameworkCore.Relational` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.EntityFrameworkCore.Sqlite.Core` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.EntityFrameworkCore.Sqlite` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.EntityFrameworkCore` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.Binder` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.CommandLine` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.EnvironmentVariables` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.FileExtensions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.Json` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration.UserSecrets` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Configuration` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.DependencyInjection.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.DependencyInjection` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.DependencyModel` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Diagnostics.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Diagnostics` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.FileProviders.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.FileProviders.Physical` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.FileSystemGlobbing` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Hosting.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Hosting` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.Configuration` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.Console` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.Debug` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.EventLog` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging.EventSource` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Logging` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Options.ConfigurationExtensions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Options` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Primitives` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.OpenApi` - 2.12.2 - MIT - https://github.com/microsoft/OpenAPI.NET
- `Serilog.Extensions.Hosting` - 10.0.0 - Apache-2.0 - https://github.com/serilog/serilog-extensions-hosting
- `Serilog.Extensions.Logging` - 10.0.0 - Apache-2.0 - https://github.com/serilog/serilog-extensions-logging
- `Serilog.Sinks.Console` - 6.1.1 - Apache-2.0 - https://github.com/serilog/serilog-sinks-console
- `Serilog.Sinks.File` - 7.0.0 - Apache-2.0 - https://github.com/serilog/serilog-sinks-file
- `Serilog` - 4.3.0 - Apache-2.0 - https://github.com/serilog/serilog
- `SharpCompress` - 0.50.4 - MIT - https://github.com/adamhathcock/sharpcompress
- `SQLite` - 3.53.4 - blessing (SQLite public-domain dedication; no SPDX expression in nuspec - see Review) - https://sqlite.org/
- `SQLitePCLRaw.bundle_e_sqlite3` - 3.0.5 - Apache-2.0 - https://github.com/ericsink/SQLitePCL.raw
- `SQLitePCLRaw.config.e_sqlite3` - 3.0.5 - Apache-2.0 - https://github.com/ericsink/SQLitePCL.raw
- `SQLitePCLRaw.core` - 3.0.5 - Apache-2.0 - https://github.com/ericsink/SQLitePCL.raw
- `SQLitePCLRaw.provider.e_sqlite3` - 3.0.5 - Apache-2.0 - https://github.com/ericsink/SQLitePCL.raw
- `System.Diagnostics.EventLog` - 10.0.0 - MIT - https://github.com/dotnet/dotnet

### 1b. Shipped only through `Microsoft.EntityFrameworkCore.Design` (28)

Design-time EF Core tooling and its dependency tree (see Review item 3). This set
is computed from the dependency graph in the published `MangaPixer.Server.deps.json`.

- `Humanizer.Core` - 2.14.1 - MIT - https://github.com/Humanizr/Humanizer
- `Microsoft.Build.Framework` - 17.14.28 - MIT - https://github.com/dotnet/msbuild
- `Microsoft.Build.Tasks.Core` - 17.14.28 - MIT - https://github.com/dotnet/msbuild
- `Microsoft.Build.Utilities.Core` - 17.14.28 - MIT - https://github.com/dotnet/msbuild
- `Microsoft.Build` - 17.7.2 - MIT - https://github.com/dotnet/msbuild
- `Microsoft.CodeAnalysis.Common` - 4.14.0 - MIT - https://github.com/dotnet/roslyn
- `Microsoft.CodeAnalysis.CSharp.Workspaces` - 4.14.0 - MIT - https://github.com/dotnet/roslyn
- `Microsoft.CodeAnalysis.CSharp` - 4.14.0 - MIT - https://github.com/dotnet/roslyn
- `Microsoft.CodeAnalysis.Workspaces.Common` - 4.14.0 - MIT - https://github.com/dotnet/roslyn
- `Microsoft.CodeAnalysis.Workspaces.MSBuild` - 4.14.0 - MIT - https://github.com/dotnet/roslyn
- `Microsoft.EntityFrameworkCore.Design` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.NET.StringTools` - 17.14.28 - MIT - https://github.com/dotnet/msbuild
- `Mono.TextTemplating` - 3.0.0 - MIT - https://github.com/mono/t4
- `Newtonsoft.Json` - 13.0.3 - MIT - https://github.com/JamesNK/Newtonsoft.Json
- `System.CodeDom` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition.AttributedModel` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition.Convention` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition.Hosting` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition.Runtime` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition.TypedParts` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Composition` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Configuration.ConfigurationManager` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Formats.Nrbf` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Reflection.MetadataLoadContext` - 7.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Resources.Extensions` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Security.Cryptography.ProtectedData` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Security.Permissions` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Windows.Extensions` - 9.0.0 - MIT - https://github.com/dotnet/runtime

### 1c. ImageMagick and the native libraries inside Magick.NET

Magick.NET (Apache-2.0, Copyright 2013-2026 Dirk Lemstra) wraps **ImageMagick**,
which is under the ImageMagick License, an Apache-2.0-derived license
(https://imagemagick.org/license/; Copyright 1999 ImageMagick Studio LLC).
The native `Magick.Native-Q8` library contains the components below. Full license
texts and copyright notices for all of them are in `Notice.txt` at the root of
the `Magick.NET-Q8-AnyCPU` 14.17.1 NuGet package. Versions and licenses below are
taken from that file. The notice gives upstream URLs only for ImageMagick and
FreeType; for the rest, the package's `Notice.txt` is the reference.

- `ImageMagick` - 7.1.2-31 - ImageMagick (Apache-2.0-derived) - https://imagemagick.org
- `aom` - 3.15.0 - BSD-2-Clause - see Notice.txt
- `brotli` - 1.2.0 - MIT - see Notice.txt
- `libbzip2` - 1.0.8 - bzip2-1.0.6 - see Notice.txt
- `cairo` - 1.18.4 - MPL-1.1 (text reproduced in the notice; upstream cairo is LGPL-2.1 OR MPL-1.1) - see Notice.txt
- `libcroco` - 0.6.13 - LGPL-2.0 (GNU Library GPL v2 text) - see Notice.txt
- `libde265` - 1.1.1 - LGPL-3.0 (library; sample applications MIT) - see Notice.txt
- `openexr` - 3.4.15 - BSD-3-Clause - see Notice.txt
- `libffi` - 3.8.0 - MIT - see Notice.txt
- `fontconfig` - 2.18.3 - MIT-style (fontconfig license) incl. Unicode data terms - see Notice.txt
- `freetype` - 2.14.3 - FTL - https://freetype.org
- `fribidi` - 1.0.16 - LGPL-2.1-or-later - see Notice.txt
- `gdk-pixbuf` - 2.44.8 - LGPL-2.1-or-later - see Notice.txt
- `glib` - 2.64.3 - LGPL-2.1-or-later - see Notice.txt
- `harfbuzz` - 14.4.0 - MIT-style ("Old MIT") - see Notice.txt
- `libheif` - 1.23.2 - LGPL-3.0 (library; samples and wrappers MIT) - see Notice.txt
- `libhwy` - 1.4.0 - Apache-2.0 OR BSD-3-Clause - see Notice.txt
- `imath` - 3.2.3 - BSD-3-Clause - see Notice.txt
- `libjpeg-turbo` - 3.2.0 - IJG AND BSD-3-Clause (its Zlib-licensed parts are subsumed, per the notice) - see Notice.txt
- `libjxl` - 0.12.0 - BSD-3-Clause - see Notice.txt
- `lcms` - 2.19.1 - MIT - see Notice.txt
- `liblqr` - 0.4.2 - LGPL-3.0 - see Notice.txt
- `liblzma` - 5.8.3 - LGPL-2.1-or-later (as reproduced in the notice) - see Notice.txt
- `openh264` - 2.6.0 - BSD-2-Clause - see Notice.txt
- `openjpeg` - 2.5.4 - BSD-2-Clause - see Notice.txt
- `openjph` - 0.31.0 - BSD-2-Clause - see Notice.txt
- `pango` - 1.45.3 - LGPL-2.0 (GNU Library GPL v2 text) - see Notice.txt
- `pixman` - 0.46.4 - MIT - see Notice.txt
- `libpng` - 1.6.58 - libpng-2.0 - see Notice.txt
- `libraqm` - 0.11.0 - MIT - see Notice.txt
- `libraw` - 0.22.2 - LGPL-2.1 (as reproduced in the notice) - see Notice.txt
- `librsvg` - 2.40.20 - LGPL-2.0 (GNU Library GPL v2 text) - see Notice.txt
- `libtiff` - 4.7.2 - libtiff - see Notice.txt
- `libwebp` - 1.6.0 - BSD-3-Clause - see Notice.txt
- `libxml2` - 2.15.3 - MIT - see Notice.txt
- `libzip` - 1.11.4 - BSD-3-Clause - see Notice.txt
- `zlib` - 1.3.2 - Zlib - see Notice.txt

### 1d. Copyright notices for shipped .NET packages

The MIT and BSD licenses ask that the copyright notice travel with copies. These lines
come from each package's nuspec `<copyright>` element. If a nuspec had none, the
upstream LICENSE applies, as noted.

- `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.AspNetCore.OpenApi`, `Microsoft.Build.Framework`, `Microsoft.Build.Tasks.Core`, `Microsoft.Build.Utilities.Core`, `Microsoft.Build`, `Microsoft.CodeAnalysis.Common`, `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Workspaces.Common`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Data.Sqlite.Core`, `Microsoft.EntityFrameworkCore.Abstractions`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.EntityFrameworkCore.Sqlite.Core`, `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Configuration.CommandLine`, `Microsoft.Extensions.Configuration.EnvironmentVariables`, `Microsoft.Extensions.Configuration.FileExtensions`, `Microsoft.Extensions.Configuration.Json`, `Microsoft.Extensions.Configuration.UserSecrets`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyModel`, `Microsoft.Extensions.Diagnostics.Abstractions`, `Microsoft.Extensions.Diagnostics`, `Microsoft.Extensions.FileProviders.Abstractions`, `Microsoft.Extensions.FileProviders.Physical`, `Microsoft.Extensions.FileSystemGlobbing`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Logging.Configuration`, `Microsoft.Extensions.Logging.Console`, `Microsoft.Extensions.Logging.Debug`, `Microsoft.Extensions.Logging.EventLog`, `Microsoft.Extensions.Logging.EventSource`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Primitives`, `Microsoft.NET.StringTools`, `Microsoft.OpenApi`, `System.CodeDom`, `System.Composition.AttributedModel`, `System.Composition.Convention`, `System.Composition.Hosting`, `System.Composition.Runtime`, `System.Composition.TypedParts`, `System.Composition`, `System.Configuration.ConfigurationManager`, `System.Diagnostics.EventLog`, `System.Formats.Nrbf`, `System.Reflection.MetadataLoadContext`, `System.Resources.Extensions`, `System.Security.Cryptography.ProtectedData`, `System.Security.Permissions`, `System.Windows.Extensions`  
  © Microsoft Corporation. All rights reserved.
- `SQLite`, `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.config.e_sqlite3`  
  Copyright 2014-2026 SourceGear, LLC
- `Magick.NET-Q8-AnyCPU`, `Magick.NET.Core`  
  Copyright 2013-2026 Dirk Lemstra
- `SQLitePCLRaw.core`, `SQLitePCLRaw.provider.e_sqlite3`  
  Copyright 2014-2025 SourceGear, LLC
- `Humanizer.Core`  
  Copyright (c) .NET Foundation and Contributors
- `Mono.TextTemplating`  
  Copyright (c) 2009-2011 Novell, Inc.; Copyright (c) 2011-2016 Xamarin Inc.; Copyright (c) Microsoft Corp. (from the package LICENSE; the nuspec has no copyright element)
- `Newtonsoft.Json`  
  Copyright © James Newton-King 2008
- `Serilog.Extensions.Hosting`  
  no copyright element in the nuspec - see upstream https://github.com/serilog/serilog-extensions-hosting
- `Serilog.Extensions.Logging`  
  no copyright element in the nuspec - see upstream https://github.com/serilog/serilog-extensions-logging
- `Serilog.Sinks.Console`  
  no copyright element in the nuspec - see upstream https://github.com/serilog/serilog-sinks-console
- `Serilog.Sinks.File`  
  no copyright element in the nuspec - see upstream https://github.com/serilog/serilog-sinks-file
- `Serilog`  
  Copyright © Serilog Contributors
- `SharpCompress`  
  Copyright (c) 2025 Adam Hathcock

The Apache-2.0 packages (Magick.NET, Serilog, SQLitePCLRaw) do not include a separate
`NOTICE` file in their NuGet packages. Magick.NET's `Notice.txt` is the
third-party notice described in 1c.

## 2. .NET packages - build and test only (25)

These are resolved for the test projects, or as analyzers and build tooling. None
of them is in the publish output. Some `Microsoft.Extensions.*` and
`Microsoft.AspNetCore.*` IDs appear here because `MangaPixer.Server.Tests`
resolves them as packages. At runtime, the server uses the copies in the ASP.NET Core
shared framework.

- `Microsoft.AspNetCore.Cryptography.Internal` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.AspNetCore.Cryptography.KeyDerivation` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.AspNetCore.Mvc.Testing` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.AspNetCore.TestHost` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.CodeAnalysis.Analyzers` - 3.11.0 - MIT - https://github.com/dotnet/roslyn-analyzers
- `Microsoft.CodeCoverage` - 17.14.0 - MIT - https://github.com/microsoft/vstest
- `Microsoft.EntityFrameworkCore.Analyzers` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Caching.Abstractions` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Caching.Memory` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Identity.Core` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.Extensions.Identity.Stores` - 10.0.0 - MIT - https://github.com/dotnet/dotnet
- `Microsoft.NET.Test.Sdk` - 17.14.0 - MIT - https://github.com/microsoft/vstest
- `Microsoft.TestPlatform.ObjectModel` - 17.14.0 - MIT - https://github.com/microsoft/vstest
- `Microsoft.TestPlatform.TestHost` - 17.14.0 - MIT - https://github.com/microsoft/vstest
- `System.Security.Cryptography.Pkcs` - 10.0.11 - MIT - https://github.com/dotnet/dotnet
- `System.Security.Cryptography.Pkcs` - 9.0.0 - MIT - https://github.com/dotnet/runtime
- `System.Security.Cryptography.Xml` - 10.0.11 - MIT - https://github.com/dotnet/dotnet
- `xunit.abstractions` - 2.0.3 - Apache-2.0 (nuspec has only a licenseUrl - see Review) - https://github.com/xunit/xunit
- `xunit.analyzers` - 1.18.0 - Apache-2.0 - https://github.com/xunit/xunit.analyzers
- `xunit.assert` - 2.9.3 - Apache-2.0 - https://github.com/xunit/xunit
- `xunit.core` - 2.9.3 - Apache-2.0 - https://github.com/xunit/xunit
- `xunit.extensibility.core` - 2.9.3 - Apache-2.0 - https://github.com/xunit/xunit
- `xunit.extensibility.execution` - 2.9.3 - Apache-2.0 - https://github.com/xunit/xunit
- `xunit.runner.visualstudio` - 3.1.0 - Apache-2.0 - https://github.com/xunit/visualstudio.xunit
- `xunit` - 2.9.3 - Apache-2.0 - https://github.com/xunit/xunit

## 3. Web runtime packages - npm (19)

This is the production dependency tree of `web/`. Packages marked *(bundled)* are
compiled into the shipped SPA; they are the ones Angular lists in its extracted
`3rdpartylicenses.txt`. The rest are in the production tree but tree-shaken out
of the bundle, or used only at compile time.

- `@angular/animations` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@angular/cdk` - 22.1.5 - MIT - https://github.com/angular/components *(bundled)*
- `@angular/common` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@angular/compiler` - 22.1.5 - MIT - https://github.com/angular/angular
- `@angular/core` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@angular/forms` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@angular/material` - 22.1.5 - MIT - https://github.com/angular/components *(bundled)*
- `@angular/platform-browser` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@angular/platform-browser-dynamic` - 22.1.5 - MIT - https://github.com/angular/angular
- `@angular/router` - 22.1.5 - MIT - https://github.com/angular/angular *(bundled)*
- `@fontsource/roboto` - 5.3.0 - OFL-1.1 - https://github.com/fontsource/font-files *(bundled)*
- `@standard-schema/spec` - 1.1.0 - MIT - https://github.com/standard-schema/standard-schema
- `entities` - 8.0.0 - BSD-2-Clause - https://github.com/fb55/entities
- `material-icons` - 1.13.14 - Apache-2.0 - https://github.com/marella/material-icons *(bundled)*
- `parse5` - 8.0.1 - MIT - https://github.com/inikulin/parse5
- `rxjs` - 7.8.2 - Apache-2.0 - https://github.com/reactivex/rxjs *(bundled)*
- `tslib` - 2.8.1 - 0BSD - https://github.com/microsoft/tslib *(bundled)*
- `zod` - 4.4.3 - MIT - https://github.com/colinhacks/zod
- `zone.js` - 0.15.1 - MIT - https://github.com/angular/angular *(bundled)*

Copyright notices for these packages (from their LICENSE files):

- Copyright (c) 2010-2026 Google LLC - Angular framework packages (`@angular/*`, MIT);
  Copyright (c) 2026 Google LLC - `@angular/cdk`, `@angular/material`;
  Copyright (c) 2010-2025 Google LLC - `zone.js`
- Copyright (c) Microsoft Corporation - `tslib` (0BSD); `rxjs` is Apache-2.0 with no NOTICE file
- Copyright (c) 2013-2019 Ivan Nikulin - `parse5` (MIT); Copyright (c) Felix Böhm - `entities` (BSD-2-Clause)
- Copyright (c) 2025 Colin McDonnell - `zod` (MIT); Copyright (c) 2024 Colin McDonnell - `@standard-schema/spec` (MIT)

### Fonts and icons

- **Roboto** (via `@fontsource/roboto` 5.3.0): Copyright 2011 The Roboto Project Authors
  (https://github.com/googlefonts/roboto-classic). Licensed under the SIL Open Font
  License, Version 1.1 (`OFL-1.1`, https://openfontlicense.org). Weights 400, 500 and 700
  are bundled as `.woff`/`.woff2` files. The font is served by the MangaPixer
  server itself, never from a remote font CDN.
- **Material Icons** (via `material-icons` 1.13.14): the Material Design icons are
  created by Google and distributed under the Apache License 2.0 (`Apache-2.0`,
  https://github.com/google/material-design-icons). The package is maintained at
  https://github.com/marella/material-icons. The icon fonts ship as
  `.woff`/`.woff2` in the SPA.

## 4. Web build and test packages - npm (861)

These are dev dependencies and their transitive trees: the Angular CLI and build
toolchain, TypeScript, ESLint, Vitest, jsdom and Playwright. None is in the shipped
SPA. The inventory reflects a Windows x64 install; platform-specific optional
binaries such as `lightningcss-*`, `@esbuild/*` and `@rollup/*` differ on other
operating systems, including the Linux image build stage.

- `@ampproject/remapping` - 2.3.0 - Apache-2.0 - https://github.com/ampproject/remapping
- `@angular-devkit/architect` - 0.1902.27 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/architect` - 0.2201.7 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/build-angular` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/build-webpack` - 0.2201.7 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/core` - 19.2.27 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/core` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/schematics` - 19.2.27 - MIT - https://github.com/angular/angular-cli
- `@angular-devkit/schematics` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@angular-eslint/builder` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/bundled-angular-compiler` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/eslint-plugin` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/eslint-plugin-template` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/schematics` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/template-parser` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular-eslint/utils` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `@angular/build` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@angular/cli` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@angular/compiler-cli` - 22.1.5 - MIT - https://github.com/angular/angular
- `@asamuzakjp/css-color` - 6.0.7 - MIT - https://github.com/asamuzaK/cssColor
- `@asamuzakjp/dom-selector` - 8.3.2 - MIT - https://github.com/asamuzaK/domSelector
- `@babel/code-frame` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/code-frame` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/compat-data` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/compat-data` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/core` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/core` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/generator` - 7.29.8 - MIT - https://github.com/babel/babel
- `@babel/generator` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-annotate-as-pure` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-compilation-targets` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-compilation-targets` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-create-class-features-plugin` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-create-regexp-features-plugin` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-define-polyfill-provider` - 1.0.0 - MIT - https://github.com/babel/babel-polyfills
- `@babel/helper-globals` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-globals` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-member-expression-to-functions` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-module-imports` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-module-imports` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-module-transforms` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-module-transforms` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-optimise-call-expression` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-plugin-utils` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-remap-async-to-generator` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-replace-supers` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/helper-skip-transparent-expression-wrappers` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-split-export-declaration` - 7.24.7 - MIT - https://github.com/babel/babel
- `@babel/helper-string-parser` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-string-parser` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-validator-identifier` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-validator-identifier` - 8.0.4 - MIT - https://github.com/babel/babel
- `@babel/helper-validator-option` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helper-validator-option` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helper-wrap-function` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/helpers` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/helpers` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/parser` - 7.29.8 - MIT - https://github.com/babel/babel
- `@babel/parser` - 8.0.4 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-firefox-class-in-computed-class-key` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-safari-class-field-initializer-scope` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-safari-id-destructuring-collision-in-function-expression` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-safari-rest-destructuring-rhs-array` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-v8-spread-parameters-in-optional-chaining` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-bugfix-v8-static-class-fields-redefine-readonly` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-arrow-functions` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-async-generator-functions` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-async-to-generator` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-block-scoped-functions` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-block-scoping` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-class-properties` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-class-static-block` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-classes` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-computed-properties` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-destructuring` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-dotall-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-duplicate-keys` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-duplicate-named-capturing-groups-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-dynamic-import` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-explicit-resource-management` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-exponentiation-operator` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-export-namespace-from` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-for-of` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-function-name` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-json-strings` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-literals` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-logical-assignment-operators` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-member-expression-literals` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-modules-amd` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-modules-commonjs` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-modules-systemjs` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-modules-umd` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-named-capturing-groups-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-new-target` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-nullish-coalescing-operator` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-numeric-separator` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-object-rest-spread` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-object-super` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-optional-catch-binding` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-optional-chaining` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-parameters` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-private-methods` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-private-property-in-object` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-property-literals` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-regenerator` - 8.0.2 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-regexp-modifiers` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-reserved-words` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-runtime` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-shorthand-properties` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-spread` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-sticky-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-template-literals` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-typeof-symbol` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-unicode-escapes` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-unicode-property-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-unicode-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/plugin-transform-unicode-sets-regex` - 8.0.1 - MIT - https://github.com/babel/babel
- `@babel/preset-env` - 8.0.2 - MIT - https://github.com/babel/babel
- `@babel/preset-modules` - 0.2.0 - MIT - https://github.com/babel/preset-modules
- `@babel/runtime` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/template` - 7.29.7 - MIT - https://github.com/babel/babel
- `@babel/template` - 8.0.0 - MIT - https://github.com/babel/babel
- `@babel/traverse` - 7.29.8 - MIT - https://github.com/babel/babel
- `@babel/traverse` - 8.0.4 - MIT - https://github.com/babel/babel
- `@babel/types` - 7.29.8 - MIT - https://github.com/babel/babel
- `@babel/types` - 8.0.4 - MIT - https://github.com/babel/babel
- `@bramus/specificity` - 2.4.2 - MIT - https://github.com/bramus/specificity
- `@csstools/color-helpers` - 6.1.1 - MIT-0 - https://github.com/csstools/postcss-plugins
- `@csstools/css-calc` - 3.3.0 - MIT - https://github.com/csstools/postcss-plugins
- `@csstools/css-color-parser` - 4.2.2 - MIT - https://github.com/csstools/postcss-plugins
- `@csstools/css-parser-algorithms` - 4.0.0 - MIT - https://github.com/csstools/postcss-plugins
- `@csstools/css-syntax-patches-for-csstree` - 1.1.12 - MIT-0 - https://github.com/csstools/postcss-plugins
- `@csstools/css-tokenizer` - 4.0.0 - MIT - https://github.com/csstools/postcss-plugins
- `@discoveryjs/json-ext` - 1.1.0 - MIT - https://github.com/discoveryjs/json-ext
- `@esbuild/win32-x64` - 0.28.2 - MIT - https://github.com/evanw/esbuild
- `@eslint-community/eslint-utils` - 4.10.1 - MIT - https://github.com/eslint-community/eslint-utils
- `@eslint-community/regexpp` - 4.12.2 - MIT - https://github.com/eslint-community/regexpp
- `@eslint/config-array` - 0.21.2 - Apache-2.0 - https://github.com/eslint/rewrite
- `@eslint/config-helpers` - 0.4.2 - Apache-2.0 - https://github.com/eslint/rewrite
- `@eslint/core` - 0.17.0 - Apache-2.0 - https://github.com/eslint/rewrite
- `@eslint/eslintrc` - 3.3.7 - MIT - https://github.com/eslint/eslintrc
- `@eslint/js` - 9.39.5 - MIT - https://github.com/eslint/eslint
- `@eslint/object-schema` - 2.1.7 - Apache-2.0 - https://github.com/eslint/rewrite
- `@eslint/plugin-kit` - 0.4.1 - Apache-2.0 - https://github.com/eslint/rewrite
- `@exodus/bytes` - 1.15.1 - MIT - https://github.com/ExodusOSS/bytes
- `@harperfast/extended-iterable` - 1.0.3 - Apache-2.0 - https://github.com/harperdb/extended-iterable
- `@hono/node-server` - 2.1.1 - MIT - https://github.com/honojs/node-server
- `@humanfs/core` - 0.19.2 - Apache-2.0 - https://github.com/humanwhocodes/humanfs
- `@humanfs/node` - 0.16.8 - Apache-2.0 - https://github.com/humanwhocodes/humanfs
- `@humanfs/types` - 0.15.0 - Apache-2.0 - https://github.com/humanwhocodes/humanfs
- `@humanwhocodes/module-importer` - 1.0.1 - Apache-2.0 - https://github.com/humanwhocodes/module-importer
- `@humanwhocodes/retry` - 0.4.3 - Apache-2.0 - https://github.com/humanwhocodes/retry
- `@inquirer/ansi` - 2.0.8 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/checkbox` - 5.2.4 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/confirm` - 6.1.1 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/core` - 11.2.1 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/core` - 12.0.2 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/editor` - 5.3.2 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/expand` - 5.1.4 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/external-editor` - 3.0.5 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/figures` - 2.0.9 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/input` - 5.1.5 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/number` - 4.2.2 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/password` - 5.2.1 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/prompts` - 8.5.2 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/rawlist` - 5.3.4 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/search` - 4.3.2 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/select` - 5.2.4 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@inquirer/type` - 4.1.1 - MIT - https://github.com/SBoudrias/Inquirer.js
- `@istanbuljs/schema` - 0.1.6 - MIT - https://github.com/istanbuljs/schema
- `@jridgewell/gen-mapping` - 0.3.13 - MIT - https://github.com/jridgewell/sourcemaps
- `@jridgewell/remapping` - 2.3.5 - MIT - https://github.com/jridgewell/sourcemaps
- `@jridgewell/resolve-uri` - 3.1.2 - MIT - https://github.com/jridgewell/resolve-uri
- `@jridgewell/source-map` - 0.3.11 - MIT - https://github.com/jridgewell/sourcemaps
- `@jridgewell/sourcemap-codec` - 1.6.0 - MIT - https://github.com/jridgewell/sourcemaps
- `@jridgewell/trace-mapping` - 0.3.31 - MIT - https://github.com/jridgewell/sourcemaps
- `@jsonjoy.com/base64` - 1.1.2 - Apache-2.0 - https://github.com/jsonjoy-com/base64
- `@jsonjoy.com/base64` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/base64
- `@jsonjoy.com/buffers` - 1.2.1 - Apache-2.0 - https://github.com/jsonjoy-com/buffers
- `@jsonjoy.com/buffers` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/buffers
- `@jsonjoy.com/codegen` - 1.0.0 - Apache-2.0 - https://github.com/jsonjoy-com/codegen
- `@jsonjoy.com/codegen` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/codegen
- `@jsonjoy.com/fs-core` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-fsa` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-node` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-node-builtins` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-node-to-fsa` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-node-utils` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-print` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/fs-snapshot` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `@jsonjoy.com/json-pack` - 1.21.0 - Apache-2.0 - https://github.com/jsonjoy-com/json-pack
- `@jsonjoy.com/json-pack` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/json-pack
- `@jsonjoy.com/json-pointer` - 1.0.2 - Apache-2.0 - https://github.com/jsonjoy-com/json-pointer
- `@jsonjoy.com/json-pointer` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/json-pointer
- `@jsonjoy.com/util` - 1.9.0 - Apache-2.0 - https://github.com/jsonjoy-com/util
- `@jsonjoy.com/util` - 17.67.0 - Apache-2.0 - https://github.com/jsonjoy-com/util
- `@leichtgewicht/ip-codec` - 2.0.5 - MIT - https://github.com/martinheidegger/ip-codec
- `@listr2/prompt-adapter-inquirer` - 4.2.5 - MIT - https://github.com/listr2/listr2
- `@lmdb/lmdb-win32-x64` - 3.5.6 - MIT - https://github.com/kriszyp/lmdb-js
- `@modelcontextprotocol/sdk` - 1.30.0 - MIT - https://github.com/modelcontextprotocol/typescript-sdk
- `@msgpackr-extract/msgpackr-extract-win32-x64` - 3.0.4 - MIT - https://github.com/kriszyp/msgpackr-extract
- `@napi-rs/nice` - 1.1.1 - MIT - https://github.com/Brooooooklyn/nice
- `@napi-rs/nice-win32-x64-msvc` - 1.1.1 - MIT - https://github.com/Brooooooklyn/nice
- `@ngtools/webpack` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@noble/hashes` - 1.4.0 - MIT - https://github.com/paulmillr/noble-hashes
- `@oxc-parser/binding-win32-x64-msvc` - 0.142.0 - MIT - https://github.com/oxc-project/oxc
- `@oxc-project/types` - 0.139.0 - MIT - https://github.com/oxc-project/oxc
- `@oxc-project/types` - 0.140.0 - MIT - https://github.com/oxc-project/oxc
- `@oxc-project/types` - 0.142.0 - MIT - https://github.com/oxc-project/oxc
- `@parcel/watcher` - 2.6.0 - MIT - https://github.com/parcel-bundler/watcher
- `@parcel/watcher-win32-x64` - 2.6.0 - MIT - https://github.com/parcel-bundler/watcher
- `@peculiar/asn1-cms` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-csr` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-ecc` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-pfx` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-pkcs8` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-pkcs9` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-rsa` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-schema` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-x509` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/asn1-x509-attr` - 2.9.4 - MIT - https://github.com/PeculiarVentures/asn1-schema
- `@peculiar/utils` - 2.0.3 - MIT - https://github.com/PeculiarVentures/pvtsutils
- `@peculiar/x509` - 1.14.3 - MIT - https://github.com/PeculiarVentures/x509
- `@playwright/test` - 1.63.0 - Apache-2.0 - https://github.com/microsoft/playwright
- `@rolldown/binding-win32-x64-msvc` - 1.1.5 - MIT - https://github.com/rolldown/rolldown
- `@rolldown/binding-win32-x64-msvc` - 1.2.0 - MIT - https://github.com/rolldown/rolldown
- `@rolldown/pluginutils` - 1.0.1 - MIT - https://github.com/rolldown/plugins
- `@schematics/angular` - 22.1.7 - MIT - https://github.com/angular/angular-cli
- `@types/body-parser` - 1.19.6 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/bonjour` - 3.5.13 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/chai` - 5.2.3 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/connect` - 3.4.38 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/connect-history-api-fallback` - 1.5.4 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/deep-eql` - 4.0.2 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/estree` - 1.0.9 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/express` - 4.17.25 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/express-serve-static-core` - 4.19.9 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/gensync` - 1.0.5 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/http-errors` - 2.0.5 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/http-proxy` - 1.17.17 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/jsesc` - 2.5.1 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/json-schema` - 7.0.15 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/less` - 3.0.8 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/mime` - 1.3.5 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/node` - 24.13.3 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/qs` - 6.15.1 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/range-parser` - 1.2.7 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/retry` - 0.12.2 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/send` - 0.17.6 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/send` - 1.2.1 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/serve-index` - 1.9.4 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/serve-static` - 1.15.10 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/sockjs` - 0.3.36 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@types/ws` - 8.18.1 - MIT - https://github.com/DefinitelyTyped/DefinitelyTyped
- `@typescript-eslint/eslint-plugin` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/parser` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/project-service` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/scope-manager` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/tsconfig-utils` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/type-utils` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/types` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/typescript-estree` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/utils` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@typescript-eslint/visitor-keys` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `@vitejs/plugin-basic-ssl` - 2.3.0 - MIT - https://github.com/vitejs/vite-plugin-basic-ssl
- `@vitest/expect` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/mocker` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/pretty-format` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/runner` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/snapshot` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/spy` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@vitest/utils` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `@webassemblyjs/ast` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/floating-point-hex-parser` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/helper-api-error` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/helper-buffer` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/helper-numbers` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/helper-wasm-bytecode` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/helper-wasm-section` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/ieee754` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/leb128` - 1.13.2 - Apache-2.0 - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/utf8` - 1.13.2 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/wasm-edit` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/wasm-gen` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/wasm-opt` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/wasm-parser` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@webassemblyjs/wast-printer` - 1.14.1 - MIT - https://github.com/xtuc/webassemblyjs
- `@xtuc/ieee754` - 1.2.0 - BSD-3-Clause - https://github.com/feross/ieee754
- `@xtuc/long` - 4.2.2 - Apache-2.0 - https://github.com/dcodeIO/long.js
- `accepts` - 1.3.8 - MIT - https://github.com/jshttp/accepts
- `accepts` - 2.0.0 - MIT - https://github.com/jshttp/accepts
- `acorn` - 8.18.0 - MIT - https://github.com/acornjs/acorn
- `acorn-jsx` - 5.3.2 - MIT - https://github.com/acornjs/acorn-jsx
- `adjust-sourcemap-loader` - 4.0.0 - MIT - https://github.com/bholloway/adjust-sourcemap-loader
- `agent-base` - 9.0.0 - MIT - https://github.com/TooTallNate/proxy-agents
- `ajv` - 6.15.0 - MIT - https://github.com/ajv-validator/ajv
- `ajv` - 8.18.0 - MIT - https://github.com/ajv-validator/ajv
- `ajv` - 8.20.0 - MIT - https://github.com/ajv-validator/ajv
- `ajv-formats` - 2.1.1 - MIT - https://github.com/ajv-validator/ajv-formats
- `ajv-formats` - 3.0.1 - MIT - https://github.com/ajv-validator/ajv-formats
- `ajv-keywords` - 5.1.0 - MIT - https://github.com/epoberezkin/ajv-keywords
- `angular-eslint` - 19.8.1 - MIT - https://github.com/angular-eslint/angular-eslint
- `ansi-colors` - 4.1.3 - MIT - https://github.com/doowb/ansi-colors
- `ansi-escapes` - 7.3.0 - MIT - https://github.com/sindresorhus/ansi-escapes
- `ansi-html-community` - 0.0.8 - Apache-2.0 - https://github.com/mahdyar/ansi-html-community
- `ansi-regex` - 5.0.1 - MIT - https://github.com/chalk/ansi-regex
- `ansi-regex` - 6.3.0 - MIT - https://github.com/chalk/ansi-regex
- `ansi-styles` - 4.3.0 - MIT - https://github.com/chalk/ansi-styles
- `ansi-styles` - 6.2.3 - MIT - https://github.com/chalk/ansi-styles
- `anymatch` - 3.1.3 - ISC - https://github.com/micromatch/anymatch
- `argparse` - 2.0.1 - Python-2.0 - https://github.com/nodeca/argparse
- `aria-query` - 5.3.2 - Apache-2.0 - https://github.com/A11yance/aria-query
- `array-flatten` - 1.1.1 - MIT - https://github.com/blakeembrey/array-flatten
- `asn1js` - 3.0.10 - BSD-3-Clause - https://github.com/PeculiarVentures/ASN1.js
- `assertion-error` - 2.0.1 - MIT - https://github.com/chaijs/assertion-error
- `autoprefixer` - 10.5.4 - MIT - https://github.com/postcss/autoprefixer
- `axobject-query` - 4.1.0 - Apache-2.0 - https://github.com/A11yance/axobject-query
- `babel-loader` - 10.1.1 - MIT - https://github.com/babel/babel-loader
- `babel-plugin-polyfill-corejs3` - 1.0.0 - MIT - https://github.com/babel/babel-polyfills
- `balanced-match` - 1.0.2 - MIT - https://github.com/juliangruber/balanced-match
- `balanced-match` - 4.0.4 - MIT - https://github.com/juliangruber/balanced-match
- `base64-js` - 1.5.1 - MIT - https://github.com/beatgammit/base64-js
- `baseline-browser-mapping` - 2.11.21 - Apache-2.0 - https://github.com/web-platform-dx/baseline-browser-mapping
- `batch` - 0.6.1 - MIT - https://github.com/visionmedia/batch
- `beasties` - 0.4.3 - Apache-2.0 - https://github.com/danielroe/beasties
- `bidi-js` - 1.1.0 - MIT - https://github.com/lojjic/bidi-js
- `big.js` - 5.2.2 - MIT - https://github.com/MikeMcl/big.js
- `binary-extensions` - 2.3.0 - MIT - https://github.com/sindresorhus/binary-extensions
- `bl` - 4.1.0 - MIT - https://github.com/rvagg/bl
- `body-parser` - 1.20.6 - MIT - https://github.com/expressjs/body-parser
- `body-parser` - 2.3.0 - MIT - https://github.com/expressjs/body-parser
- `bonjour-service` - 1.4.4 - MIT - https://github.com/onlxltd/bonjour-service
- `boolbase` - 1.0.0 - ISC - https://github.com/fb55/boolbase
- `brace-expansion` - 1.1.18 - MIT - https://github.com/juliangruber/brace-expansion
- `brace-expansion` - 5.0.9 - MIT - https://github.com/juliangruber/brace-expansion
- `braces` - 3.0.3 - MIT - https://github.com/micromatch/braces
- `browserslist` - 4.28.9 - MIT - https://github.com/browserslist/browserslist
- `buffer` - 5.7.1 - MIT - https://github.com/feross/buffer
- `buffer-from` - 1.1.2 - MIT - https://github.com/LinusU/buffer-from
- `bundle-name` - 4.1.0 - MIT - https://github.com/sindresorhus/bundle-name
- `bytes` - 3.1.2 - MIT - https://github.com/visionmedia/bytes.js
- `bytestreamjs` - 2.0.1 - BSD-3-Clause - https://github.com/PeculiarVentures/ByteStream.js
- `call-bind-apply-helpers` - 1.0.2 - MIT - https://github.com/ljharb/call-bind-apply-helpers
- `call-bound` - 1.0.4 - MIT - https://github.com/ljharb/call-bound
- `callsites` - 3.1.0 - MIT - https://github.com/sindresorhus/callsites
- `caniuse-lite` - 1.0.30001810 - CC-BY-4.0 - https://github.com/browserslist/caniuse-lite
- `chai` - 6.2.2 - MIT - https://github.com/chaijs/chai
- `chalk` - 4.1.2 - MIT - https://github.com/chalk/chalk
- `chalk` - 5.6.2 - MIT - https://github.com/chalk/chalk
- `chardet` - 2.2.0 - MIT - https://github.com/runk/node-chardet
- `chokidar` - 3.6.0 - MIT - https://github.com/paulmillr/chokidar
- `chokidar` - 5.0.0 - MIT - https://github.com/paulmillr/chokidar
- `chrome-trace-event` - 1.0.4 - MIT - https://github.com/samccone/chrome-trace-event
- `cli-cursor` - 3.1.0 - MIT - https://github.com/sindresorhus/cli-cursor
- `cli-cursor` - 5.0.0 - MIT - https://github.com/sindresorhus/cli-cursor
- `cli-spinners` - 2.9.2 - MIT - https://github.com/sindresorhus/cli-spinners
- `cli-spinners` - 3.4.0 - MIT - https://github.com/sindresorhus/cli-spinners
- `cli-truncate` - 6.1.1 - MIT - https://github.com/sindresorhus/cli-truncate
- `cli-width` - 4.1.0 - ISC - https://github.com/knownasilya/cli-width
- `cliui` - 9.0.1 - ISC - https://github.com/yargs/cliui
- `clone` - 1.0.4 - MIT - https://github.com/pvorb/node-clone
- `clone-deep` - 4.0.1 - MIT - https://github.com/jonschlinkert/clone-deep
- `color-convert` - 2.0.1 - MIT - https://github.com/Qix-/color-convert
- `color-name` - 1.1.4 - MIT - https://github.com/colorjs/color-name
- `colorette` - 2.0.20 - MIT - https://github.com/jorgebucaran/colorette
- `commander` - 2.20.3 - MIT - https://github.com/tj/commander.js
- `compressible` - 2.0.18 - MIT - https://github.com/jshttp/compressible
- `compression` - 1.8.1 - MIT - https://github.com/expressjs/compression
- `concat-map` - 0.0.1 - MIT - https://github.com/substack/node-concat-map
- `connect-history-api-fallback` - 2.0.0 - MIT - https://github.com/bripkens/connect-history-api-fallback
- `content-disposition` - 0.5.4 - MIT - https://github.com/jshttp/content-disposition
- `content-disposition` - 1.1.0 - MIT - https://github.com/jshttp/content-disposition
- `content-type` - 1.0.5 - MIT - https://github.com/jshttp/content-type
- `content-type` - 2.1.0 - MIT - https://github.com/jshttp/content-type
- `convert-source-map` - 1.9.0 - MIT - https://github.com/thlorenz/convert-source-map
- `convert-source-map` - 2.0.0 - MIT - https://github.com/thlorenz/convert-source-map
- `cookie` - 0.7.2 - MIT - https://github.com/jshttp/cookie
- `cookie-signature` - 1.0.7 - MIT - https://github.com/visionmedia/node-cookie-signature
- `cookie-signature` - 1.2.2 - MIT - https://github.com/visionmedia/node-cookie-signature
- `copy-anything` - 3.0.5 - MIT - https://github.com/mesqueeb/copy-anything
- `copy-webpack-plugin` - 14.0.0 - MIT - https://github.com/webpack/copy-webpack-plugin
- `core-js-compat` - 3.50.0 - MIT - https://github.com/zloirock/core-js
- `core-util-is` - 1.0.3 - MIT - https://github.com/isaacs/core-util-is
- `cors` - 2.8.6 - MIT - https://github.com/expressjs/cors
- `cosmiconfig` - 9.0.2 - MIT - https://github.com/cosmiconfig/cosmiconfig
- `cross-spawn` - 7.0.6 - MIT - https://github.com/moxystudio/node-cross-spawn
- `css-loader` - 7.1.4 - MIT - https://github.com/webpack/css-loader
- `css-select` - 6.0.0 - BSD-2-Clause - https://github.com/fb55/css-select
- `css-tree` - 3.2.1 - MIT - https://github.com/csstree/csstree
- `css-what` - 7.0.0 - BSD-2-Clause - https://github.com/fb55/css-what
- `cssesc` - 3.0.0 - MIT - https://github.com/mathiasbynens/cssesc
- `data-urls` - 7.0.0 - MIT - https://github.com/jsdom/data-urls
- `debug` - 2.6.9 - MIT - https://github.com/visionmedia/debug
- `debug` - 3.2.7 - MIT - https://github.com/visionmedia/debug
- `debug` - 4.4.3 - MIT - https://github.com/debug-js/debug
- `decimal.js` - 10.6.0 - MIT - https://github.com/MikeMcl/decimal.js
- `deep-is` - 0.1.4 - MIT - https://github.com/thlorenz/deep-is
- `default-browser` - 5.5.1 - MIT - https://github.com/sindresorhus/default-browser
- `default-browser-id` - 5.0.1 - MIT - https://github.com/sindresorhus/default-browser-id
- `defaults` - 1.0.4 - MIT - https://github.com/sindresorhus/node-defaults
- `define-lazy-prop` - 3.0.0 - MIT - https://github.com/sindresorhus/define-lazy-prop
- `depd` - 1.1.2 - MIT - https://github.com/dougwilson/nodejs-depd
- `depd` - 2.0.0 - MIT - https://github.com/dougwilson/nodejs-depd
- `destroy` - 1.2.0 - MIT - https://github.com/stream-utils/destroy
- `detect-libc` - 2.1.2 - Apache-2.0 - https://github.com/lovell/detect-libc
- `detect-node` - 2.1.0 - MIT - https://github.com/iliakan/detect-node
- `dns-packet` - 5.6.1 - MIT - https://github.com/mafintosh/dns-packet
- `dom-serializer` - 2.0.0 - MIT - https://github.com/cheeriojs/dom-serializer
- `domelementtype` - 2.3.0 - BSD-2-Clause - https://github.com/fb55/domelementtype
- `domhandler` - 5.0.3 - BSD-2-Clause - https://github.com/fb55/domhandler
- `domutils` - 3.2.2 - BSD-2-Clause - https://github.com/fb55/domutils
- `dunder-proto` - 1.0.1 - MIT - https://github.com/es-shims/dunder-proto
- `ee-first` - 1.1.1 - MIT - https://github.com/jonathanong/ee-first
- `electron-to-chromium` - 1.5.422 - ISC - https://github.com/Kilian/electron-to-chromium
- `emoji-regex` - 10.6.0 - MIT - https://github.com/mathiasbynens/emoji-regex
- `emojis-list` - 3.0.0 - MIT - https://github.com/kikobeats/emojis-list
- `empathic` - 2.0.1 - MIT - https://github.com/lukeed/empathic
- `encodeurl` - 2.0.0 - MIT - https://github.com/pillarjs/encodeurl
- `enhanced-resolve` - 5.24.5 - MIT - https://github.com/webpack/enhanced-resolve
- `entities` - 4.5.0 - BSD-2-Clause - https://github.com/fb55/entities
- `entities` - 7.0.1 - BSD-2-Clause - https://github.com/fb55/entities
- `env-paths` - 2.2.1 - MIT - https://github.com/sindresorhus/env-paths
- `environment` - 1.1.0 - MIT - https://github.com/sindresorhus/environment
- `errno` - 0.1.8 - MIT - https://github.com/rvagg/node-errno
- `error-ex` - 1.3.4 - MIT - https://github.com/qix-/node-error-ex
- `es-define-property` - 1.0.1 - MIT - https://github.com/ljharb/es-define-property
- `es-errors` - 1.3.0 - MIT - https://github.com/ljharb/es-errors
- `es-module-lexer` - 2.3.2 - MIT - https://github.com/guybedford/es-module-lexer
- `es-object-atoms` - 1.1.2 - MIT - https://github.com/ljharb/es-object-atoms
- `esbuild` - 0.28.2 - MIT - https://github.com/evanw/esbuild
- `esbuild-wasm` - 0.28.2 - MIT - https://github.com/evanw/esbuild
- `escalade` - 3.2.0 - MIT - https://github.com/lukeed/escalade
- `escape-html` - 1.0.3 - MIT - https://github.com/component/escape-html
- `escape-string-regexp` - 4.0.0 - MIT - https://github.com/sindresorhus/escape-string-regexp
- `eslint` - 9.39.5 - MIT - https://github.com/eslint/eslint
- `eslint-scope` - 5.1.1 - BSD-2-Clause - https://github.com/eslint/eslint-scope
- `eslint-scope` - 8.4.0 - BSD-2-Clause - https://github.com/eslint/js
- `eslint-visitor-keys` - 3.4.3 - Apache-2.0 - https://github.com/eslint/eslint-visitor-keys
- `eslint-visitor-keys` - 4.2.1 - Apache-2.0 - https://github.com/eslint/js
- `eslint-visitor-keys` - 5.0.1 - Apache-2.0 - https://github.com/eslint/js
- `espree` - 10.4.0 - BSD-2-Clause - https://github.com/eslint/js
- `esquery` - 1.7.0 - BSD-3-Clause - https://github.com/estools/esquery
- `esrecurse` - 4.3.0 - BSD-2-Clause - https://github.com/estools/esrecurse
- `estraverse` - 4.3.0 - BSD-2-Clause - https://github.com/estools/estraverse
- `estraverse` - 5.3.0 - BSD-2-Clause - https://github.com/estools/estraverse
- `estree-walker` - 3.0.3 - MIT - https://github.com/Rich-Harris/estree-walker
- `esutils` - 2.0.3 - BSD-2-Clause - https://github.com/estools/esutils
- `etag` - 1.8.1 - MIT - https://github.com/jshttp/etag
- `eventemitter3` - 4.0.7 - MIT - https://github.com/primus/eventemitter3
- `events` - 3.3.0 - MIT - https://github.com/Gozala/events
- `eventsource` - 3.0.7 - MIT - https://github.com/EventSource/eventsource
- `eventsource-parser` - 3.1.1 - MIT - https://github.com/rexxars/eventsource-parser
- `expect-type` - 1.4.0 - Apache-2.0 - https://github.com/mmkal/expect-type
- `express` - 4.22.2 - MIT - https://github.com/expressjs/express
- `express` - 5.2.1 - MIT - https://github.com/expressjs/express
- `express-rate-limit` - 8.7.0 - MIT - https://github.com/express-rate-limit/express-rate-limit
- `fast-deep-equal` - 3.1.3 - MIT - https://github.com/epoberezkin/fast-deep-equal
- `fast-json-stable-stringify` - 2.1.0 - MIT - https://github.com/epoberezkin/fast-json-stable-stringify
- `fast-levenshtein` - 2.0.6 - MIT - https://github.com/hiddentao/fast-levenshtein
- `fast-string-truncated-width` - 3.0.3 - MIT - https://github.com/fabiospampinato/fast-string-truncated-width
- `fast-string-width` - 3.0.2 - MIT - https://github.com/fabiospampinato/fast-string-width
- `fast-uri` - 3.1.7 - BSD-3-Clause - https://github.com/fastify/fast-uri
- `fast-wrap-ansi` - 0.2.2 - MIT - https://github.com/43081j/fast-wrap-ansi
- `faye-websocket` - 0.11.4 - Apache-2.0 - https://github.com/faye/faye-websocket-node
- `fdir` - 6.5.0 - MIT - https://github.com/thecodrr/fdir
- `file-entry-cache` - 8.0.0 - MIT - https://github.com/jaredwray/file-entry-cache
- `fill-range` - 7.1.1 - MIT - https://github.com/jonschlinkert/fill-range
- `finalhandler` - 1.3.2 - MIT - https://github.com/pillarjs/finalhandler
- `finalhandler` - 2.1.1 - MIT - https://github.com/pillarjs/finalhandler
- `find-up` - 5.0.0 - MIT - https://github.com/sindresorhus/find-up
- `flat` - 5.0.2 - BSD-3-Clause - https://github.com/hughsk/flat
- `flat-cache` - 4.0.1 - MIT - https://github.com/jaredwray/flat-cache
- `flatted` - 3.4.4 - ISC - https://github.com/WebReflection/flatted
- `follow-redirects` - 1.16.0 - MIT - https://github.com/follow-redirects/follow-redirects
- `forwarded` - 0.2.0 - MIT - https://github.com/jshttp/forwarded
- `fraction.js` - 5.3.4 - MIT - https://github.com/rawify/Fraction.js
- `fresh` - 0.5.2 - MIT - https://github.com/jshttp/fresh
- `fresh` - 2.0.0 - MIT - https://github.com/jshttp/fresh
- `function-bind` - 1.1.2 - MIT - https://github.com/Raynos/function-bind
- `gensync` - 1.0.0-beta.2 - MIT - https://github.com/loganfsmyth/gensync
- `get-caller-file` - 2.0.5 - ISC - https://github.com/stefanpenner/get-caller-file
- `get-east-asian-width` - 1.6.0 - MIT - https://github.com/sindresorhus/get-east-asian-width
- `get-intrinsic` - 1.3.0 - MIT - https://github.com/ljharb/get-intrinsic
- `get-proto` - 1.0.1 - MIT - https://github.com/ljharb/get-proto
- `glob-parent` - 5.1.2 - ISC - https://github.com/gulpjs/glob-parent
- `glob-parent` - 6.0.2 - ISC - https://github.com/gulpjs/glob-parent
- `glob-to-regex.js` - 1.2.0 - Apache-2.0 - https://github.com/streamich/glob-to-regex
- `globals` - 14.0.0 - MIT - https://github.com/sindresorhus/globals
- `gopd` - 1.2.0 - MIT - https://github.com/ljharb/gopd
- `graceful-fs` - 4.2.11 - ISC - https://github.com/isaacs/node-graceful-fs
- `handle-thing` - 2.0.1 - MIT - https://github.com/indutny/handle-thing
- `has-flag` - 4.0.0 - MIT - https://github.com/sindresorhus/has-flag
- `has-symbols` - 1.1.0 - MIT - https://github.com/inspect-js/has-symbols
- `hasown` - 2.0.4 - MIT - https://github.com/inspect-js/hasOwn
- `hono` - 4.13.7 - MIT - https://github.com/honojs/hono
- `hosted-git-info` - 10.1.1 - ISC - https://github.com/npm/hosted-git-info
- `hpack.js` - 2.1.6 - MIT - https://github.com/indutny/hpack.js
- `html-encoding-sniffer` - 6.0.0 - MIT - https://github.com/jsdom/html-encoding-sniffer
- `htmlparser2` - 10.1.0 - MIT - https://github.com/fb55/htmlparser2
- `http-deceiver` - 1.2.7 - MIT - https://github.com/indutny/http-deceiver
- `http-errors` - 1.8.1 - MIT - https://github.com/jshttp/http-errors
- `http-errors` - 2.0.1 - MIT - https://github.com/jshttp/http-errors
- `http-parser-js` - 0.5.10 - MIT - https://github.com/creationix/http-parser-js
- `http-proxy` - 1.18.1 - MIT - https://github.com/http-party/node-http-proxy
- `http-proxy-middleware` - 2.0.10 - MIT - https://github.com/chimurai/http-proxy-middleware
- `http-proxy-middleware` - 4.2.0 - MIT - https://github.com/chimurai/http-proxy-middleware
- `https-proxy-agent` - 9.1.0 - MIT - https://github.com/TooTallNate/proxy-agents
- `httpxy` - 0.5.5 - MIT - https://github.com/unjs/httpxy
- `hyperdyperid` - 1.2.0 - MIT - https://github.com/streamich/hyperdyperid
- `iconv-lite` - 0.4.24 - MIT - https://github.com/ashtuchkin/iconv-lite
- `iconv-lite` - 0.6.3 - MIT - https://github.com/ashtuchkin/iconv-lite
- `iconv-lite` - 0.7.3 - MIT - https://github.com/pillarjs/iconv-lite
- `icss-utils` - 5.1.0 - ISC - https://github.com/css-modules/icss-utils
- `ieee754` - 1.2.1 - BSD-3-Clause - https://github.com/feross/ieee754
- `ignore` - 5.3.2 - MIT - https://github.com/kaelzhang/node-ignore
- `ignore` - 7.0.5 - MIT - https://github.com/kaelzhang/node-ignore
- `immutable` - 5.1.9 - MIT - https://github.com/immutable-js/immutable-js
- `import-fresh` - 3.3.1 - MIT - https://github.com/sindresorhus/import-fresh
- `import-meta-resolve` - 4.2.0 - MIT - https://github.com/wooorm/import-meta-resolve
- `imurmurhash` - 0.1.4 - MIT - https://github.com/jensyt/imurmurhash-js
- `inherits` - 2.0.4 - ISC - https://github.com/isaacs/inherits
- `ip-address` - 10.7.0 - MIT - https://github.com/beaugunderson/ip-address
- `ipaddr.js` - 1.9.1 - MIT - https://github.com/whitequark/ipaddr.js
- `ipaddr.js` - 2.5.0 - MIT - https://github.com/whitequark/ipaddr.js
- `is-arrayish` - 0.2.1 - MIT - https://github.com/qix-/node-is-arrayish
- `is-binary-path` - 2.1.0 - MIT - https://github.com/sindresorhus/is-binary-path
- `is-docker` - 3.0.0 - MIT - https://github.com/sindresorhus/is-docker
- `is-extglob` - 2.1.1 - MIT - https://github.com/jonschlinkert/is-extglob
- `is-fullwidth-code-point` - 5.1.0 - MIT - https://github.com/sindresorhus/is-fullwidth-code-point
- `is-glob` - 4.0.3 - MIT - https://github.com/micromatch/is-glob
- `is-in-ssh` - 1.0.0 - MIT - https://github.com/sindresorhus/is-in-ssh
- `is-inside-container` - 1.0.0 - MIT - https://github.com/sindresorhus/is-inside-container
- `is-interactive` - 1.0.0 - MIT - https://github.com/sindresorhus/is-interactive
- `is-interactive` - 2.0.0 - MIT - https://github.com/sindresorhus/is-interactive
- `is-network-error` - 1.3.2 - MIT - https://github.com/sindresorhus/is-network-error
- `is-number` - 7.0.0 - MIT - https://github.com/jonschlinkert/is-number
- `is-plain-obj` - 3.0.0 - MIT - https://github.com/sindresorhus/is-plain-obj
- `is-plain-obj` - 4.1.0 - MIT - https://github.com/sindresorhus/is-plain-obj
- `is-plain-object` - 2.0.4 - MIT - https://github.com/jonschlinkert/is-plain-object
- `is-potential-custom-element-name` - 1.0.1 - MIT - https://github.com/mathiasbynens/is-potential-custom-element-name
- `is-promise` - 4.0.0 - MIT - https://github.com/then/is-promise
- `is-unicode-supported` - 0.1.0 - MIT - https://github.com/sindresorhus/is-unicode-supported
- `is-unicode-supported` - 2.1.0 - MIT - https://github.com/sindresorhus/is-unicode-supported
- `is-what` - 4.1.16 - MIT - https://github.com/mesqueeb/is-what
- `is-wsl` - 3.1.1 - MIT - https://github.com/sindresorhus/is-wsl
- `isarray` - 1.0.0 - MIT - https://github.com/juliangruber/isarray
- `isexe` - 2.0.0 - ISC - https://github.com/isaacs/isexe
- `isobject` - 3.0.1 - MIT - https://github.com/jonschlinkert/isobject
- `istanbul-lib-coverage` - 3.2.2 - BSD-3-Clause - https://github.com/istanbuljs/istanbuljs
- `istanbul-lib-instrument` - 6.0.3 - BSD-3-Clause - https://github.com/istanbuljs/istanbuljs
- `jest-worker` - 27.5.1 - MIT - https://github.com/facebook/jest
- `jiti` - 2.7.0 - MIT - https://github.com/unjs/jiti
- `jose` - 6.2.12 - MIT - https://github.com/panva/jose
- `js-tokens` - 10.0.0 - MIT - https://github.com/lydell/js-tokens
- `js-tokens` - 4.0.0 - MIT - https://github.com/lydell/js-tokens
- `js-yaml` - 4.3.2 - MIT - https://github.com/nodeca/js-yaml
- `jsdom` - 30.0.1 - MIT - https://github.com/jsdom/jsdom
- `jsesc` - 3.1.0 - MIT - https://github.com/mathiasbynens/jsesc
- `json-buffer` - 3.0.1 - MIT - https://github.com/dominictarr/json-buffer
- `json-parse-even-better-errors` - 2.3.1 - MIT - https://github.com/npm/json-parse-even-better-errors
- `json-schema-traverse` - 0.4.1 - MIT - https://github.com/epoberezkin/json-schema-traverse
- `json-schema-traverse` - 1.0.0 - MIT - https://github.com/epoberezkin/json-schema-traverse
- `json-schema-typed` - 8.0.2 - BSD-2-Clause - https://github.com/RemyRylan/json-schema-typed
- `json-stable-stringify-without-jsonify` - 1.0.1 - MIT - https://github.com/samn/json-stable-stringify
- `json5` - 2.2.3 - MIT - https://github.com/json5/json5
- `jsonc-parser` - 3.3.1 - MIT - https://github.com/microsoft/node-jsonc-parser
- `karma-source-map-support` - 1.4.0 - MIT - https://github.com/tschaub/karma-source-map-support
- `keyv` - 4.5.4 - MIT - https://github.com/jaredwray/keyv
- `kind-of` - 6.0.3 - MIT - https://github.com/jonschlinkert/kind-of
- `launch-editor` - 2.14.1 - MIT - https://github.com/vitejs/launch-editor
- `less` - 4.9.0 - Apache-2.0 - https://github.com/less/less.js
- `less-loader` - 13.0.0 - MIT - https://github.com/webpack/less-loader
- `levn` - 0.4.1 - MIT - https://github.com/gkz/levn
- `license-webpack-plugin` - 4.0.2 - ISC - https://github.com/xz64/license-webpack-plugin
- `lightningcss` - 1.33.0 - MPL-2.0 - https://github.com/parcel-bundler/lightningcss
- `lightningcss-win32-x64-msvc` - 1.33.0 - MPL-2.0 - https://github.com/parcel-bundler/lightningcss
- `lines-and-columns` - 1.2.4 - MIT - https://github.com/eventualbuddha/lines-and-columns
- `listr2` - 11.0.0 - MIT - https://github.com/listr2/listr2
- `lmdb` - 3.5.6 - MIT - https://github.com/kriszyp/lmdb-js
- `loader-utils` - 2.0.4 - MIT - https://github.com/webpack/loader-utils
- `loader-utils` - 3.3.1 - MIT - https://github.com/webpack/loader-utils
- `locate-path` - 6.0.0 - MIT - https://github.com/sindresorhus/locate-path
- `lodash.debounce` - 4.0.8 - MIT - https://github.com/lodash/lodash
- `lodash.merge` - 4.6.2 - MIT - https://github.com/lodash/lodash
- `log-symbols` - 4.1.0 - MIT - https://github.com/sindresorhus/log-symbols
- `log-symbols` - 7.0.1 - MIT - https://github.com/sindresorhus/log-symbols
- `log-update` - 8.0.0 - MIT - https://github.com/sindresorhus/log-update
- `lru-cache` - 11.5.2 - BlueOak-1.0.0 - https://github.com/isaacs/node-lru-cache
- `lru-cache` - 5.1.1 - ISC - https://github.com/isaacs/node-lru-cache
- `magic-string` - 0.30.17 - MIT - https://github.com/rich-harris/magic-string
- `magic-string` - 0.30.21 - MIT - https://github.com/Rich-Harris/magic-string
- `magic-string` - 1.0.0 - MIT - https://github.com/Rich-Harris/magic-string
- `make-dir` - 5.1.0 - MIT - https://github.com/sindresorhus/make-dir
- `math-intrinsics` - 1.1.0 - MIT - https://github.com/es-shims/math-intrinsics
- `mdn-data` - 2.27.1 - CC0-1.0 - https://github.com/mdn/data
- `media-typer` - 0.3.0 - MIT - https://github.com/jshttp/media-typer
- `media-typer` - 1.1.1 - MIT - https://github.com/jshttp/media-typer
- `memfs` - 4.71.0 - Apache-2.0 - https://github.com/streamich/memfs
- `merge-descriptors` - 1.0.3 - MIT - https://github.com/sindresorhus/merge-descriptors
- `merge-descriptors` - 2.0.0 - MIT - https://github.com/sindresorhus/merge-descriptors
- `merge-stream` - 2.0.0 - MIT - https://github.com/grncdr/merge-stream
- `methods` - 1.1.2 - MIT - https://github.com/jshttp/methods
- `micromatch` - 4.0.8 - MIT - https://github.com/micromatch/micromatch
- `mime` - 1.6.0 - MIT - https://github.com/broofa/node-mime
- `mime-db` - 1.52.0 - MIT - https://github.com/jshttp/mime-db
- `mime-db` - 1.54.0 - MIT - https://github.com/jshttp/mime-db
- `mime-types` - 2.1.35 - MIT - https://github.com/jshttp/mime-types
- `mime-types` - 3.0.2 - MIT - https://github.com/jshttp/mime-types
- `mimic-fn` - 2.1.0 - MIT - https://github.com/sindresorhus/mimic-fn
- `mimic-function` - 5.0.1 - MIT - https://github.com/sindresorhus/mimic-function
- `mini-css-extract-plugin` - 2.10.2 - MIT - https://github.com/webpack/mini-css-extract-plugin
- `minimalistic-assert` - 1.0.1 - ISC - https://github.com/calvinmetcalf/minimalistic-assert
- `minimatch` - 10.2.6 - BlueOak-1.0.0 - https://github.com/isaacs/minimatch
- `minimatch` - 3.1.5 - ISC - https://github.com/isaacs/minimatch
- `minimizer-webpack-plugin` - 5.9.0 - MIT - https://github.com/webpack/minimizer-webpack-plugin
- `mrmime` - 2.0.1 - MIT - https://github.com/lukeed/mrmime
- `ms` - 2.0.0 - MIT - https://github.com/zeit/ms
- `ms` - 2.1.3 - MIT - https://github.com/vercel/ms
- `msgpackr` - 1.12.1 - MIT - https://github.com/kriszyp/msgpackr
- `msgpackr-extract` - 3.0.4 - MIT - https://github.com/kriszyp/msgpackr-extract
- `multicast-dns` - 7.2.5 - MIT - https://github.com/mafintosh/multicast-dns
- `mute-stream` - 3.0.0 - ISC - https://github.com/npm/mute-stream
- `nanoid` - 3.3.18 - MIT - https://github.com/ai/nanoid
- `natural-compare` - 1.4.0 - MIT - https://github.com/litejs/natural-compare-lite
- `needle` - 2.9.1 - MIT - https://github.com/tomas/needle
- `needle` - 3.5.0 - MIT - https://github.com/tomas/needle
- `negotiator` - 0.6.3 - MIT - https://github.com/jshttp/negotiator
- `negotiator` - 0.6.4 - MIT - https://github.com/jshttp/negotiator
- `negotiator` - 1.1.0 - MIT - https://github.com/jshttp/negotiator
- `neo-async` - 2.6.2 - MIT - https://github.com/suguru03/neo-async
- `node-addon-api` - 6.1.0 - MIT - https://github.com/nodejs/node-addon-api
- `node-addon-api` - 7.1.1 - MIT - https://github.com/nodejs/node-addon-api
- `node-gyp-build-optional-packages` - 5.2.2 - MIT - https://github.com/prebuild/node-gyp-build
- `node-releases` - 2.0.54 - MIT - https://github.com/chicoxyzzy/node-releases
- `normalize-path` - 3.0.0 - MIT - https://github.com/jonschlinkert/normalize-path
- `npm-package-arg` - 14.0.0 - ISC - https://github.com/npm/npm-package-arg
- `nth-check` - 2.1.1 - BSD-2-Clause - https://github.com/fb55/nth-check
- `object-assign` - 4.1.1 - MIT - https://github.com/sindresorhus/object-assign
- `object-inspect` - 1.13.4 - MIT - https://github.com/inspect-js/object-inspect
- `obuf` - 1.1.2 - MIT - https://github.com/indutny/offset-buffer
- `obug` - 2.1.4 - MIT - https://github.com/sxzz/obug
- `on-finished` - 2.4.1 - MIT - https://github.com/jshttp/on-finished
- `on-headers` - 1.1.0 - MIT - https://github.com/jshttp/on-headers
- `once` - 1.4.0 - ISC - https://github.com/isaacs/once
- `onetime` - 5.1.2 - MIT - https://github.com/sindresorhus/onetime
- `onetime` - 7.0.0 - MIT - https://github.com/sindresorhus/onetime
- `open` - 10.2.0 - MIT - https://github.com/sindresorhus/open
- `open` - 11.0.0 - MIT - https://github.com/sindresorhus/open
- `optionator` - 0.9.4 - MIT - https://github.com/gkz/optionator
- `ora` - 5.4.1 - MIT - https://github.com/sindresorhus/ora
- `ora` - 9.4.1 - MIT - https://github.com/sindresorhus/ora
- `ordered-binary` - 1.6.1 - MIT - https://github.com/kriszyp/ordered-binary
- `oxc-parser` - 0.142.0 - MIT - https://github.com/oxc-project/oxc
- `p-limit` - 3.1.0 - MIT - https://github.com/sindresorhus/p-limit
- `p-locate` - 5.0.0 - MIT - https://github.com/sindresorhus/p-locate
- `p-retry` - 6.2.1 - MIT - https://github.com/sindresorhus/p-retry
- `parent-module` - 1.0.1 - MIT - https://github.com/sindresorhus/parent-module
- `parse-json` - 5.2.0 - MIT - https://github.com/sindresorhus/parse-json
- `parse-node-version` - 1.0.1 - MIT - https://github.com/gulpjs/parse-node-version
- `parse5-html-rewriting-stream` - 8.0.1 - MIT - https://github.com/inikulin/parse5
- `parse5-sax-parser` - 8.0.0 - MIT - https://github.com/inikulin/parse5
- `parseurl` - 1.3.3 - MIT - https://github.com/pillarjs/parseurl
- `path-exists` - 4.0.0 - MIT - https://github.com/sindresorhus/path-exists
- `path-key` - 3.1.1 - MIT - https://github.com/sindresorhus/path-key
- `path-to-regexp` - 0.1.13 - MIT - https://github.com/pillarjs/path-to-regexp
- `path-to-regexp` - 8.4.2 - MIT - https://github.com/pillarjs/path-to-regexp
- `pathe` - 2.0.3 - MIT - https://github.com/unjs/pathe
- `picocolors` - 1.1.1 - ISC - https://github.com/alexeyraspopov/picocolors
- `picomatch` - 2.3.2 - MIT - https://github.com/micromatch/picomatch
- `picomatch` - 4.0.4 - MIT - https://github.com/micromatch/picomatch
- `picomatch` - 4.0.5 - MIT - https://github.com/micromatch/picomatch
- `piscina` - 5.2.0 - MIT - https://github.com/piscinajs/piscina
- `pkce-challenge` - 5.0.1 - MIT - https://github.com/crouchcd/pkce-challenge
- `pkijs` - 3.4.0 - BSD-3-Clause - https://github.com/PeculiarVentures/PKI.js
- `playwright` - 1.63.0 - Apache-2.0 - https://github.com/microsoft/playwright
- `playwright-core` - 1.63.0 - Apache-2.0 - https://github.com/microsoft/playwright
- `postcss` - 8.5.25 - MIT - https://github.com/postcss/postcss
- `postcss-loader` - 8.2.1 - MIT - https://github.com/webpack/postcss-loader
- `postcss-media-query-parser` - 0.2.3 - MIT - https://github.com/dryoma/postcss-media-query-parser
- `postcss-modules-extract-imports` - 3.1.0 - ISC - https://github.com/css-modules/postcss-modules-extract-imports
- `postcss-modules-local-by-default` - 4.2.0 - MIT - https://github.com/css-modules/postcss-modules-local-by-default
- `postcss-modules-scope` - 3.2.1 - ISC - https://github.com/css-modules/postcss-modules-scope
- `postcss-modules-values` - 4.0.0 - ISC - https://github.com/css-modules/postcss-modules-values
- `postcss-safe-parser` - 7.1.0 - MIT - https://github.com/postcss/postcss-safe-parser
- `postcss-selector-parser` - 7.1.6 - MIT - https://github.com/postcss/postcss-selector-parser
- `postcss-value-parser` - 4.2.0 - MIT - https://github.com/TrySound/postcss-value-parser
- `powershell-utils` - 0.1.0 - MIT - https://github.com/sindresorhus/powershell-utils
- `prelude-ls` - 1.2.1 - MIT - https://github.com/gkz/prelude-ls
- `probe-image-size` - 7.4.0 - MIT - https://github.com/nodeca/probe-image-size
- `proc-log` - 7.0.0 - ISC - https://github.com/npm/proc-log
- `process-nextick-args` - 2.0.1 - MIT - https://github.com/calvinmetcalf/process-nextick-args
- `proxy-addr` - 2.0.7 - MIT - https://github.com/jshttp/proxy-addr
- `proxy-agent-negotiate` - 1.1.0 - MIT - https://github.com/TooTallNate/proxy-agents
- `prr` - 1.0.1 - MIT - https://github.com/rvagg/prr
- `punycode` - 2.3.1 - MIT - https://github.com/mathiasbynens/punycode.js
- `pvtsutils` - 1.3.6 - MIT - https://github.com/PeculiarVentures/pvtsutils
- `pvutils` - 1.2.0 - MIT - https://github.com/PeculiarVentures/pvutils
- `qs` - 6.15.3 - BSD-3-Clause - https://github.com/ljharb/qs
- `qs` - 6.16.0 - BSD-3-Clause - https://github.com/ljharb/qs
- `range-parser` - 1.2.1 - MIT - https://github.com/jshttp/range-parser
- `range-parser` - 1.3.0 - MIT - https://github.com/jshttp/range-parser
- `raw-body` - 2.5.3 - MIT - https://github.com/stream-utils/raw-body
- `raw-body` - 3.0.2 - MIT - https://github.com/stream-utils/raw-body
- `readable-stream` - 2.3.8 - MIT - https://github.com/nodejs/readable-stream
- `readable-stream` - 3.6.2 - MIT - https://github.com/nodejs/readable-stream
- `readdirp` - 3.6.0 - MIT - https://github.com/paulmillr/readdirp
- `readdirp` - 5.1.1 - MIT - https://github.com/paulmillr/readdirp
- `reflect-metadata` - 0.2.2 - Apache-2.0 - https://github.com/rbuckton/reflect-metadata
- `regenerate` - 1.4.2 - MIT - https://github.com/mathiasbynens/regenerate
- `regenerate-unicode-properties` - 10.2.2 - MIT - https://github.com/mathiasbynens/regenerate-unicode-properties
- `regex-parser` - 2.3.1 - MIT - https://github.com/IonicaBizau/regex-parser.js
- `regexpu-core` - 6.4.0 - MIT - https://github.com/mathiasbynens/regexpu-core
- `regjsgen` - 0.8.0 - MIT - https://github.com/bnjmnt4n/regjsgen
- `regjsparser` - 0.13.2 - BSD-2-Clause - https://github.com/jviereck/regjsparser
- `require-from-string` - 2.0.2 - MIT - https://github.com/floatdrop/require-from-string
- `requires-port` - 1.0.0 - MIT - https://github.com/unshiftio/requires-port
- `resolve-from` - 4.0.0 - MIT - https://github.com/sindresorhus/resolve-from
- `resolve-url-loader` - 5.0.0 - MIT - https://github.com/bholloway/resolve-url-loader
- `restore-cursor` - 3.1.0 - MIT - https://github.com/sindresorhus/restore-cursor
- `restore-cursor` - 5.1.0 - MIT - https://github.com/sindresorhus/restore-cursor
- `retry` - 0.13.1 - MIT - https://github.com/tim-kos/node-retry
- `rolldown` - 1.1.5 - MIT - https://github.com/rolldown/rolldown
- `rolldown` - 1.2.0 - MIT - https://github.com/rolldown/rolldown
- `router` - 2.2.0 - MIT - https://github.com/pillarjs/router
- `run-applescript` - 7.1.0 - MIT - https://github.com/sindresorhus/run-applescript
- `rxjs` - 7.8.1 - Apache-2.0 - https://github.com/reactivex/rxjs
- `safe-buffer` - 5.1.2 - MIT - https://github.com/feross/safe-buffer
- `safe-buffer` - 5.2.1 - MIT - https://github.com/feross/safe-buffer
- `safer-buffer` - 2.1.2 - MIT - https://github.com/ChALkeR/safer-buffer
- `sass` - 1.101.0 - MIT - https://github.com/sass/dart-sass
- `sass-loader` - 17.0.0 - MIT - https://github.com/webpack/sass-loader
- `sax` - 1.6.1 - BlueOak-1.0.0 - https://github.com/isaacs/sax-js
- `saxes` - 6.0.0 - ISC - https://github.com/lddubeau/saxes
- `schema-utils` - 4.3.3 - MIT - https://github.com/webpack/schema-utils
- `select-hose` - 2.0.0 - MIT - https://github.com/indutny/select-hose
- `selfsigned` - 5.5.0 - MIT - https://github.com/jfromaniello/selfsigned
- `semver` - 6.3.1 - ISC - https://github.com/npm/node-semver
- `semver` - 7.7.2 - ISC - https://github.com/npm/node-semver
- `semver` - 7.8.5 - ISC - https://github.com/npm/node-semver
- `send` - 0.19.2 - MIT - https://github.com/pillarjs/send
- `send` - 1.2.1 - MIT - https://github.com/pillarjs/send
- `serialize-javascript` - 7.1.1 - BSD-3-Clause - https://github.com/yahoo/serialize-javascript
- `serve-index` - 1.9.2 - MIT - https://github.com/expressjs/serve-index
- `serve-static` - 1.16.3 - MIT - https://github.com/expressjs/serve-static
- `serve-static` - 2.2.1 - MIT - https://github.com/expressjs/serve-static
- `setprototypeof` - 1.2.0 - ISC - https://github.com/wesleytodd/setprototypeof
- `shallow-clone` - 3.0.1 - MIT - https://github.com/jonschlinkert/shallow-clone
- `shebang-command` - 2.0.0 - MIT - https://github.com/kevva/shebang-command
- `shebang-regex` - 3.0.0 - MIT - https://github.com/sindresorhus/shebang-regex
- `shell-quote` - 1.10.0 - MIT - https://github.com/ljharb/shell-quote
- `side-channel` - 1.1.1 - MIT - https://github.com/ljharb/side-channel
- `side-channel-list` - 1.0.1 - MIT - https://github.com/ljharb/side-channel-list
- `side-channel-map` - 1.0.1 - MIT - https://github.com/ljharb/side-channel-map
- `side-channel-weakmap` - 1.0.2 - MIT - https://github.com/ljharb/side-channel-weakmap
- `siginfo` - 2.0.0 - ISC - https://github.com/emilbayes/siginfo
- `signal-exit` - 3.0.7 - ISC - https://github.com/tapjs/signal-exit
- `signal-exit` - 4.1.0 - ISC - https://github.com/tapjs/signal-exit
- `slice-ansi` - 9.0.0 - MIT - https://github.com/chalk/slice-ansi
- `sockjs` - 0.3.24 - MIT - https://github.com/sockjs/sockjs-node
- `source-map` - 0.6.1 - BSD-3-Clause - https://github.com/mozilla/source-map
- `source-map` - 0.7.4 - BSD-3-Clause - https://github.com/mozilla/source-map
- `source-map` - 0.7.6 - BSD-3-Clause - https://github.com/mozilla/source-map
- `source-map-js` - 1.2.1 - BSD-3-Clause - https://github.com/7rulnik/source-map-js
- `source-map-loader` - 5.0.0 - MIT - https://github.com/webpack-contrib/source-map-loader
- `source-map-support` - 0.5.21 - MIT - https://github.com/evanw/node-source-map-support
- `spdy` - 4.0.2 - MIT - https://github.com/indutny/node-spdy
- `spdy-transport` - 3.0.0 - MIT - https://github.com/spdy-http2/spdy-transport
- `stackback` - 0.0.2 - MIT - https://github.com/shtylman/node-stackback
- `statuses` - 1.5.0 - MIT - https://github.com/jshttp/statuses
- `statuses` - 2.0.2 - MIT - https://github.com/jshttp/statuses
- `std-env` - 4.2.0 - MIT - https://github.com/unjs/std-env
- `stdin-discarder` - 0.3.2 - MIT - https://github.com/sindresorhus/stdin-discarder
- `stream-parser` - 0.3.1 - MIT - https://github.com/TooTallNate/node-stream-parser
- `string_decoder` - 1.1.1 - MIT - https://github.com/nodejs/string_decoder
- `string_decoder` - 1.3.0 - MIT - https://github.com/nodejs/string_decoder
- `string-width` - 7.2.0 - MIT - https://github.com/sindresorhus/string-width
- `string-width` - 8.2.2 - MIT - https://github.com/sindresorhus/string-width
- `strip-ansi` - 6.0.1 - MIT - https://github.com/chalk/strip-ansi
- `strip-ansi` - 7.2.0 - MIT - https://github.com/chalk/strip-ansi
- `strip-json-comments` - 3.1.1 - MIT - https://github.com/sindresorhus/strip-json-comments
- `supports-color` - 7.2.0 - MIT - https://github.com/chalk/supports-color
- `supports-color` - 8.1.1 - MIT - https://github.com/chalk/supports-color
- `symbol-tree` - 3.2.4 - MIT - https://github.com/jsdom/js-symbol-tree
- `tapable` - 2.3.3 - MIT - https://github.com/webpack/tapable
- `terser` - 5.49.0 - BSD-2-Clause - https://github.com/terser/terser
- `terser` - 5.51.2 - BSD-2-Clause - https://github.com/terser/terser
- `thingies` - 2.6.1 - MIT - https://github.com/streamich/thingies
- `thunky` - 1.1.0 - MIT - https://github.com/mafintosh/thunky
- `tinybench` - 2.9.0 - MIT - https://github.com/tinylibs/tinybench
- `tinyexec` - 1.3.1 - MIT - https://github.com/tinylibs/tinyexec
- `tinyglobby` - 0.2.17 - MIT - https://github.com/SuperchupuDev/tinyglobby
- `tinyrainbow` - 3.1.1 - MIT - https://github.com/tinylibs/tinyrainbow
- `tldts` - 7.4.12 - MIT - https://github.com/remusao/tldts
- `tldts-core` - 7.4.12 - MIT - https://github.com/remusao/tldts
- `to-regex-range` - 5.0.1 - MIT - https://github.com/micromatch/to-regex-range
- `toidentifier` - 1.0.1 - MIT - https://github.com/component/toidentifier
- `tough-cookie` - 6.0.2 - BSD-3-Clause - https://github.com/salesforce/tough-cookie
- `tr46` - 6.0.0 - MIT - https://github.com/jsdom/tr46
- `tree-dump` - 1.1.0 - Apache-2.0 - https://github.com/streamich/tree-dump
- `ts-api-utils` - 2.5.0 - MIT - https://github.com/JoshuaKGoldberg/ts-api-utils
- `tslib` - 1.14.1 - 0BSD - https://github.com/microsoft/tslib
- `tsyringe` - 4.10.0 - MIT - https://github.com/microsoft/tsyringe
- `type-check` - 0.4.0 - MIT - https://github.com/gkz/type-check
- `type-is` - 1.6.18 - MIT - https://github.com/jshttp/type-is
- `type-is` - 2.1.0 - MIT - https://github.com/jshttp/type-is
- `typed-assert` - 1.0.9 - MIT - https://github.com/elierotenberg/typed-assert
- `typescript` - 6.0.3 - Apache-2.0 - https://github.com/microsoft/TypeScript
- `typescript-eslint` - 8.69.0 - MIT - https://github.com/typescript-eslint/typescript-eslint
- `undici` - 8.10.2 - MIT - https://github.com/nodejs/undici
- `undici-types` - 7.18.2 - MIT - https://github.com/nodejs/undici
- `unicode-canonical-property-names-ecmascript` - 2.0.1 - MIT - https://github.com/mathiasbynens/unicode-canonical-property-names-ecmascript
- `unicode-match-property-ecmascript` - 2.0.0 - MIT - https://github.com/mathiasbynens/unicode-match-property-ecmascript
- `unicode-match-property-value-ecmascript` - 2.2.1 - MIT - https://github.com/mathiasbynens/unicode-match-property-value-ecmascript
- `unicode-property-aliases-ecmascript` - 2.2.0 - MIT - https://github.com/mathiasbynens/unicode-property-aliases-ecmascript
- `unpipe` - 1.0.0 - MIT - https://github.com/stream-utils/unpipe
- `update-browserslist-db` - 1.3.2 - MIT - https://github.com/browserslist/update-db
- `uri-js` - 4.4.1 - BSD-2-Clause - https://github.com/garycourt/uri-js
- `util-deprecate` - 1.0.2 - MIT - https://github.com/TooTallNate/util-deprecate
- `utils-merge` - 1.0.1 - MIT - https://github.com/jaredhanson/utils-merge
- `uuid` - 8.3.2 - MIT - https://github.com/uuidjs/uuid
- `validate-npm-package-name` - 8.0.0 - ISC - https://github.com/npm/validate-npm-package-name
- `vary` - 1.1.2 - MIT - https://github.com/jshttp/vary
- `vite` - 8.1.5 - MIT - https://github.com/vitejs/vite
- `vitest` - 4.1.11 - MIT - https://github.com/vitest-dev/vitest
- `w3c-xmlserializer` - 5.0.0 - MIT - https://github.com/jsdom/w3c-xmlserializer
- `watchpack` - 2.5.2 - MIT - https://github.com/webpack/watchpack
- `wbuf` - 1.7.3 - MIT - https://github.com/indutny/wbuf
- `wcwidth` - 1.0.1 - MIT - https://github.com/timoxley/wcwidth
- `weak-lru-cache` - 1.2.2 - MIT - https://github.com/kriszyp/weak-lru-cache
- `webidl-conversions` - 8.0.1 - BSD-2-Clause - https://github.com/jsdom/webidl-conversions
- `webpack` - 5.109.2 - MIT - https://github.com/webpack/webpack
- `webpack-dev-middleware` - 7.4.6 - MIT - https://github.com/webpack/webpack-dev-middleware
- `webpack-dev-middleware` - 8.0.3 - MIT - https://github.com/webpack/webpack-dev-middleware
- `webpack-dev-server` - 5.2.6 - MIT - https://github.com/webpack/webpack-dev-server
- `webpack-merge` - 6.0.1 - MIT - https://github.com/survivejs/webpack-merge
- `webpack-sources` - 3.5.1 - MIT - https://github.com/webpack/webpack-sources
- `webpack-subresource-integrity` - 5.1.0 - MIT - https://github.com/waysact/webpack-subresource-integrity
- `websocket-driver` - 0.7.5 - Apache-2.0 - https://github.com/faye/websocket-driver-node
- `websocket-extensions` - 0.1.4 - Apache-2.0 - https://github.com/faye/websocket-extensions-node
- `whatwg-mimetype` - 5.0.0 - MIT - https://github.com/jsdom/whatwg-mimetype
- `whatwg-url` - 16.0.1 - MIT - https://github.com/jsdom/whatwg-url
- `whatwg-url` - 17.1.0 - MIT - https://github.com/jsdom/whatwg-url
- `which` - 2.0.2 - ISC - https://github.com/isaacs/node-which
- `why-is-node-running` - 2.3.0 - MIT - https://github.com/mafintosh/why-is-node-running
- `wildcard` - 2.0.1 - MIT - https://github.com/DamonOehlman/wildcard
- `word-wrap` - 1.2.5 - MIT - https://github.com/jonschlinkert/word-wrap
- `wrap-ansi` - 10.0.1 - MIT - https://github.com/chalk/wrap-ansi
- `wrap-ansi` - 9.0.2 - MIT - https://github.com/chalk/wrap-ansi
- `wrappy` - 1.0.2 - ISC - https://github.com/npm/wrappy
- `ws` - 8.21.3 - MIT - https://github.com/websockets/ws
- `wsl-utils` - 0.1.0 - MIT - https://github.com/sindresorhus/wsl-utils
- `wsl-utils` - 0.3.1 - MIT - https://github.com/sindresorhus/wsl-utils
- `xml-name-validator` - 5.0.0 - Apache-2.0 - https://github.com/jsdom/xml-name-validator
- `xmlchars` - 2.2.0 - MIT - https://github.com/lddubeau/xmlchars
- `y18n` - 5.0.8 - ISC - https://github.com/yargs/y18n
- `yallist` - 3.1.1 - ISC - https://github.com/isaacs/yallist
- `yargs` - 18.1.0 - MIT - https://github.com/yargs/yargs
- `yargs-parser` - 22.0.0 - ISC - https://github.com/yargs/yargs-parser
- `yocto-queue` - 0.1.0 - MIT - https://github.com/sindresorhus/yocto-queue
- `yoctocolors` - 2.2.0 - MIT - https://github.com/sindresorhus/yoctocolors
- `zod-to-json-schema` - 3.25.2 - ISC - https://github.com/StefanTerdell/zod-to-json-schema

## 5. Container images and OS packages

| Image | Role | Distributed? | Notes |
|---|---|---|---|
| `mcr.microsoft.com/dotnet/aspnet:10.0` | runtime base of the MangaPixer image | **yes** (it is the image's base layers) | Ubuntu 24.04.5 LTS ("noble") as of the 1.12.0 build; .NET runtime and ASP.NET Core 10.0.12 (MIT, https://github.com/dotnet/dotnet-docker/blob/main/LICENSE). Microsoft's container legal notice: https://aka.ms/mcr/osslegalnotice. Linux image contents: https://github.com/dotnet/dotnet-docker/blob/main/documentation/image-artifact-details.md |
| `mcr.microsoft.com/dotnet/sdk:10.0` | .NET build stage | no | Ubuntu 24.04.5 LTS; same licensing references as above |
| `node:24-bookworm-slim` | Angular build stage | no | Debian 12 ("bookworm"); Node.js v24 (MIT, https://github.com/nodejs/node/blob/main/LICENSE) |

The `aspnet:10.0` tag resolved to Ubuntu 24.04 for the 1.12.0 build, as the
Dockerfile's header comment says.

Packages `deploy/Dockerfile` adds to the runtime image with `apt-get`, with
versions and licenses read from the 1.12.0 release image (`dpkg-query`,
`/usr/share/doc/<pkg>/copyright`):

- `gosu` - 1.17-1ubuntu0.24.04.3 - Apache-2.0 - https://github.com/tianon/gosu (drops root privileges in `entrypoint.sh`)
- `libgdiplus` - 6.1+dfsg-1build3 - MIT (X11), Copyright (c) 2001-2004 Novell - https://github.com/mono/libgdiplus
- `curl` / `libcurl4t64` - 8.5.0-2ubuntu10.13 - curl - https://curl.se (used by the image health check)
- `ca-certificates` - 20260601~24.04.1 - MPL-2.0 (Mozilla CA data) and GPL-2.0-or-later (scripts) - https://launchpad.net/ubuntu/+source/ca-certificates

Every other OS package in the image (the base layers plus the dependencies pulled in
by the packages above) keeps its own license. Each one's copyright file is at
`/usr/share/doc/<package>/copyright` inside the image, and
`pwsh ./scripts/Package-Release.ps1` writes a complete SPDX SBOM of the image.

## 6. Windows distribution

<!-- TODO(owner): the Windows tray app / MSI installer (parallel cycle) will need its own inventory. A self-contained Windows publish also redistributes the .NET runtime (MIT) and its THIRD-PARTY-NOTICES.TXT, the Windows native binaries (Magick.Native-Q8-x64.dll, e_sqlite3.dll), and any installer-toolchain runtime. Add them here once that build exists. -->

Not yet covered - see the TODO above.

## 7. Regenerating this inventory

```text
dotnet restore MangaPixer.slnx --locked-mode
dotnet list MangaPixer.slnx package --include-transitive --format json
dotnet publish src/MangaPixer.Server/MangaPixer.Server.csproj -c Release -o <tmp>/server
dotnet publish src/MangaPixer.MediaWorker/MangaPixer.MediaWorker.csproj -c Release -o <tmp>/worker
#   shipped set = "type": "package" entries in <tmp>/*/*.deps.json
npm --prefix web ci
npm --prefix web ls --omit=dev --all --json
npm --prefix web ls --all --json
```

Licenses: read `<license>` from `~/.nuget/packages/<id>/<version>/<id>.nuspec` and
`license` from `web/node_modules/<pkg>/package.json`. Refresh this file whenever
`Directory.Packages.props` or `web/package-lock.json` changes.
