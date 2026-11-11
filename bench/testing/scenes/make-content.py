#!/usr/bin/env python3
"""Writes the test content (content/*.vt) the reference scenes feed into erikterm.

The .vt files are committed, so this only has to run when the content changes:
    python3 bench/testing/scenes/make-content.py

Format: one terminal line per file line, in C# escape notation (\\e = ESC, \\a = BEL, \\\\ = a backslash);
everything else is literal UTF-8. The runners join the lines with \\r\\n into one `feed:` step, and
print-vt.sh / print-vt.ps1 print a file through the PTY instead (with real escape bytes).
"""
import base64
import colorsys
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "content")
E = r"\e"
ST = r"\e\\"
BEL = r"\a"


def write(name, lines):
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def b64(s):
    return base64.b64encode(s.encode()).decode()


def png(width, height, rgb):
    raw = b"".join(b"\x00" + bytes(rgb[y * width * 3:(y + 1) * width * 3]) for y in range(height))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def title(text):
    return f"{E}[H{E}[2J{E}[1;38;5;111m erikterm reference: {text}{E}[0m"


# ---- text rendering ---------------------------------------------------------------------------
PL, PL_LEFT, ROUND, ROUND_LEFT = "", "", "", ""
NERD = " ".join(["", "", "", "", "", "", "", "", "", "",
                 "", "\U000f031b", "\U000f0219", ""])
LABEL = f"{E}[38;5;245m"
write("text.vt", [
    f"{title('text rendering')}{E}[2 q",
    "",
    f" {LABEL}Ligatures {E}[0m  -> => != === !== <= >= <=> :: |> <| /* */ ++ -- && || ..= 0xFF www",
    f" {LABEL}Styles    {E}[0m  {E}[1mbold{E}[0m {E}[2mdim{E}[0m {E}[3mitalic{E}[0m {E}[1;3mbold italic{E}[0m "
    f"{E}[4munderline{E}[0m {E}[4:3;58:2::255:85:85mundercurl{E}[0m {E}[9mstrike{E}[0m {E}[7minverse{E}[0m {E}[5mblink{E}[0m",
    f" {LABEL}Colors    {E}[0m  {E}[31mred{E}[0m {E}[32mgreen{E}[0m {E}[33myellow{E}[0m {E}[34mblue{E}[0m "
    f"{E}[35mmagenta{E}[0m {E}[36mcyan{E}[0m {E}[1;31mbold red{E}[0m {E}[38;2;255;140;0mtruecolor{E}[0m {E}[48;5;238m 256 bg {E}[0m",
    f" {LABEL}Emoji     {E}[0m  😀 👍🏽 👨‍👩‍👧 🇳🇱 ❤️ 1️⃣ #️⃣ ✅ 🚀 🦀 ⚡ ☕",
    f" {LABEL}CJK       {E}[0m  日本語のテキスト 한국어 中文字符 ｶﾀｶﾅ |wide|",
    f" {LABEL}Combining {E}[0m  é ñ ü (precomposed)  é ñ ü (combining)  Z̤͂ a̧ ǫ̃",
    f" {LABEL}Nerd Font {E}[0m  {NERD}",
    f" {LABEL}Powerline {E}[0m  {E}[48;5;111;38;5;235m main {E}[0;38;5;111;48;5;238m{PL}{E}[38;5;252m ~/src/erikterm "
    f"{E}[0;38;5;238m{PL}{E}[0m  {E}[38;5;150m{ROUND_LEFT}{E}[48;5;150;38;5;235m ok {E}[0;38;5;150m{ROUND}{E}[0m  {PL_LEFT}{PL}",
    "",
    "  ┌──────┬──────┐  ╔══════╦═════╗  ╭────────╮  ┏━━━━━━┳━━━━━┓   ░░▒▒▓▓██  ▀▀▄▄▌▐  ▁▂▃▄▅▆▇█",
    "  │ thin │ box  │  ║ dbl  ║ box ║  │ round  │  ┃ heavy┃ box ┃   ⠁⠃⠇⡇⣇⣧⣷⣿  braille",
    "  ├──────┼──────┤  ╠══════╬═════╣  ╰────────╯  ┣━━━━━━╋━━━━━┫   ◢◣◤◥ ◆◇○● ■□▪▫",
    "  └──────┴──────┘  ╚══════╩═════╝  ╶──╴╷╵ ┄┈┆  ┗━━━━━━┻━━━━━┛   ✓✗ →←↑↓ ⇒ ∀∃∈ ≠≤≥ λ π Σ",
    "",
    f" {LABEL}The quick brown fox jumps over the lazy dog. 0123456789 Il1| O0 {{}}[]() <> @#$%&*{E}[0m",
    f" {LABEL}Wrapped: a long line follows that is wider than the window and has to continue on the next row "
    f"without a break at all, like any other terminal{E}[0m",
    "",
    f" {E}[32m${E}[0m ",
])

