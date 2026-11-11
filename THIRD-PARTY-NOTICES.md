# Third-party notices

erikterm is MIT licensed (see `LICENSE`). It uses the libraries below. On Linux and macOS they come from your system.
The Windows download includes them as DLLs, and the full license text of each DLL is in its `licenses/` folder.

| Library | License | Project |
|---|---|---|
| SDL3 | zlib | https://libsdl.org |
| FreeType | FreeType License (FTL) | https://freetype.org |
| HarfBuzz | MIT ("Old MIT") | https://harfbuzz.github.io |
| Windows console host (`conpty.dll`, `OpenConsole.exe`; Windows download only) | MIT | https://github.com/microsoft/terminal |
| .NET runtime (compiled in) | MIT | https://dotnet.microsoft.com |

The Windows download also includes the libraries FreeType is built with in vcpkg. erikterm doesn't call them itself.

| Library (Windows download only) | DLLs | License | Project |
|---|---|---|---|
| libpng | `libpng16.dll` | PNG Reference Library License version 2 | http://www.libpng.org/pub/png/libpng.html |
| zlib | `z.dll` | zlib | https://zlib.net |
| bzip2 | `bz2.dll` | bzip2 license (BSD-style) | https://sourceware.org/bzip2/ |
| Brotli | `brotlicommon.dll`, `brotlidec.dll`, `brotlienc.dll` | MIT | https://github.com/google/brotli |

Portions of this software are copyright © The FreeType Project (https://freetype.org). All rights reserved.
