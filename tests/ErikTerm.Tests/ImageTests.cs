using System.IO.Compression;
using System.Text;
using ErikTerm.Core;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

public class ImageTests
{
    private static (Terminal T, List<string> Replies) Term(int cols = 20, int rows = 8, int scrollback = 100)
    {
        var t = new Terminal(cols, rows, scrollback) { CellPixelWidth = 10, CellPixelHeight = 20 };
        var replies = new List<string>();
        t.Response = d => replies.Add(Encoding.UTF8.GetString(d));
        return (t, replies);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static string Kitty(string control, string payload = "") => $"\e_G{control};{payload}\e\\";

    private static string Rgba(int w, int h, uint color = 0xFF0000FF)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = (byte)(color >> 24); px[i * 4 + 1] = (byte)(color >> 16); px[i * 4 + 2] = (byte)(color >> 8); px[i * 4 + 3] = (byte)color;
        }
        return Convert.ToBase64String(px);
    }

    private static string PngBase64(int w, int h) => Convert.ToBase64String(PngTests.EncodeRgba(w, h, (x, y) => (uint)(x * 20 << 24 | y * 20 << 16 | 0xFF)));

    /// <summary>Cells flagged as image tiles on a screen row.</summary>
    private static int TileCount(Terminal t, int y)
    {
        int n = 0;
        foreach (var c in t.GetScreenRow(y).Cells) if ((c.Flags & CellAttributes.Image) != 0) n++;
        return n;
    }

    /// <summary>Tile counts of the first <paramref name="rows"/> screen rows.</summary>
    private static int[] TileCounts(Terminal t, int rows) => Enumerable.Range(0, rows).Select(y => TileCount(t, y)).ToArray();

    /// <summary>The inline (iTerm2) image whose tile is at the top-left cell.</summary>
    private static InlineImage InlineAtHome(Terminal t) =>
        t.Images.GetSlot(ImageStore.UnpackTile(t.GetScreenRow(0).Cells[0].Fg).Slot)!.Value;

    /// <summary>Claude Code's iTerm2 writer: <c>ESC]1337;File=inline=1;size=N;width=C;height=R;preserveAspectRatio=1;doNotMoveCursor=1:B64 BEL</c>.</summary>
    private static string ClaudeITerm(string b64, int cols, int rows)
    {
        int pad = b64.EndsWith("==") ? 2 : b64.EndsWith('=') ? 1 : 0;
        return $"\e]1337;File=inline=1;size={b64.Length / 4 * 3 - pad};width={cols};height={rows};preserveAspectRatio=1;doNotMoveCursor=1:{b64}\a";
    }

    /// <summary>A mutable time source for <see cref="ImageStore.Clock"/>.</summary>
    private sealed class FakeClock
    {
        public long Now;
    }

    // ---- Parser ---------------------------------------------------------------------------------------

    [Test]
    public async Task Large_iterm_payloads_pass_the_osc_cap()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        // ~300 KB of base64: beyond the 64 KB OSC cap that applies to everything else.
        string b64 = Convert.ToBase64String(PngTests.EncodeRgba(300, 300, (x, y) => (uint)((x ^ y) * 0x01010100) | 0xFF));

        // Act
        Feed(t, "\e[2;3H" + ClaudeITerm(b64, 16, 6));

        // Assert
        await Assert.That(b64.Length).IsGreaterThan(64 * 1024);
        var img = await Assert.That(t.Images.Images).HasSingleItem();
        await Assert.That((img!.Width, img.Height)).IsEqualTo((300, 300));
        await Assert.That(TileCount(t, 1)).IsEqualTo(16);
    }

    // ---- iTerm2 -------------------------------------------------------------------------------------

    [Test]
    public async Task Claude_code_iterm_sequence_fills_its_box_without_moving_the_cursor()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        string b64 = PngBase64(32, 24);

        // Act
        Feed(t, "\e7\e[3;5H" + ClaudeITerm(b64, 16, 6) + "\e8");
        var (slot, r, col) = ImageStore.UnpackTile(t.GetScreenRow(2).Cells[4].Fg);
        var inline = t.Images.GetSlot(slot)!.Value;

        // Assert
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 0));
        await Assert.That(TileCounts(t, 10)).IsEquivalentTo([0, 0, 16, 16, 16, 16, 16, 16, 0, 0], CollectionOrdering.Matching);
        await Assert.That((r, col)).IsEqualTo((0, 0));
        await Assert.That((inline.Columns, inline.Rows)).IsEqualTo((16, 6));
        // 32×24 px into 160×120 px: aspect kept (scaled to 160×120 exactly here), centred.
        await Assert.That(inline.ContentW).IsEqualTo(16f).Within(0.0005f);
        await Assert.That(inline.ContentH).IsEqualTo(6f).Within(0.0005f);
    }

    [Test]
    public async Task Repainting_the_same_iterm_picture_reuses_the_decoded_image_and_the_slot()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        string paint = "\e7\e[3;5H" + ClaudeITerm(PngBase64(32, 24), 16, 6) + "\e8";
        Feed(t, paint);
        uint tileBefore = t.GetScreenRow(2).Cells[4].Fg;

        // Act
        Feed(t, paint);

        // Assert
        await Assert.That(t.Images.Images).HasSingleItem();
        await Assert.That(t.GetScreenRow(2).Cells[4].Fg).IsEqualTo(tileBefore);
    }

    [Test]
    public async Task Iterm_image_covers_its_cells()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, ClaudeITerm(PngBase64(8, 8), 4, 2));

        // Assert
        await Assert.That(TileCount(t, 0)).IsEqualTo(4);
    }

    [Test]
    [Arguments("\e[1;2Hxy", 0, 2)]    // print over two cells
    [Arguments("\e[2;1H\e[2X", 1, 2)] // ECH: Claude Code erases an image it can't draw this way
    public async Task Text_and_erase_over_an_iterm_image_erase_those_cells(string sequence, int row, int expectedTiles)
    {
        // Arrange
        var (t, _) = Term(40, 10);
        Feed(t, ClaudeITerm(PngBase64(8, 8), 4, 2));

        // Act
        Feed(t, sequence);

        // Assert
        await Assert.That(TileCount(t, row)).IsEqualTo(expectedTiles);
    }

    [Test]
    public async Task Erasing_the_display_removes_an_iterm_image()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        Feed(t, ClaudeITerm(PngBase64(8, 8), 4, 2));

        // Act
        Feed(t, "\e[2J");

        // Assert
        await Assert.That(TileCount(t, 0) + TileCount(t, 1)).IsEqualTo(0);
    }

    private static readonly string ItermAtTheLastLine = "\e[4;1H\e]1337;File=inline=1;width=3;height=2:" + PngBase64(6, 4) + "\a";

    [Test]
    public async Task Iterm_image_moves_the_cursor_and_scrolls_the_screen()
    {
        // Arrange
        var (t, _) = Term(20, 4, scrollback: 2);

        // Act
        Feed(t, ItermAtTheLastLine);

        // Assert
        // Two rows from the last line: the screen scrolled once; the cursor is right of the image on its last row.
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((3, 3));
        await Assert.That(TileCount(t, 2)).IsEqualTo(3);
        await Assert.That(TileCount(t, 3)).IsEqualTo(3);
    }

    [Test]
    public async Task Iterm_image_scrolls_away_with_the_text()
    {
        // Arrange
        var (t, _) = Term(20, 4, scrollback: 2);
        Feed(t, ItermAtTheLastLine);

        // Act
        Feed(t, "\n\n");

        // Assert
        await Assert.That(TileCount(t, 1)).IsEqualTo(3);
    }

    [Test]
    public async Task Iterm_image_scrolls_out_of_the_scrollback()
    {
        // Arrange
        var (t, _) = Term(20, 4, scrollback: 2);
        Feed(t, ItermAtTheLastLine + "\n\n");

        // Act
        Feed(t, "\n\n\n\n");

        // Assert
        await Assert.That(TileCounts(t, 4)).IsEquivalentTo([0, 0, 0, 0], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Iterm_width_in_pixels_and_auto_height()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, "\e]1337;File=inline=1;width=35px;height=auto:" + PngBase64(10, 20) + "\a");
        var inline = InlineAtHome(t);

        // Assert
        await Assert.That((inline.Columns, inline.Rows)).IsEqualTo((4, 4)); // 35×70 px → 4×4 cells
        await Assert.That(inline.ContentW).IsEqualTo(3.5f).Within(0.0005f);
    }

    [Test]
    public async Task Iterm_percent_width_without_preserved_aspect_ratio_stretches()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, "\e[H\e[2J\e]1337;File=inline=1;width=50%;height=2;preserveAspectRatio=0:" + PngBase64(10, 20) + "\a");
        var inline = InlineAtHome(t);

        // Assert
        await Assert.That((inline.Columns, inline.Rows)).IsEqualTo((20, 2));
        await Assert.That((inline.ContentW, inline.ContentH)).IsEqualTo((20f, 2f)); // stretched
    }

    [Test]
    public async Task Iterm_auto_size_uses_native_pixels()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, "\e[H\e[2J\e]1337;File=inline=1:" + PngBase64(25, 30) + "\a");
        var inline = InlineAtHome(t);

        // Assert
        await Assert.That((inline.Columns, inline.Rows)).IsEqualTo((3, 2));
        await Assert.That((inline.ContentW, inline.ContentH)).IsEqualTo((2.5f, 1.5f));
    }

    [Test]
    public async Task Iterm_file_without_inline_is_a_download()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, "\e[H\e[2J\e]1337;File=width=2;height=2:" + PngBase64(4, 4) + "\a"); // inline=0: a download

        // Assert
        await Assert.That(TileCount(t, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task Iterm_multipart_file()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        string b64 = PngBase64(16, 16);
        var sb = new StringBuilder("\e]1337;MultipartFile=inline=1;width=4;height=2\a");
        for (int i = 0; i < b64.Length; i += 50) sb.Append("\e]1337;FilePart=").Append(b64.AsSpan(i, Math.Min(50, b64.Length - i))).Append('\a');
        sb.Append("\e]1337;FileEnd\a");

        // Act
        Feed(t, sb.ToString());

        // Assert
        await Assert.That(TileCount(t, 0)).IsEqualTo(4);
        await Assert.That(TileCount(t, 1)).IsEqualTo(4);
    }

    [Test]
    public async Task Undecodable_iterm_file_shows_a_placeholder_box()
    {
        // Arrange
        var (t, _) = Term(40, 10);

        // Act
        Feed(t, "\e]1337;File=inline=1;width=4;height=2:" + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]) + "\a");

        // Assert
        await Assert.That(TileCount(t, 0)).IsEqualTo(4);
    }

    [Test]
    public async Task Set_user_var_still_works()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e]1337;SetUserVar=IS_NVIM=dHJ1ZQ==\a");

        // Assert
        await Assert.That(t.UserVars["IS_NVIM"]).IsEqualTo("true");
    }

    // ---- Kitty ---------------------------------------------------------------------------------------

    [Test]
    public async Task Kitty_transmit_and_display_with_replies()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("a=T,f=32,s=2,v=2,i=7", Rgba(2, 2)));

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=7;OK\e\\"], CollectionOrdering.Matching);
        var p = await Assert.That(t.Images.Placements(false)).HasSingleItem();
        await Assert.That((p!.Image.Id, p.Line, p.Column, p.Columns, p.Rows)).IsEqualTo((7u, 0L, 0, 1, 1));
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((1, 0)); // cursor after the image
    }

    [Test]
    public async Task Kitty_quiet_modes_and_missing_ids_limit_the_replies()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("a=T,f=32,s=2,v=2,i=8,q=1", Rgba(2, 2)));   // q=1: no OK
        Feed(t, Kitty("a=T,f=32,s=2,v=2,i=9,q=1", Rgba(1, 1)));   // q=1: errors still reported
        Feed(t, Kitty("a=T,f=32,s=2,v=2,i=10,q=2", Rgba(1, 1)));  // q=2: nothing
        Feed(t, Kitty("a=T,f=32,s=1,v=1", Rgba(1, 1)));           // no id: no reply

        // Assert
        await Assert.That(replies).HasSingleItem();
        await Assert.That(replies[0]).StartsWith("\e_Gi=9;ENODATA:");
    }

    [Test]
    public async Task Kitty_query_answers_claude_codes_probe()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("i=31,s=1,v=1,a=q,t=d,f=24", "AAAA"));

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=31;OK\e\\"], CollectionOrdering.Matching);
        await Assert.That(t.Images.Images).IsEmpty(); // queries don't store anything
    }

    [Test]
    public async Task Kitty_query_with_an_undecodable_png_reports_an_error()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("i=32,a=q,t=d,f=100", "bm90IGEgcG5n"));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=32;E");
    }

    private const uint ClaudeImageId = 0xABCDEF;

    private static readonly string ClaudePngBase64 =
        Convert.ToBase64String(PngTests.EncodeRgba(120, 90, (x, y) => (uint)((x * 2) << 24 | (y * 2) << 16 | 0x80FF)));

    /// <summary>p9s(): a=T,U=1,q=2,f=100,i=…,c=…,r=…,m=1 then m=1,q=2 … m=0,q=2, 4096 base64 bytes each.</summary>
    private static string ClaudeChunkedVirtualPng()
    {
        string b64 = ClaudePngBase64;
        var sb = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 4096)
        {
            string m = i + 4096 >= b64.Length ? "m=0" : "m=1";
            string control = i == 0 ? $"a=T,U=1,q=2,f=100,i={ClaudeImageId},c=16,r=6,{m}" : $"{m},q=2";
            sb.Append(Kitty(control, b64.Substring(i, Math.Min(4096, b64.Length - i))));
        }
        return sb.ToString();
    }

    /// <summary>f9s(): 38;2;r;g;b + U+10EEEE + row diacritic + column diacritic per cell, then 39m.</summary>
    private static string ClaudePlaceholders(uint id)
    {
        var text = new StringBuilder();
        for (int r = 0; r < 6; r++)
        {
            text.Append($"\e[{r + 1};1H\e[38;2;{id >> 16};{(id >> 8) & 0xFF};{id & 0xFF}m");
            for (int c = 0; c < 16; c++)
                text.Append(char.ConvertFromUtf32(KittyPlaceholder.Codepoint))
                    .Append(char.ConvertFromUtf32(KittyPlaceholder.Diacritic(r)))
                    .Append(char.ConvertFromUtf32(KittyPlaceholder.Diacritic(c)));
            text.Append("\e[39m");
        }
        return text.ToString();
    }

    /// <summary>The grapheme cluster at a cell, decoded as a placeholder (spans can't live in an async test).</summary>
    private static (bool Found, (int Row, int Col, int High) Decoded) PlaceholderAt(Row row, int col)
    {
        bool found = Terminal.TryGetCluster(row, col, out var cluster);
        return (found, KittyPlaceholder.Decode(cluster));
    }

    [Test]
    public async Task Kitty_chunked_png_with_claude_codes_framing_makes_a_virtual_placement()
    {
        // Arrange
        var (t, replies) = Term(40, 10);

        // Act
        Feed(t, ClaudeChunkedVirtualPng());

        // Assert
        await Assert.That(ClaudePngBase64.Length).IsGreaterThan(4096);
        await Assert.That(replies).IsEmpty();
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 0)); // virtual placements don't move the cursor
        var vp = await Assert.That(t.Images.FindVirtual(ClaudeImageId, 0, false)).IsNotNull();
        await Assert.That((vp.Image.Width, vp.Image.Height, vp.Columns, vp.Rows)).IsEqualTo((120, 90, 16, 6));
        await Assert.That(t.Images.HasVirtualPlacements).IsTrue();
    }

    [Test]
    public async Task Kitty_unicode_placeholders_from_claude_code_name_the_image_and_the_cell()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        Feed(t, ClaudeChunkedVirtualPng());

        // Act
        Feed(t, ClaudePlaceholders(ClaudeImageId));
        var row = t.GetScreenRow(2);
        var (found, decoded) = PlaceholderAt(row, 5);

        // Assert
        await Assert.That(row.Cells[5].Rune).IsEqualTo(KittyPlaceholder.Codepoint);
        await Assert.That(KittyPlaceholder.ImageIdFromColor(row.Cells[5].Fg)).IsEqualTo(ClaudeImageId);
        await Assert.That(found).IsTrue();
        await Assert.That(decoded).IsEqualTo((2, 5, -1));
        await Assert.That(row.Cells[16].Rune).IsEqualTo(0); // 16 cells wide
    }

    [Test]
    public async Task Kitty_delete_by_id_frees_a_virtual_placement()
    {
        // Arrange
        var (t, _) = Term(40, 10);
        Feed(t, ClaudeChunkedVirtualPng() + ClaudePlaceholders(ClaudeImageId));

        // Act
        Feed(t, Kitty($"a=d,d=I,i={ClaudeImageId},q=2")); // a=d,d=I,i=…,q=2

        // Assert
        await Assert.That(t.Images.FindVirtual(ClaudeImageId, 0, false)).IsNull();
        await Assert.That(t.Images.Images).IsEmpty();
    }

    /// <summary>A 4×3 RGB image, zlib-compressed, transmitted (not placed) as id 5.</summary>
    private static string ZlibRgbTransmission()
    {
        var rgb = new byte[3 * 4 * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)i;
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(rgb);
        return Kitty("a=t,f=24,s=4,v=3,o=z,i=5", Convert.ToBase64String(z.ToArray()));
    }

    [Test]
    public async Task Kitty_raw_rgb_zlib_transmission()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, ZlibRgbTransmission());

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=5;OK\e\\"], CollectionOrdering.Matching);
        var img = await Assert.That(t.Images.ById(5)).IsNotNull();
        await Assert.That((img.Width, img.Height)).IsEqualTo((4, 3));
        await Assert.That(img.Rgba!.AsSpan(4, 4).ToArray()).IsEquivalentTo(new byte[] { 3, 4, 5, 255 }, CollectionOrdering.Matching);
        await Assert.That(t.Images.Placements(false)).IsEmpty(); // a=t only transmits
    }

    [Test]
    public async Task Kitty_put_places_a_transmitted_image()
    {
        // Arrange
        var (t, replies) = Term();
        Feed(t, ZlibRgbTransmission());
        replies.Clear();

        // Act
        Feed(t, "\e[3;4H" + Kitty("a=p,i=5,p=2,c=5,r=2,x=1,y=1,w=2,h=1,z=-1,C=1"));

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=5,p=2;OK\e\\"], CollectionOrdering.Matching);
        var p = await Assert.That(t.Images.Placements(false)).HasSingleItem();
        await Assert.That((p!.Line - t.ScreenTopLine, p.Column, p.Columns, p.Rows, p.Z)).IsEqualTo((2L, 3, 5, 2, -1));
        await Assert.That((p.SourceX, p.SourceY, p.SourceWidth, p.SourceHeight)).IsEqualTo((1, 1, 2, 1));
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((3, 2)); // C=1
    }

    [Test]
    public async Task Kitty_put_with_the_same_placement_id_replaces()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, ZlibRgbTransmission());
        Feed(t, "\e[3;4H" + Kitty("a=p,i=5,p=2,c=5,r=2,x=1,y=1,w=2,h=1,z=-1,C=1"));

        // Act
        Feed(t, Kitty("a=p,i=5,p=2,q=2"));

        // Assert
        await Assert.That(t.Images.Placements(false)).HasSingleItem();
    }

    [Test]
    public async Task Kitty_put_of_an_unknown_image_reports_enoent()
    {
        // Arrange
        var (t, replies) = Term();
        Feed(t, ZlibRgbTransmission());
        replies.Clear();

        // Act
        Feed(t, Kitty("a=p,i=99"));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=99;ENOENT");
    }

    [Test]
    public async Task Kitty_image_numbers_get_an_id()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("a=t,f=32,s=1,v=1,I=42", Rgba(1, 1)));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).Matches(@"^\e_Gi=\d+,I=42;OK\e\\$");
        await Assert.That(t.Images.ByNumber(42)).IsNotNull();
    }

    private static readonly string TenByTwentyAtRowTwo = "\e[2;1H" + Kitty("a=T,f=32,s=10,v=20,i=1,C=1,q=2", Rgba(10, 20));

    [Test]
    public async Task Kitty_placements_scroll_with_the_text()
    {
        // Arrange
        var (t, _) = Term(20, 4, scrollback: 3);
        Feed(t, TenByTwentyAtRowTwo);
        var p = t.Images.Placements(false).Single();
        long line = p.Line;

        // Act
        Feed(t, "\e[4;1H\n\n");

        // Assert
        await Assert.That(p.Line).IsEqualTo(line); // absolute: same content line
        await Assert.That(p.Line - t.ScreenTopLine).IsEqualTo(-1);
    }

    [Test]
    public async Task Kitty_placements_leave_with_the_scrollback()
    {
        // Arrange
        var (t, _) = Term(20, 4, scrollback: 3);
        Feed(t, TenByTwentyAtRowTwo);
        Feed(t, "\e[4;1H\n\n");

        // Act
        Feed(t, "\n\n\n\n\n");      // beyond the 3-line scrollback
        Feed(t, Kitty("a=T,f=32,s=1,v=1,i=2,q=2", Rgba(1, 1))); // placing prunes

        // Assert
        await Assert.That(t.Images.Placements(false)).DoesNotContain(x => x.Image.Id == 1);
    }

    [Test]
    public async Task Kitty_placements_move_with_region_scrolls()
    {
        // One sequence: each scroll moves the placement from where the previous one left it.
        // Arrange
        var (t, _) = Term(20, 6);
        Feed(t, "\e[4;1H" + Kitty("a=T,f=32,s=1,v=1,i=1,C=1,q=2", Rgba(1, 1)));
        var p = t.Images.Placements(false).Single();

        // Act
        Feed(t, "\e[2;5r\e[5;1H\n"); // scroll region 2..5 up by one
        long afterRegionScroll = p.Line - t.ScreenTopLine;
        Feed(t, "\e[2;1H\eM");       // reverse index at the top margin: down by one
        long afterReverseIndex = p.Line - t.ScreenTopLine;
        Feed(t, "\e[2;1H\e[3M");     // delete lines: scrolls out of the region top → deleted

        // Assert
        await Assert.That(afterRegionScroll).IsEqualTo(2);
        await Assert.That(afterReverseIndex).IsEqualTo(3);
        await Assert.That(t.Images.Placements(false)).IsEmpty();
    }

    [Test]
    public async Task Kitty_delete_selectors()
    {
        // One sequence: each selector deletes from what the previous ones left, so it stays one test.
        // Arrange
        var (t, _) = Term(20, 6);
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)));
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=2,q=2", Rgba(1, 1)));
        Feed(t, "\e[1;1H" + Kitty("a=p,i=1,p=1,C=1,q=2") + "\e[2;3H" + Kitty("a=p,i=1,p=2,z=5,C=1,q=2") + "\e[4;6H" + Kitty("a=p,i=2,C=1,q=2"));
        var list = t.Images.Placements(false);
        int placed = list.Count;

        // Act
        Feed(t, Kitty("a=d,d=p,x=3,y=2")); // the cell of placement 2
        int afterCellDelete = list.Count;
        Feed(t, Kitty("a=d,d=i,i=1"));      // lower case keeps the data
        int afterIdDelete = list.Count;
        bool image1KeptByLowerCase = t.Images.ById(1) is not null;
        Feed(t, "\e[4;6H" + Kitty("a=d,d=C")); // at the cursor, freeing the data
        int afterCursorDelete = list.Count;
        bool image2FreedByUpperCase = t.Images.ById(2) is null;
        Feed(t, Kitty("a=p,i=1,z=3,C=1,q=2") + Kitty("a=d,d=z,z=3"));
        int afterZDelete = list.Count;
        Feed(t, Kitty("a=p,i=1,C=1,q=2") + Kitty("a=d"));
        int afterDeleteAll = list.Count;
        bool image1KeptByDeleteAll = t.Images.ById(1) is not null;
        Feed(t, Kitty("a=d,d=A") + Kitty("a=d,d=I,i=1"));

        // Assert
        await Assert.That(placed).IsEqualTo(3);
        await Assert.That(afterCellDelete).IsEqualTo(2);
        await Assert.That(afterIdDelete).IsEqualTo(1);
        await Assert.That(image1KeptByLowerCase).IsTrue();
        await Assert.That(afterCursorDelete).IsEqualTo(0);
        await Assert.That(image2FreedByUpperCase).IsTrue();
        await Assert.That(afterZDelete).IsEqualTo(0);
        await Assert.That(afterDeleteAll).IsEqualTo(0);
        await Assert.That(image1KeptByDeleteAll).IsTrue();
        await Assert.That(t.Images.Images).IsEmpty();
    }

    [Test]
    public async Task Kitty_alt_screen_images_go_away_with_it()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, Kitty("a=T,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)));
        Feed(t, "\e[?1049h" + Kitty("a=T,f=32,s=1,v=1,i=2,q=2", Rgba(1, 1)));
        var altPlacementsBefore = t.Images.Placements(true).ToList();

        // Act
        Feed(t, "\e[?1049l");

        // Assert
        await Assert.That(altPlacementsBefore).HasSingleItem();
        await Assert.That(t.Images.Placements(true)).IsEmpty();
        await Assert.That(t.Images.ById(2)).IsNull();
        await Assert.That(t.Images.Placements(false)).HasSingleItem();
    }

    // ---- Kitty files ----------------------------------------------------------------------------------

    /// <summary>A unique temporary directory, deleted with everything in it.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erikterm-img-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name, byte[] contents)
        {
            string file = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(file, contents);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Test]
    public async Task Kitty_file_transmission_reads_the_file_and_never_deletes_it()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        using var dir = new TempDir();
        string file = dir.File("pic.rgba", [1, 2, 3, 4, 9, 9, 9, 9]);

        // Act
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=1", B64(file)));
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,O=4,S=4,i=2", B64(file)));

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=1;OK\e\\", "\e_Gi=2;OK\e\\"], CollectionOrdering.Matching);
        await Assert.That(t.Images.ById(2)!.Rgba).IsEquivalentTo(new byte[] { 9, 9, 9, 9 }, CollectionOrdering.Matching);
        await Assert.That(File.Exists(file)).IsTrue(); // t=f never deletes
    }

    [Test]
    public async Task Kitty_temp_file_transmission_reads_and_deletes_a_marked_temp_file()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        using var dir = new TempDir();
        // t=t: read and deleted, but only from a temp dir with the marker in the name.
        string temp = dir.File("tty-graphics-protocol-x.rgba", [5, 6, 7, 8]);

        // Act
        Feed(t, Kitty("a=t,t=t,f=32,s=1,v=1,i=3", B64(temp)));

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e_Gi=3;OK\e\\"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(temp)).IsFalse();
    }

    [Test]
    public async Task Kitty_temp_file_transmission_refuses_a_directory_that_only_starts_like_the_temp_directory()
    {
        // Arrange: nothing has to exist, the place is refused before the file is opened.
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        string sibling = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + "-sibling";
        string file = Path.Combine(sibling, "tty-graphics-protocol-x.rgba");

        // Act
        Feed(t, Kitty("a=t,t=t,f=32,s=1,v=1,i=7", B64(file)));

        // Assert
        await Assert.That(replies).IsEquivalentTo(
            ["\e_Gi=7;EPERM:temporary file is not in a temp directory or lacks tty-graphics-protocol in its name\e\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Kitty_temp_file_transmission_refuses_a_file_without_the_marker()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        using var dir = new TempDir();
        string file = dir.File("pic.rgba", [1, 2, 3, 4, 9, 9, 9, 9]);

        // Act
        Feed(t, Kitty("a=t,t=t,f=32,s=1,v=1,i=4", B64(file)));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=4;EPERM");
        await Assert.That(File.Exists(file)).IsTrue();
    }

    [Test]
    public async Task Kitty_file_transmission_refuses_files_that_are_not_regular()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        using var dir = new TempDir();

        // Act
        // Not regular files: devices, /proc, directories.
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=5", B64("/dev/zero")));
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=6", B64("/proc/self/environ")));
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=7", B64(dir.Path)));

        // Assert
        await Assert.That(replies.Count).IsEqualTo(3);
        foreach (var r in replies) await Assert.That(r).Matches(@";E[A-Z]+:");
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task Kitty_file_transmission_of_a_fifo_fails_without_blocking()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true; // off by default (allow_kitty_file_transmission)
        using var dir = new TempDir();
        string fifo = Path.Combine(dir.Path, "fifo");
        using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo)) mk.WaitForExit();

        // Act
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=8", B64(fifo))); // must not block

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=8;E");
    }

    [Test]
    public async Task Kitty_file_transmission_is_refused_when_image_files_are_disallowed()
    {
        // Arrange
        var (t, replies) = Term();
        using var dir = new TempDir();
        string file = dir.File("pic.rgba", [1, 2, 3, 4, 9, 9, 9, 9]);
        t.AllowImageFiles = false;

        // Act
        Feed(t, Kitty("a=t,t=f,f=32,s=1,v=1,i=9", B64(file)));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=9;EPERM");
    }

    [Test]
    public async Task Kitty_placement_survives_reflow_on_its_row()
    {
        // Arrange
        var (t, _) = Term(10, 6);
        Feed(t, "aaaaaaaaaaaaaaa\r\nb" + Kitty("a=T,f=32,s=1,v=1,i=1,C=1,q=2", Rgba(1, 1))); // wrapped line, then the image row
        var p = t.Images.Placements(false).Single();
        long rowBefore = p.Line - t.ScreenTopLine;

        // Act
        t.Resize(20, 6); // the wrapped line now fits on one row: the image row moves up

        // Assert
        await Assert.That(rowBefore).IsEqualTo(2);
        await Assert.That(p.Line - t.FirstLine - t.ScrollbackCount).IsEqualTo(1);
        await Assert.That(t.GetLineText((int)(p.Line - t.ScreenTopLine))).IsEqualTo("b");
    }

    // ---- Sixel --------------------------------------------------------------------------------------

    [Test]
    public async Task ATallSixelWithinThePixelLimitDecodesWhole()
    {
        // Arrange: no raster attributes, so the canvas grows as it goes. The first band's width arrives in two steps, which
        // left the canvas wider than the image, and the spare width then used up the pixel limit.
        var data = Encoding.ASCII.GetBytes("#1!1792~!608~-" + string.Concat(Enumerable.Repeat("#1!2400~-", 2999)));

        // Act
        var img = Sixel.Decode(data);

        // Assert
        await Assert.That(img.Width).IsEqualTo(2400);
        await Assert.That(img.Height).IsEqualTo(18000);
    }

    [Test]
    public async Task Sixel_decodes_palette_repeats_and_bands()
    {
        // Act
        // Register 1 = red (RGB percent), register 2 = HLS blue; 4 columns of red over 6 rows, then a band of blue.
        var img = Sixel.Decode("\"1;1;5;12#1;2;100;0;0#2;1;0;50;100#1!4~-#2~~"u8);
        int o = (6 * 5 + 1) * 4;

        // Assert
        await Assert.That((img.Width, img.Height)).IsEqualTo((5, 12));
        await Assert.That(img.Rgba.AsSpan(0, 4).ToArray()).IsEquivalentTo(new byte[] { 255, 0, 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(img.Rgba[4 * 4 + 3]).IsEqualTo((byte)0); // column 4 never painted: transparent
        await Assert.That(img.Rgba.AsSpan(o, 4).ToArray()).IsEquivalentTo(new byte[] { 0, 0, 255, 255 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Sixel_is_placed_at_the_cursor()
    {
        // Arrange
        var (t, _) = Term(20, 8);

        // Act
        Feed(t, "\e[2;3H\eP0;1;0q\"1;1;25;30#1;2;0;100;0#1!25~-!25~-!25~-!25~-!25~\e\\");

        // Assert
        await Assert.That(TileCount(t, 1)).IsEqualTo(3); // 25 px / 10 px cells
        await Assert.That(TileCount(t, 2)).IsEqualTo(3); // 30 px / 20 px cells → 2 rows
        await Assert.That(TileCount(t, 3)).IsEqualTo(0);
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((2, 3)); // next line, same column
    }

    [Test]
    public async Task Sixel_support_is_reported_in_da1()
    {
        // Arrange
        var (t, replies) = Term(20, 8);

        // Act
        Feed(t, "\e[c");

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).Contains(";4;");
    }

    // ---- Budget -------------------------------------------------------------------------------------

    [Test]
    public async Task Memory_budget_evicts_least_recently_used()
    {
        // Arrange
        var (t, _) = Term();
        t.Images.MemoryLimit = 3 * 100 * 100 * 4;
        for (uint i = 1; i <= 3; i++) Feed(t, Kitty($"a=t,f=32,s=100,v=100,i={i},q=2", Rgba(100, 100)));
        int storedBeforeEviction = t.Images.Images.Count;

        // Act
        Feed(t, Kitty("a=p,i=1,q=2")); // image 1 used again
        Feed(t, Kitty("a=t,f=32,s=100,v=100,i=4,q=2", Rgba(100, 100)));

        // Assert
        await Assert.That(storedBeforeEviction).IsEqualTo(3);
        await Assert.That(t.Images.ById(2)).IsNull(); // the least recently used went
        await Assert.That(t.Images.ById(1)).IsNotNull();
        await Assert.That(t.Images.ById(4)).IsNotNull();
        await Assert.That(t.Images.MemoryUsed).IsLessThanOrEqualTo(t.Images.MemoryLimit);
    }

    [Test]
    public async Task Image_larger_than_the_whole_budget_is_refused()
    {
        // Arrange
        var (t, replies) = Term();
        t.Images.MemoryLimit = 100;

        // Act
        Feed(t, Kitty("a=t,f=32,s=10,v=10,i=1", Rgba(10, 10)));

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).StartsWith("\e_Gi=1;ENOSPC");
    }

    [Test]
    public async Task Full_reset_removes_images()
    {
        // Arrange
        var (t, _) = Term(20, 8);
        Feed(t, Kitty("a=T,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)));

        // Act
        Feed(t, "\ec");

        // Assert
        await Assert.That(t.Images.Images).IsEmpty();
    }

    [Test]
    public async Task Window_size_reports()
    {
        // Arrange
        var (t, replies) = Term(20, 8);

        // Act
        Feed(t, "\e[16t\e[14t\e[18t");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[6;20;10t", "\e[4;160;200t", "\e[8;8;20t"], CollectionOrdering.Matching);
    }

    // ---- Kitty relative placements --------------------------------------------------------------------

    /// <summary>Images 1 and 2; image 1 placed at (row 2, col 3) as p=1, 2×2 cells.</summary>
    private static (Terminal T, List<string> Replies) WithParent()
    {
        var (t, replies) = Term(20, 8);
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)) + Kitty("a=t,f=32,s=1,v=1,i=2,q=2", Rgba(1, 1)));
        Feed(t, "\e[3;4H" + Kitty("a=p,i=1,p=1,c=2,r=2,q=2"));
        return (t, replies);
    }

    private static readonly string ChildOfParent = "\e[1;1H" + Kitty("a=p,i=2,p=5,P=1,Q=1,H=3,V=-1");

    [Test]
    public async Task Kitty_relative_placement_is_positioned_from_its_parent()
    {
        // Arrange
        var (t, replies) = WithParent();

        // Act
        Feed(t, ChildOfParent);
        var child = t.Images.Placements(false).Single(p => p.Image.Id == 2);

        // Assert
        var reply = await Assert.That(replies).HasSingleItem();
        await Assert.That(reply).IsEqualTo("\e_Gi=2,p=5;OK\e\\");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 0)); // relative placements never move the cursor
        await Assert.That((child.Line - t.ScreenTopLine, child.Column)).IsEqualTo((1L, 6)); // parent at (row 2, col 3) + (V=-1, H=3)
    }

    [Test]
    public async Task Kitty_relative_placement_chain_follows_its_parent()
    {
        // Arrange
        var (t, _) = WithParent();
        Feed(t, ChildOfParent);
        var list = t.Images.Placements(false);
        var child = list.Single(p => p.Image.Id == 2);

        // Act
        // A grandchild, then the parent is placed again elsewhere: the chain follows.
        Feed(t, Kitty("a=p,i=2,p=6,P=2,Q=5,H=1,V=1,q=2"));
        Feed(t, "\e[6;1H" + Kitty("a=p,i=1,p=1,q=2"));
        var grandchild = list.Single(p => p.PlacementId == 6);

        // Assert
        await Assert.That((child.Line - t.ScreenTopLine, child.Column)).IsEqualTo((4L, 3));
        await Assert.That((grandchild.Line - t.ScreenTopLine, grandchild.Column)).IsEqualTo((5L, 4));
    }

    [Test]
    public async Task Kitty_relative_placement_chain_dies_with_its_parent()
    {
        // Arrange
        var (t, _) = WithParent();
        Feed(t, ChildOfParent);
        Feed(t, Kitty("a=p,i=2,p=6,P=2,Q=5,H=1,V=1,q=2"));
        Feed(t, "\e[6;1H" + Kitty("a=p,i=1,p=1,q=2"));

        // Act
        Feed(t, Kitty("a=d,d=i,i=1,p=1,q=2")); // deleting the parent deletes the whole chain

        // Assert
        await Assert.That(t.Images.Placements(false)).IsEmpty();
    }

    /// <summary>Image 1 transmitted, nothing placed.</summary>
    private static (Terminal T, List<string> Replies) WithImageOne()
    {
        var (t, replies) = Term(20, 8);
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)));
        return (t, replies);
    }

    [Test]
    public async Task Kitty_relative_placement_without_its_parent_is_enoparent()
    {
        // Arrange
        var (t, replies) = WithImageOne();

        // Act
        Feed(t, Kitty("a=p,i=1,p=1,P=1,Q=9"));

        // Assert
        await Assert.That(replies[^1]).StartsWith("\e_Gi=1,p=1;ENOPARENT");
    }

    [Test]
    public async Task Kitty_virtual_relative_placement_is_einval()
    {
        // Arrange
        var (t, replies) = WithImageOne();
        Feed(t, Kitty("a=p,i=1,p=1,q=2"));

        // Act
        Feed(t, Kitty("a=p,i=1,p=2,P=1,Q=1,U=1"));

        // Assert
        await Assert.That(replies[^1]).StartsWith("\e_Gi=1,p=2;EINVAL");
    }

    [Test]
    public async Task Kitty_relative_placement_cycle_is_ecycle()
    {
        // Arrange
        var (t, replies) = WithImageOne();
        Feed(t, Kitty("a=p,i=1,p=1,q=2") + Kitty("a=p,i=1,p=2,P=1,Q=1,q=2"));

        // Act
        Feed(t, Kitty("a=p,i=1,p=1,P=1,Q=2"));

        // Assert
        await Assert.That(replies[^1]).StartsWith("\e_Gi=1,p=1;ECYCLE");
    }

    [Test]
    public async Task Kitty_relative_placement_chains_are_capped_at_eight_links()
    {
        // Arrange
        var (t, replies) = WithImageOne();
        // The history of the refusals above (none of them may leave anything behind), then a chain of 8 links: fine.
        Feed(t, Kitty("a=p,i=1,p=1,P=1,Q=9"));
        Feed(t, Kitty("a=p,i=1,p=1,q=2") + Kitty("a=p,i=1,p=2,P=1,Q=1,U=1"));
        Feed(t, Kitty("a=p,i=1,p=2,P=1,Q=1,q=2") + Kitty("a=p,i=1,p=1,P=1,Q=2"));
        for (int k = 3; k <= 9; k++) Feed(t, Kitty($"a=p,i=1,p={k},P=1,Q={k - 1},H=1,q=1"));

        // Act
        Feed(t, Kitty("a=p,i=1,p=10,P=1,Q=9")); // a 9th link is too deep

        // Assert
        await Assert.That(replies[^1]).StartsWith("\e_Gi=1,p=10;ETOODEEP");
        await Assert.That(t.Images.Placements(false).Count).IsEqualTo(9);
    }

    /// <summary>Image 1 as a virtual placement (p=1) and image 2 placed relative to it.</summary>
    private static (Terminal T, KittyPlacement Child) RelativeToVirtual()
    {
        var (t, _) = Term(20, 8);
        Feed(t, Kitty("a=T,U=1,f=32,s=1,v=1,i=1,p=1,c=2,r=2,q=2", Rgba(1, 1)) + Kitty("a=t,f=32,s=1,v=1,i=2,q=2", Rgba(1, 1)));
        Feed(t, Kitty("a=p,i=2,p=1,P=1,Q=1,H=2,V=1,q=2"));
        return (t, t.Images.Placements(false).Single(p => p.Image.Id == 2));
    }

    [Test]
    public async Task Kitty_relative_to_a_virtual_placement_is_positioned_by_the_renderer()
    {
        // Act
        var (_, child) = RelativeToVirtual();

        // Assert
        await Assert.That(child.RelativeToVirtual).IsTrue();
        await Assert.That((child.Line, child.Column)).IsEqualTo((1L, 2)); // offsets from wherever the placeholders are
    }

    [Test]
    public async Task Kitty_relative_to_a_virtual_placement_is_not_moved_or_pruned_by_scrolling()
    {
        // Arrange
        var (t, child) = RelativeToVirtual();

        // Act
        Feed(t, "\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n");

        // Assert
        await Assert.That((child.Line, child.Column)).IsEqualTo((1L, 2));
        await Assert.That(t.Images.Placements(false)).Contains(child);
    }

    [Test]
    public async Task Kitty_relative_to_a_virtual_placement_dies_with_it()
    {
        // Arrange
        var (t, _) = RelativeToVirtual();
        Feed(t, "\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n");

        // Act
        Feed(t, Kitty("a=d,d=I,i=1,q=2"));

        // Assert
        await Assert.That(t.Images.Placements(false)).IsEmpty();
    }

    // ---- Kitty animation ----------------------------------------------------------------------------

    private static byte[] Pixel(TerminalImage img, int frame, int x, int y) =>
        img.Animation!.Frame(frame)!.AsSpan((y * img.Width + x) * 4, 4).ToArray();

    /// <summary>A 2×2 red root frame, frame 2 = root + a blue pixel (gap 100 ms), frame 3 = half green over red.</summary>
    private static (Terminal T, List<string> Replies, TerminalImage Image, FakeClock Clock) ThreeFrames()
    {
        var (t, replies) = Term(20, 8);
        var clock = new FakeClock { Now = 1000 };
        t.Images.Clock = () => clock.Now;
        Feed(t, Kitty("a=T,f=32,s=2,v=2,i=1,q=2", Rgba(2, 2, 0xFF0000FF)));            // red root frame
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,x=1,y=1,c=1,z=100", Rgba(1, 1, 0x0000FFFF))); // frame 2: root + blue pixel
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,Y=4278190335,q=2", Rgba(1, 1, 0x00FF0080)));   // frame 3: half green over Y=0xFF0000FF
        return (t, replies, t.Images.ById(1)!, clock);
    }

    [Test]
    public async Task Kitty_animation_frames_compose()
    {
        // Act
        var (t, replies, img, _) = ThreeFrames();

        // Assert
        await Assert.That(replies[^1]).IsEqualTo("\e_Gi=1;OK\e\\"); // frame 2's reply (frame 3 is quiet)
        await Assert.That(img.Animation!.FrameCount).IsEqualTo(3);
        await Assert.That(Pixel(img, 1, 0, 0)).IsEquivalentTo(new byte[] { 255, 0, 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(Pixel(img, 1, 1, 1)).IsEquivalentTo(new byte[] { 0, 0, 255, 255 }, CollectionOrdering.Matching);
        await Assert.That(Pixel(img, 2, 1, 1)).IsEquivalentTo(new byte[] { 255, 0, 0, 255 }, CollectionOrdering.Matching);   // background color Y
        await Assert.That(Pixel(img, 2, 0, 0)).IsEquivalentTo(new byte[] { 127, 128, 0, 255 }, CollectionOrdering.Matching); // half-transparent green blended over it
        await Assert.That(img.Bytes).IsEqualTo(2L * 2 * 4 * 3);
        await Assert.That(t.Images.MemoryUsed).IsEqualTo(img.Bytes);
    }

    [Test]
    public async Task Kitty_animation_is_stopped_until_told_to_run()
    {
        // Arrange
        var (_, _, img, clock) = ThreeFrames();

        // Act
        int wait = img.Animation!.MsUntilNextFrame(clock.Now);

        // Assert
        await Assert.That(wait).IsEqualTo(int.MaxValue);
    }

    /// <summary>Runs the animation (s=3) and plays it until it wraps: 1000 → 1140 ms, ending on frame 2 (index 1).</summary>
    private static (int MsAtStart, bool Advanced, int CurrentAfterStart, int MsOnFrame2, int CurrentAfter100, int CurrentAfterWrap) Play(
        Terminal t, ImageAnimation anim, FakeClock clock)
    {
        Feed(t, Kitty("a=a,i=1,s=3,v=1,q=2"));
        int msAtStart = anim.MsUntilNextFrame(clock.Now);
        bool advanced = anim.Update(clock.Now);
        int currentAfterStart = anim.Current;
        int msOnFrame2 = anim.MsUntilNextFrame(clock.Now);
        clock.Now += 100;
        anim.Update(clock.Now);
        int currentAfter100 = anim.Current;
        clock.Now += 40;
        anim.Update(clock.Now);                              // wraps past the gapless root to frame 2
        return (msAtStart, advanced, currentAfterStart, msOnFrame2, currentAfter100, anim.Current);
    }

    [Test]
    public async Task Kitty_animation_plays_on_the_clock_and_skips_the_gapless_root_frame()
    {
        // One sequence on one clock: each frame change depends on the previous one.
        // Arrange
        var (t, _, img, clock) = ThreeFrames();

        // Act
        // The root frame has gap 0 (skipped while playing), new frames 40 ms; frame 2 was given 100 ms.
        var played = Play(t, img.Animation!, clock);

        // Assert
        await Assert.That(played.MsAtStart).IsEqualTo(0); // the root frame is gapless: move on now
        await Assert.That(played.Advanced).IsTrue();
        await Assert.That(played.CurrentAfterStart).IsEqualTo(1);
        await Assert.That(played.MsOnFrame2).IsEqualTo(100);
        await Assert.That(played.CurrentAfter100).IsEqualTo(2);
        await Assert.That(played.CurrentAfterWrap).IsEqualTo(1);
    }

    [Test]
    public async Task Kitty_animation_control_sets_a_frame_gap_and_the_current_frame()
    {
        // Arrange
        var (t, _, img, clock) = ThreeFrames();
        var anim = img.Animation!;
        Play(t, anim, clock);

        // Act
        Feed(t, Kitty("a=a,i=1,r=2,z=500,c=2,q=2")); // frame gap via a=a, current frame via c=

        // Assert
        await Assert.That(anim.Current).IsEqualTo(1);
        await Assert.That(anim.MsUntilNextFrame(clock.Now)).IsEqualTo(500);
    }

    [Test]
    public async Task Kitty_animation_control_stops_it()
    {
        // Arrange
        var (t, _, img, clock) = ThreeFrames();
        var anim = img.Animation!;
        Play(t, anim, clock);
        Feed(t, Kitty("a=a,i=1,r=2,z=500,c=2,q=2"));

        // Act
        Feed(t, Kitty("a=a,i=1,s=1,q=2"));

        // Assert
        await Assert.That(anim.MsUntilNextFrame(clock.Now)).IsEqualTo(int.MaxValue);
        await Assert.That(anim.Update(clock.Now + 10_000)).IsFalse();
    }

    /// <summary>Two 1×1 frames with 10 ms gaps, stopped, on a clock starting at 0.</summary>
    private static (Terminal T, ImageAnimation Animation, FakeClock Clock) TwoFrames()
    {
        var (t, _) = Term(20, 8);
        var clock = new FakeClock();
        t.Images.Clock = () => clock.Now;
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=1,q=2", Rgba(1, 1)));
        Feed(t, Kitty("a=a,i=1,r=1,z=10,q=2"));
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,z=10,q=2", Rgba(1, 1)));
        return (t, t.Images.ById(1)!.Animation!, clock);
    }

    /// <summary>Plays with v=3 for 200 ms; returns how many updates changed the frame.</summary>
    private static int LoopTwice(Terminal t, ImageAnimation anim, FakeClock clock)
    {
        Feed(t, Kitty("a=a,i=1,v=3,s=3,q=2"));
        int changes = 0;
        for (int i = 0; i < 20; i++) { clock.Now += 10; if (anim.Update(clock.Now)) changes++; }
        return changes;
    }

    [Test]
    public async Task Kitty_animation_loops_the_requested_number_of_times_then_stops()
    {
        // Arrange
        var (t, anim, clock) = TwoFrames();

        // Act
        // v=3: loop twice (v - 1), then stop on the last frame.
        int changes = LoopTwice(t, anim, clock);

        // Assert
        await Assert.That(anim.State).IsEqualTo(ImageAnimation.PlayState.Stopped);
        await Assert.That(changes).IsEqualTo(5);   // 0→1, 1→0, 0→1, 1→0, 0→1
        await Assert.That(anim.Current).IsEqualTo(1);
    }

    [Test]
    public async Task Kitty_animation_loading_mode_waits_on_the_last_frame_for_more()
    {
        // One sequence on one clock: the wait only ends because a frame arrives during it.
        // Arrange
        var (t, anim, clock) = TwoFrames();
        LoopTwice(t, anim, clock);

        // Act
        // Loading mode waits on the last frame for more instead of looping.
        Feed(t, Kitty("a=a,i=1,v=1,c=1,s=2,q=2"));
        clock.Now += 10;
        anim.Update(clock.Now);
        int currentWhileWaiting = anim.Current;
        int msWhileWaiting = anim.MsUntilNextFrame(clock.Now);
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,z=10,q=2", Rgba(1, 1)));
        int msAfterNewFrame = anim.MsUntilNextFrame(clock.Now);
        clock.Now += 10;
        anim.Update(clock.Now);

        // Assert
        await Assert.That(currentWhileWaiting).IsEqualTo(1);
        await Assert.That(msWhileWaiting).IsEqualTo(int.MaxValue);
        await Assert.That(msAfterNewFrame).IsEqualTo(10); // the last frame's gap, then on to the new one
        await Assert.That(anim.Current).IsEqualTo(2);
    }

    /// <summary>A 2×1 image: a red root frame and a green frame 2.</summary>
    private static (Terminal T, List<string> Replies, TerminalImage Image) RedAndGreenFrames()
    {
        var (t, replies) = Term(20, 8);
        Feed(t, Kitty("a=t,f=32,s=2,v=1,i=1,q=2", Rgba(2, 1, 0xFF0000FF)));
        Feed(t, Kitty("a=f,f=32,s=2,v=1,i=1,q=2", Rgba(2, 1, 0x00FF00FF)));
        return (t, replies, t.Images.ById(1)!);
    }

    [Test]
    public async Task Kitty_animation_compose_copies_a_rectangle_between_frames()
    {
        // Arrange
        var (t, _, img) = RedAndGreenFrames();
        long version = img.Animation!.PixelsVersion;

        // Act
        // Frame 1's right pixel onto frame 2's left pixel.
        Feed(t, Kitty("a=c,i=1,r=1,c=2,X=1,x=0,w=1,h=1,q=2"));

        // Assert
        await Assert.That(Pixel(img, 1, 0, 0)).IsEquivalentTo(new byte[] { 255, 0, 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(Pixel(img, 1, 1, 0)).IsEquivalentTo(new byte[] { 0, 255, 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(img.Animation.PixelsVersion).IsNotEqualTo(version);
    }

    [Test]
    public async Task Kitty_animation_edits_the_root_frame_in_place()
    {
        // Arrange
        var (t, _, img) = RedAndGreenFrames();
        Feed(t, Kitty("a=c,i=1,r=1,c=2,X=1,x=0,w=1,h=1,q=2"));

        // Act
        // Editing frame 1 (the root) in place with X=1 (replace).
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,r=1,x=1,X=1,q=2", Rgba(1, 1, 0x0000FF00)));

        // Assert
        await Assert.That(Pixel(img, 0, 1, 0)).IsEquivalentTo(new byte[] { 0, 0, 255, 0 }, CollectionOrdering.Matching);
        await Assert.That(img.Animation!.Frame(0)).IsSameReferenceAs(img.Rgba);
    }

    [Test]
    [Arguments("a=c,i=1,r=1,c=2,x=1,w=2,h=1", false, "\e_Gi=1;EINVAL")]
    [Arguments("a=c,i=1,r=2,c=2,w=1,h=1", false, "\e_Gi=1;EINVAL")] // overlapping on the same frame
    [Arguments("a=c,i=1,r=7,c=2", false, "\e_Gi=1;ENOENT")]
    [Arguments("a=f,f=32,s=1,v=1,i=1,r=9", true, "\e_Gi=1;ENOENT")]
    [Arguments("a=a,i=42,s=3", false, "\e_Gi=42;ENOENT")]
    public async Task Kitty_animation_errors(string control, bool withPixel, string expectedReply)
    {
        // Arrange
        var (t, replies, _) = RedAndGreenFrames();
        Feed(t, Kitty("a=c,i=1,r=1,c=2,X=1,x=0,w=1,h=1,q=2"));
        Feed(t, Kitty("a=f,f=32,s=1,v=1,i=1,r=1,x=1,X=1,q=2", Rgba(1, 1, 0x0000FF00)));

        // Act
        Feed(t, Kitty(control, withPixel ? Rgba(1, 1) : ""));

        // Assert
        await Assert.That(replies[^1]).StartsWith(expectedReply);
    }

    [Test]
    public async Task Kitty_chunked_frame_data()
    {
        // Arrange
        var (t, _) = Term(20, 8);
        Feed(t, Kitty("a=t,f=32,s=2,v=1,i=1,q=2", Rgba(2, 1)));
        string b64 = Rgba(2, 1, 0x00FF00FF);

        // Act
        Feed(t, Kitty("a=f,f=32,s=2,v=1,i=1,m=1,q=2", b64[..4]) + Kitty("m=0", b64[4..]));

        // Assert
        await Assert.That(Pixel(t.Images.ById(1)!, 1, 1, 0)).IsEquivalentTo(new byte[] { 0, 255, 0, 255 }, CollectionOrdering.Matching);
    }

    // ---- Image rows on resize -------------------------------------------------------------------------

    /// <summary>A line of text, a 12×3-cell image at (row 1, col 0) with the cursor left in place, then long text.</summary>
    private static Terminal ImageBetweenText()
    {
        var (t, _) = Term(20, 8);
        Feed(t, "text before\r\n");
        Feed(t, ClaudeITerm(PngBase64(8, 8), 12, 3)); // 12×3 cells at (row 1, col 0), cursor stays
        Feed(t, "\e[5;1Hafter that, a long line of text\r\n");
        return t;
    }

    [Test]
    public async Task Image_rows_are_truncated_not_rewrapped_when_the_pane_narrows()
    {
        // Arrange
        var t = ImageBetweenText();
        int tilesBefore = TileCount(t, 1);

        // Act
        t.Resize(8, 8);
        var imageRows = TileCounts(t, 8).Where(n => n > 0).ToArray();

        // Assert
        // Every image row is still one row with the image's first 8 columns: nothing wrapped onto extra rows.
        await Assert.That(tilesBefore).IsEqualTo(12);
        await Assert.That(imageRows).IsEquivalentTo([8, 8, 8], CollectionOrdering.Matching);
        // Ordinary text still re-wraps.
        await Assert.That(Enumerable.Range(0, 8)).Contains(y => t.GetLineText(y) == "after th");
    }

    [Test]
    public async Task Image_rows_come_back_when_the_pane_widens_again()
    {
        // Arrange
        var t = ImageBetweenText();
        t.Resize(8, 8);

        // Act
        // The scrollback keeps whole rows; the screen row was truncated, so it comes back as wide as the screen kept it.
        t.Resize(20, 8);

        // Assert
        await Assert.That(TileCounts(t, 8).Count(n => n > 0)).IsEqualTo(3);
    }

    [Test]
    public async Task Text_rows_next_to_image_rows_still_reflow()
    {
        // Arrange
        var (t, _) = Term(10, 6);
        Feed(t, "0123456789abcde\r\n"); // wraps
        Feed(t, "\eP0;1;0q\"1;1;20;20#1;2;0;100;0#1!20~-!20~-!20~-\e\\");

        // Act
        t.Resize(20, 6);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("0123456789abcde");
        await Assert.That(TileCount(t, 1)).IsEqualTo(2);
    }
}