# ---- colors -----------------------------------------------------------------------------------
NAMES = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"]
lines = [title("colors"), ""]
lines.append(" " + "".join(f"{E}[3{i}m{n:<9}" for i, n in enumerate(NAMES)) + f"{E}[0m   normal")
lines.append(" " + "".join(f"{E}[9{i}m{n:<9}" for i, n in enumerate(NAMES)) + f"{E}[0m   bright")
lines.append(" " + "".join(f"{E}[1;3{i}m{n:<9}{E}[0m" for i, n in enumerate(NAMES)) + "   bold")
lines.append(" " + "".join(f"{E}[2;3{i}m{n:<9}{E}[0m" for i, n in enumerate(NAMES)) + "   dim")
lines.append("")
lines.append(" " + "".join(f"{E}[48;5;{i}m {i:>2}  " for i in range(8)) + f"{E}[0m")
lines.append(" " + "".join(f"{E}[48;5;{i}m {i:>2}  " for i in range(8, 16)) + f"{E}[0m")
lines.append("")
for row in range(6):
    s = " "
    for g in range(6):
        s += "".join(f"{E}[48;5;{16 + row * 36 + g * 6 + b}m  " for b in range(6)) + f"{E}[0m "
    lines.append(s.rstrip())
lines.append("")
lines.append(" " + "".join(f"{E}[48;5;{i}m   " for i in range(232, 256)) + f"{E}[0m")
lines.append("")


def gradient(f):
    s = " "
    for x in range(96):
        r, g, b = f(x / 95)
        s += f"{E}[48;2;{r};{g};{b}m "
    return s + f"{E}[0m"


lines.append(gradient(lambda t: tuple(int(c * 255) for c in colorsys.hsv_to_rgb(t, 1, 1))))
lines.append(gradient(lambda t: (int(t * 255),) * 3))
lines.append(gradient(lambda t: (int(255 * t), 40, int(255 * (1 - t)))))
lines.append("")
lines.append(f" {E}[7m inverse {E}[0m {E}[30;47m black on white {E}[0m {E}[97;41m bright white on red {E}[0m "
             f"{E}[38;2;250;179;135;48;2;36;39;58m truecolor fg+bg {E}[0m")
write("colors.vt", lines)

# ---- underline styles, underline colors, overline ---------------------------------------------
RED, YELLOW, BLUE, GREEN = "58:2::237:135:150", "58:2::238:212:159", "58;2;138;173;244", "58:5:114"
write("underlines.vt", [
    title("underline styles"),
    "",
    f" {LABEL}4 / 4:1   {E}[0m  {E}[4msingle underline{E}[0m   {E}[4;{BLUE}mblue (58;2;r;g;b){E}[0m   {E}[4;58;5;203mindexed (58;5;n){E}[0m",
    f" {LABEL}4:2 / 21  {E}[0m  {E}[4:2mdouble underline{E}[0m   {E}[21mSGR 21 is double too{E}[0m   {E}[4:2;{GREEN}mgreen double{E}[0m",
    f" {LABEL}4:3       {E}[0m  {E}[4:3mcurly in the text color{E}[0m   {E}[4:3;{RED}merror (LSP){E}[0m   {E}[4:3;{YELLOW}mwarning{E}[0m",
    f" {LABEL}4:4       {E}[0m  {E}[4:4mdotted underline{E}[0m   {E}[4:4;{BLUE}mteh speling misteaks{E}[0m",
    f" {LABEL}4:5       {E}[0m  {E}[4:5mdashed underline{E}[0m   {E}[4:5;{GREEN}mgreen dashes{E}[0m",
    f" {LABEL}53        {E}[0m  {E}[53moverline{E}[0m   {E}[53;4moverline + underline{E}[0m   {E}[53;9;4:3;{RED}mall at once{E}[0m",
    f" {LABEL}59 / 24   {E}[0m  {E}[4:3;{RED}mred {E}[59mthen the text color{E}[0m   {E}[4:3mcurly {E}[24mthen none{E}[0m",
    f" {LABEL}styles    {E}[0m  {E}[1;4:3;{RED}mbold{E}[0m {E}[3;4:4;{BLUE}mitalic{E}[0m {E}[7;4:3;{RED}minverse{E}[0m "
    f"{E}[31;4:5mred text dashed{E}[0m {E}[38;2;166;218;149;4:3;{RED}mgreen text, red curl{E}[0m",
    f" {LABEL}wide      {E}[0m  {E}[4:3;{RED}m日本語のテキスト{E}[0m  {E}[4:4;{BLUE}m😀 👍🏽 🚀{E}[0m  {E}[4:2m한국어{E}[0m",
    "",
    f" {E}[32m${E}[0m ",
])

