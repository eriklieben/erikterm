#!/usr/bin/env bash
# Verifies the FT_FaceRec / FT_SizeRec / FT_GlyphSlotRec / FT_Bitmap_Size / FT_MM_Var / FT_Var_Axis /
# FT_Var_Named_Style / FT_SfntName offsets hard-coded in
# src/ErikTerm.App/Native/FreeType.cs against the installed FreeType headers, for both data models:
# LP64 (Linux/macOS) and LLP64 (64-bit Windows). Needs clang; no Windows SDK or cross toolchain —
# libc headers are stubbed because the public FreeType structs only depend on stddef/limits.
set -euo pipefail
FT_INCLUDE="${FT_INCLUDE:-$(pkg-config --cflags-only-I freetype2 2>/dev/null | sed 's/-I//g' | awk '{print $1}')}"
FT_INCLUDE="${FT_INCLUDE:-/usr/include/freetype2}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

mkdir "$tmp/stub"
for h in string.h stdio.h stdlib.h errno.h; do : > "$tmp/stub/$h"; done
echo 'typedef int jmp_buf[16];' > "$tmp/stub/setjmp.h"

cat > "$tmp/check.c" <<'EOF'
#include <stddef.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_MULTIPLE_MASTERS_H
#include FT_SFNT_NAMES_H
#define CHECK(expr, lp64, llp64) _Static_assert((expr) == (sizeof(long) == 8 ? (lp64) : (llp64)), #expr);
CHECK(sizeof(FT_Pos), 8, 4)
CHECK(sizeof(FT_Long), 8, 4)
CHECK(sizeof(FT_ULong), 8, 4)
CHECK(offsetof(FT_FaceRec, face_flags), 16, 8)
CHECK(offsetof(FT_FaceRec, family_name), 40, 24)
CHECK(offsetof(FT_FaceRec, style_name), 48, 32)
CHECK(offsetof(FT_FaceRec, num_fixed_sizes), 56, 40)
CHECK(offsetof(FT_FaceRec, available_sizes), 64, 48)
CHECK(offsetof(FT_FaceRec, glyph), 152, 120)
CHECK(offsetof(FT_FaceRec, size), 160, 128)
CHECK(offsetof(FT_SizeRec, metrics) + offsetof(FT_Size_Metrics, ascender), 24 + 24, 24 + 12)
CHECK(offsetof(FT_SizeRec, metrics) + offsetof(FT_Size_Metrics, descender), 24 + 32, 24 + 16)
CHECK(offsetof(FT_SizeRec, metrics) + offsetof(FT_Size_Metrics, height), 24 + 40, 24 + 20)
CHECK(sizeof(FT_Bitmap_Size), 32, 16)
CHECK(offsetof(FT_Bitmap_Size, height), 0, 0)
CHECK(offsetof(FT_Bitmap_Size, width), 2, 2)
CHECK(offsetof(FT_Bitmap_Size, y_ppem), 24, 12)
CHECK(offsetof(FT_GlyphSlotRec, advance.x), 128, 88)
CHECK(offsetof(FT_GlyphSlotRec, bitmap.rows), 152, 104)
CHECK(offsetof(FT_GlyphSlotRec, bitmap.width), 156, 108)
CHECK(offsetof(FT_GlyphSlotRec, bitmap.pitch), 160, 112)
CHECK(offsetof(FT_GlyphSlotRec, bitmap.buffer), 168, 120)
CHECK(offsetof(FT_GlyphSlotRec, bitmap.pixel_mode), 178, 130)
CHECK(offsetof(FT_GlyphSlotRec, bitmap_left), 192, 144)
CHECK(offsetof(FT_GlyphSlotRec, bitmap_top), 196, 148)
CHECK(FT_FACE_FLAG_MULTIPLE_MASTERS, 1 << 8, 1 << 8)
CHECK(sizeof(FT_Fixed), 8, 4)
CHECK(offsetof(FT_MM_Var, num_axis), 0, 0)
CHECK(offsetof(FT_MM_Var, num_namedstyles), 8, 8)
CHECK(offsetof(FT_MM_Var, axis), 16, 16)
CHECK(offsetof(FT_MM_Var, namedstyle), 24, 24)
CHECK(sizeof(FT_Var_Axis), 48, 32)
CHECK(offsetof(FT_Var_Axis, tag), 32, 20)
CHECK(sizeof(FT_Var_Named_Style), 16, 16)
CHECK(offsetof(FT_Var_Named_Style, coords), 0, 0)
CHECK(offsetof(FT_Var_Named_Style, strid), 8, 8)
CHECK(sizeof(FT_SfntName), 24, 24)
CHECK(offsetof(FT_SfntName, platform_id), 0, 0)
CHECK(offsetof(FT_SfntName, encoding_id), 2, 2)
CHECK(offsetof(FT_SfntName, language_id), 4, 4)
CHECK(offsetof(FT_SfntName, name_id), 6, 6)
CHECK(offsetof(FT_SfntName, string), 8, 8)
CHECK(offsetof(FT_SfntName, string_len), 16, 16)
EOF

for target in x86_64-pc-linux-gnu aarch64-linux-gnu x86_64-pc-windows-msvc aarch64-pc-windows-msvc x86_64-w64-windows-gnu; do
  clang --target="$target" -nostdlibinc -I"$tmp/stub" -I"$FT_INCLUDE" -fsyntax-only "$tmp/check.c"
  echo "ok  $target"
done
