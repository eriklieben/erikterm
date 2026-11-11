#!/usr/bin/env python3
"""Generates src/ErikTerm.Core/UnicodeTables.g.cs from the Unicode Character Database.

Usage:
    scripts/gen-unicode-tables.py UCD_DIR [OUTPUT]

UCD_DIR must contain EastAsianWidth.txt, DerivedGeneralCategory.txt and emoji-data.txt, e.g.
downloaded once from https://www.unicode.org/Public/<version>/ucd/ (emoji-data.txt lives in
ucd/emoji/, DerivedGeneralCategory.txt in ucd/extracted/). The build never downloads anything:
the generated file is committed.

Each code point gets one property byte (see CharWidth.cs):
    bits 0-1  column width: 0, 1 or 2
    bit 2     Emoji                  (VS16 after it switches to wide emoji presentation)
    bit 3     Extended_Pictographic  (may continue a ZWJ sequence)
    bit 4     Emoji_Modifier         (skin tones U+1F3FB..U+1F3FF)
    bit 5     Regional_Indicator     (flag halves)
    bit 6     Emoji_Modifier_Base

Width rules (close to what kitty / wezterm / recent glibc use):
    0  general category Mn, Me, Cf, Cc (but U+00AD soft hyphen and the prepended concatenation
       marks such as U+0600 stay 1), Hangul medial vowels / final consonants (U+1160..U+11FF,
       U+D7B0..U+D7FF)
    2  East_Asian_Width W or F (including the @missing defaults for unassigned CJK blocks), or
       Emoji_Presentation=Yes
    1  everything else (ambiguous width is narrow)
The table is stored as a two-stage lookup (block index, then deduplicated blocks).
"""

import os
import re
import sys

MAX = 0x110000


def parse(path):
    """Yields (first, last, fields) for each data line; also handles '# @missing:' lines."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            missing = line.startswith("# @missing:")
            if missing:
                line = line[len("# @missing:"):]
            else:
                line = line.split("#", 1)[0]
            line = line.strip()
            if not line:
                continue
            parts = [p.strip() for p in line.split(";")]
            rng = parts[0]
            if ".." in rng:
                a, b = rng.split("..")
            else:
                a = b = rng
            yield int(a, 16), int(b, 16), parts[1:], missing


def version_of(path):
    with open(path, encoding="utf-8") as f:
        head = f.read(400)
    m = re.search(r"-(\d+\.\d+\.\d+)\.txt", head)
    return m.group(1) if m else "unknown"


def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    ucd = sys.argv[1]
    here = os.path.dirname(os.path.abspath(__file__))
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "..", "src", "ErikTerm.Core", "UnicodeTables.g.cs")

    eaw_path = os.path.join(ucd, "EastAsianWidth.txt")
    gc_path = os.path.join(ucd, "DerivedGeneralCategory.txt")
    emoji_path = os.path.join(ucd, "emoji-data.txt")
    version = version_of(eaw_path)

    eaw = ["N"] * MAX
    # @missing lines give defaults for ranges; apply them first, explicit data overrides.
    explicit = []
    for a, b, f, missing in parse(eaw_path):
        if missing:
            for cp in range(a, b + 1):
                eaw[cp] = f[0]
        else:
            explicit.append((a, b, f[0]))
    for a, b, v in explicit:
        for cp in range(a, b + 1):
            eaw[cp] = v

    gc = ["Cn"] * MAX
    for a, b, f, missing in parse(gc_path):
        if missing:
            continue
        for cp in range(a, b + 1):
            gc[cp] = f[0]

    emoji = {name: bytearray(MAX) for name in
             ("Emoji", "Emoji_Presentation", "Emoji_Modifier", "Emoji_Modifier_Base", "Extended_Pictographic")}
    for a, b, f, missing in parse(emoji_path):
        if missing or f[0] not in emoji:
            continue
        for cp in range(a, b + 1):
            emoji[f[0]][cp] = 1

    prepended = {0x0600, 0x0601, 0x0602, 0x0603, 0x0604, 0x0605, 0x06DD, 0x070F, 0x0890, 0x0891, 0x08E2,
                 0x110BD, 0x110CD}

    props = bytearray(MAX)
    for cp in range(MAX):
        g = gc[cp]
        if (g in ("Mn", "Me", "Cf", "Cc") and cp != 0x00AD and cp not in prepended) \
                or 0x1160 <= cp <= 0x11FF or 0xD7B0 <= cp <= 0xD7FF:
            w = 0
        elif eaw[cp] in ("W", "F") or emoji["Emoji_Presentation"][cp]:
            w = 2
        else:
            w = 1
        p = w
        if emoji["Emoji"][cp]:
            p |= 1 << 2
        if emoji["Extended_Pictographic"][cp]:
            p |= 1 << 3
        if emoji["Emoji_Modifier"][cp]:
            p |= 1 << 4
        if 0x1F1E6 <= cp <= 0x1F1FF:
            p |= 1 << 5
        if emoji["Emoji_Modifier_Base"][cp]:
            p |= 1 << 6
        props[cp] = p

    best = None
    for shift in (6, 7, 8):
        size = 1 << shift
        blocks, index = {}, []
        for start in range(0, MAX, size):
            blk = bytes(props[start:start + size])
            index.append(blocks.setdefault(blk, len(blocks)))
        idx_bytes = len(index) * (1 if len(blocks) <= 256 else 2)
        total = idx_bytes + len(blocks) * size
        if best is None or total < best[0]:
            best = (total, shift, blocks, index)
    total, shift, blocks, index = best
    wide_index = len(blocks) > 256

    def rows(values, per_line=32):
        values = list(values)
        for i in range(0, len(values), per_line):
            yield "        " + ", ".join(str(v) for v in values[i:i + per_line]) + ","

    lines = [
        "// <auto-generated>",
        f"// Generated by scripts/gen-unicode-tables.py from Unicode {version} (EastAsianWidth.txt,",
        "// DerivedGeneralCategory.txt, emoji-data.txt). Do not edit; re-run the script instead.",
        "// </auto-generated>",
        "",
        "namespace ErikTerm.Core;",
        "",
        "internal static class UnicodeTables",
        "{",
        f"    public const string UnicodeVersion = \"{version}\";",
        f"    public const int Shift = {shift};",
        f"    public const int Mask = {(1 << shift) - 1};",
        "",
        f"    /// <summary>Block number for each {1 << shift}-code-point block.</summary>",
        f"    public static ReadOnlySpan<{'ushort' if wide_index else 'byte'}> Index =>",
        "    [",
        *rows(index),
        "    ];",
        "",
        f"    /// <summary>{len(blocks)} deduplicated blocks of property bytes.</summary>",
        "    public static ReadOnlySpan<byte> Blocks =>",
        "    [",
        *rows(b for blk in blocks for b in blk),
        "    ];",
        "}",
        "",
    ]
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"Unicode {version}: block size {1 << shift}, {len(blocks)} blocks, {total} bytes -> {out}")


if __name__ == "__main__":
    main()