# ---- images: one test card, three protocols ---------------------------------------------------
W, H = 120, 60
rgb = []
for y in range(H):
    for x in range(W):
        if x in (0, W - 1) or y in (0, H - 1):
            c = (0, 0, 0)
        elif abs(x - 2 * y) <= 1:
            c = (255, 255, 255)
        else:
            r, g, b = colorsys.hsv_to_rgb(x / W, 0.85, 1 - 0.6 * y / H)
            c = (int(r * 255), int(g * 255), int(b * 255))
        rgb += c
card = base64.b64encode(png(W, H, rgb)).decode()
pieces = [card[i:i + 4096] for i in range(0, len(card), 4096)]
kitty = ""
for i, piece in enumerate(pieces):
    head = "a=T,f=100,c=24,r=6,C=1,q=2," if i == 0 else ""
    kitty += f"{E}_G{head}m={1 if i < len(pieces) - 1 else 0};{piece}{ST}"
iterm = f"{E}]1337;File=inline=1;width=24;height=6;preserveAspectRatio=0;doNotMoveCursor=1:{card}{BEL}"
BANDS = [(100, 0, 0), (100, 60, 0), (90, 90, 0), (0, 80, 0), (0, 40, 100), (60, 0, 80)]
sixel = f'{E}P0;1;0q"1;1;120;60' + "".join(f"#{i};2;{r};{g};{b}" for i, (r, g, b) in enumerate(BANDS))
for _ in range(10):  # 10 sixel rows of 6 px; each band is 20 px wide
    sixel += "$".join(f"#{i}" + "!20?" * i + "!20~" for i in range(6)) + "-"
sixel += ST
write("images.vt", [
    title("images"),
    "",
    " kitty graphics (PNG, 24x6 cells)    iTerm2 inline (PNG, 24x6 cells)     sixel (120x60 px)",
    "",
    f"{E}[5;2H{kitty}{E}[5;38H{iterm}{E}[5;74H{sixel}",
    f"{E}[13;1H kitty and iTerm2 images are sized in cells, so they cover the same cells everywhere; sixel is",
    " sized in pixels, so on a HiDPI display it looks smaller next to the text.",
])

# ---- links, quick select, search, vim -----------------------------------------------------------
write("links.vt", [
    title("links"),
    "",
    f"  OSC 8 hyperlink:  {E}]8;;https://github.com/erikterm/erikterm{ST}erikterm on GitHub{E}]8;;{ST}",
    "  Plain URL:        https://example.com/docs/testing?platform=windows#screenshots",
    "  Mail / ssh:       mailto:someone@example.com   ssh://git@example.com/repo.git",
    "  File + line:      README.md:46   src/ErikTerm.App/Program.cs(12,5): error CS0103",
    "",
])
write("quickselect.vt", [
    title("quick select"),
    "",
    "  commit 9f3c2a1b7e  Merge branch fix/parser   https://github.com/erikterm/erikterm/pull/42",
    "  error in /usr/local/share/erikterm/themes/macchiato.toml:12:7",
    "  host 203.0.113.20 port 8080   id 3f2504e0-4f89-11d3-9a0c-0305e82c3301   size 4096",
    "  see ~/.config/erikterm/erikterm.toml",
    "",
])
log = [f"{E}[H{E}[2J"]
for i in range(1, 25):
    error = i % 5 == 0
    level = f"{E}[31merror{E}[0m" if error else f"{E}[32minfo {E}[0m"
    text = "connection reset by peer, retrying" if error else f"processed batch {i * 17} in {i * 3} ms"
    log.append(f"  2026-11-07T10:{i:02d}:00  {level}  worker-{i % 4}  {text}")
