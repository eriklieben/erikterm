namespace ErikTerm.App.Ui;

/// <summary>One third-party component in the About screen's Licenses tab.</summary>
internal sealed record LicenseEntry(
    string Name,
    string? Version,
    string License,
    string Copyright,
    string? Credit,
    IReadOnlyList<string> Paragraphs,
    string? Url);

/// <summary>Third-party notices. erikterm loads SDL3, FreeType and HarfBuzz from the system and embeds Unicode data and the .NET runtime.</summary>
internal static class AboutLicenses
{
    public static List<LicenseEntry> Build(AboutInfo info)
    {
        var list = new List<LicenseEntry>
        {
            new("FreeType", info.FreeTypeVersion, "FreeType License (FTL)",
                "Copyright © 1996–" + info.FreeTypeYear + " David Turner, Robert Wilhelm, Werner Lemberg and the FreeType contributors.",
                AboutFormat.FreeTypeCredit(info.FreeTypeYear),
                [
                    "Font rasterisation. erikterm uses FreeType under the terms of The FreeType Project License (FTL), " +
                    "which permits use in any program provided this credit is given in the documentation. FreeType is " +
                    "provided \"as is\", without warranty of any kind.",
                ],
                "https://freetype.org/license.html"),

            new("SDL", info.SdlVersion, "zlib License",
                "Copyright © 1997–2026 Sam Lantinga <slouken@libsdl.org>",
                null,
                [
                    "This software is provided 'as-is', without any express or implied warranty. In no event will the " +
                    "authors be held liable for any damages arising from the use of this software.",
                    "Permission is granted to anyone to use this software for any purpose, including commercial " +
                    "applications, and to alter it and redistribute it freely, subject to the following restrictions:",
                    "1. The origin of this software must not be misrepresented; you must not claim that you wrote the " +
                    "original software. If you use this software in a product, an acknowledgment in the product " +
                    "documentation would be appreciated but is not required.",
                    "2. Altered source versions must be plainly marked as such, and must not be misrepresented as being " +
                    "the original software.",
                    "3. This notice may not be removed or altered from any source distribution.",
                ],
                "https://libsdl.org"),

            new("HarfBuzz", info.HarfBuzzVersion ?? "not loaded", "Old MIT License",
                "Copyright © 2010–2025 Google, Inc., Behdad Esfahbod, Red Hat, Inc., and the HarfBuzz contributors " +
                "(see the COPYING file of the installed package for every holder).",
                null,
                [
                    "Permission is hereby granted, without written agreement and without license or royalty fees, to use, " +
                    "copy, modify, and distribute this software and its documentation for any purpose, provided that the " +
                    "above copyright notice and the following two paragraphs appear in all copies of this software.",
                    "IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR DIRECT, INDIRECT, SPECIAL, " +
                    "INCIDENTAL, OR CONSEQUENTIAL DAMAGES ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, " +
                    "EVEN IF THE COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.",
                    "THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE " +
                    "IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED " +
                    "HEREUNDER IS ON AN \"AS IS\" BASIS, AND THE COPYRIGHT HOLDER HAS NO OBLIGATION TO PROVIDE " +
                    "MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.",
                ],
                "https://harfbuzz.github.io"),

            new("Unicode Character Database", info.UnicodeVersion, "Unicode License v3",
                "Copyright © 1991–2025 Unicode, Inc.",
                null,
                [
                    "Character widths and emoji properties are generated from the Unicode Character Database.",
                    "Permission is hereby granted, free of charge, to any person obtaining a copy of data files and any " +
                    "associated documentation (the \"Data Files\") or software and any associated documentation (the " +
                    "\"Software\") to deal in the Data Files or Software without restriction, including without " +
                    "limitation the rights to use, copy, modify, merge, publish, distribute, and/or sell copies of the " +
                    "Data Files or Software, and to permit persons to whom the Data Files or Software are furnished to do " +
                    "so, provided that either (a) this copyright and permission notice appear with all copies of the Data " +
                    "Files or Software, or (b) this copyright and permission notice appear in associated Documentation.",
                    "THE DATA FILES AND SOFTWARE ARE PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, " +
                    "INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND " +
                    "NONINFRINGEMENT OF THIRD PARTY RIGHTS.",
                    "IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE LIABLE FOR ANY CLAIM, OR " +
                    "ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, " +
                    "DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF " +
                    "OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA FILES OR SOFTWARE.",
                    "Except as contained in this notice, the name of a copyright holder shall not be used in advertising or " +
                    "otherwise to promote the sale, use or other dealings in these Data Files or Software without prior " +
                    "written authorization of the copyright holder.",
                ],
                "https://www.unicode.org/license.txt"),

            new(".NET Runtime", RuntimeVersion(info.Runtime), "MIT License",
                "Copyright © .NET Foundation and Contributors",
                null,
                [
                    "The runtime and base class libraries are compiled into this binary by Native AOT.",
                    "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and " +
                    "associated documentation files (the \"Software\"), to deal in the Software without restriction, " +
                    "including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, " +
                    "and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, " +
                    "subject to the following conditions: The above copyright notice and this permission notice shall be " +
                    "included in all copies or substantial portions of the Software.",
                    "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT " +
                    "NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND " +
                    "NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES " +
                    "OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN " +
                    "CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.",
                ],
                "https://github.com/dotnet/runtime"),
        };

        list.Add(new LicenseEntry("System libraries & fonts", null, "Their own licenses",
            "Loaded from the operating system at run time, not distributed with erikterm.",
            null,
            [
                "SDL3, FreeType and HarfBuzz pull in further system libraries (for example zlib, libpng, bzip2, " +
                "Brotli, GLib and graphics drivers), and fonts are found through fontconfig. Each is used under its " +
                "own license as installed by your system.",
                LibrarySummary(info.Libraries),
            ],
            null));
        return list;
    }

    private static string? RuntimeVersion(string framework)
    {
        int space = framework.LastIndexOf(' ');
        if (space > 0) return framework[(space + 1)..];
        return framework.Length > 0 ? framework : null;
    }

    /// <summary>"Loaded now (42): libSDL3.so.0 · libfreetype.so.6 · …".</summary>
    internal static string LibrarySummary(IReadOnlyList<string> libraries)
    {
        if (libraries.Count == 0) return "No shared library list is available on this platform.";
        var names = new List<string>(libraries.Count);
        foreach (var lib in libraries)
        {
            string name = Path.GetFileName(lib);
            if (!names.Contains(name)) names.Add(name);
        }
        return $"Loaded now ({names.Count}): " + string.Join(" · ", names);
    }
}