write("search.vt", log)
write("prose.vt", [
    title("vim mode"),
    "",
    "  The scrollback is a buffer you can move through with vim keys:",
    "  words, lines, paragraphs, marks, search and yank all work, and",
    "  visual mode selects characters, lines or a block (Ctrl-v).",
    "",
    "  fn main() {",
    '      let greeting = "hello, erikterm";',
    '      println!("{greeting}");',
    "  }",
    "",
])

# Commands with OSC 133 marks, some failing: the scrollbar's ticks (prompts, failures, search matches).
marks = [f"{E}[H{E}[2J"]
for n, (cmd, status) in enumerate([
    ("make test", 0), ("git status", 0), ("cargo build", 101), ("ls -la", 0), ("npm run lint", 1), ("dotnet test", 0),
    ("kubectl get pods", 0), ("make deploy", 2), ("du -sh .", 0), ("./run-benchmarks", 0), ("git log", 0), ("make", 0),
]):
    marks.append(f"{E}]133;A{ST}{E}[32m$ {E}[0m{E}]133;B{ST}{cmd}")
    marks.append(f"{E}]133;C;cmdline_url={cmd.replace(' ', '%20')}{ST}")
    for line in range(9):
        word = "error: something failed" if status and line == 5 else f"output {line} of {cmd}"
        marks.append(f"  {word}")
    marks.append(f"{E}]133;D;{status}{ST}")
write("marks.vt", marks)

# ---- coding agents (simulated with OSC 7501 program status) -------------------------------------
write("agent.vt", [
    f"{E}[H{E}[2J{E}]0;✳ Fix the parser{BEL}",
    f"{E}[38;5;209m╭──────────────────────────────────────────╮{E}[0m",
    f"{E}[38;5;209m│{E}[0m ✻ Claude Code (simulated with OSC 7501)  {E}[38;5;209m│{E}[0m",
    f"{E}[38;5;209m╰──────────────────────────────────────────╯{E}[0m",
    "",
    " ● Bash(rm -rf build && dotnet build)",
    "",
    " Do you want to proceed?",
    f" {E}[38;5;111m❯ 1. Yes{E}[0m",
    "   2. No, and tell Claude what to do",
    f"{E}]7501;state=blocked:app=claude-code:kind=permission:msg={b64('Bash: rm -rf build')}{ST}",
])
write("agent-working.vt", [
    f"{E}[H{E}[2J{E}]0;◐ Write the tests{BEL}",
    " ✻ Cogitating… (12s · esc to interrupt)",
    f"{E}]7501;state=working:app=claude-code:progress=40:msg={b64('Writing ParserTests.cs')}{ST}",
])
write("agent-idle.vt", [
    f"{E}[H{E}[2J",
    " > Claude Code at its prompt (simulated: OSC 7501 state=idle)",
    f"{E}]7501;state=idle:app=claude-code{ST}",
])

# ---- notifications and the clipboard prompt -----------------------------------------------------
write("notify.vt", [
    title("notification bar"),
    "",
    '  This pane sent OSC 777 notify while the window counted as unfocused (script step "unfocus").',
    f"{E}]777;notify;Build finished;All 1405 tests passed{BEL}",
])
write("osc52.vt", [
    title("OSC 52 clipboard prompt"),
    "",
    '  This pane asked to set the clipboard (OSC 52). With osc52 = "ask" the bar asks first.',
    f"{E}]52;c;{b64('Hello from the reference scene')}{BEL}",
])
write("plain.vt", [
    title("window"),
    "",
    "  A plain pane. Window padding, background and cursor come from scene.toml.",
    "",
    f" {E}[32m${E}[0m ",
])
