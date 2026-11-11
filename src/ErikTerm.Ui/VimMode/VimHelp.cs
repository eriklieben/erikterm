namespace ErikTerm.Ui.VimMode;

/// <summary>The vim mode keymap, as the cheat sheet (g? / :help) shows it.</summary>
public static class VimHelp
{
    public readonly record struct Entry(string Keys, string Text);
    public sealed record Section(string Title, Entry[] Entries);

    public static readonly IReadOnlyList<Section> Sections =
    [
        new("Move", [
            new("h j k l", "left, down, up, right (counts: 5j)"),
            new("w b e  W B E  ge", "words / WORDS (soft wraps join)"),
            new("0 ^ $", "line start, first character, end"),
            new("gg G  :N", "top of the scrollback, bottom, line N"),
            new("H M L", "top, middle, bottom of the screen"),
            new("f F t T  ; ,", "to a character on the line, repeat"),
            new("%", "matching bracket"),
            new("{ }", "previous / next command output"),
            new("[[ ]]", "previous / next prompt"),
        ]),
        new("Scroll", [
            new("Ctrl-u Ctrl-d", "half a page up / down"),
            new("Ctrl-b Ctrl-f", "a page up / down"),
            new("Ctrl-y Ctrl-e", "one line up / down"),
            new("zt zz zb", "cursor line to top, middle, bottom"),
        ]),
        new("Select & copy", [
            new("v V Ctrl-v", "visual: characters, lines, block"),
            new("o", "other end of the selection"),
            new("iw aw iW aW ip", "word, WORD, command output"),
            new("y  Y", "copy the selection / its lines"),
            new("yy y$ yiw", "copy the line, to the end, the word"),
            new("yo  yc", "copy the command's output / command line"),
        ]),
        new("Search", [
            new("/ ?", "search forward / backward (as you type)"),
            new("n N", "next / previous match"),
            new("* #", "the word under the cursor"),
            new(":noh", "clear the highlights"),
        ]),
        new("Open", [
            new("gx", "open the URL under the cursor"),
            new("gf", "open file:line in Neovim"),
            new("go  gO", "command output / scrollback in Neovim"),
            new("; gh", "hints: label jumps, LABEL copies, Ctrl/Enter opens"),
        ]),
        new("Marks", [
            new("m{a-z}", "set a mark"),
            new("'{a-z}  `{a-z}", "jump to its line / exact spot"),
        ]),
        new("Panes & tabs", [
            new("Ctrl-w h j k l", "focus the pane in that direction"),
            new("Ctrl-w s  v", "split down / right"),
            new("Ctrl-w q  o", "close / zoom the pane"),
            new("Ctrl-w x  r R", "swap with the next, rotate"),
            new("Ctrl-w =", "equalize sizes"),
            new("Ctrl-w H J K L", "move the pane to an edge"),
            new("Ctrl-w T", "move the pane to a new tab"),
            new("Ctrl-w > < + -", "wider, narrower, taller, shorter (5 Ctrl-w >)"),
            new("gt gT  Ngt", "next / previous tab, tab N"),
        ]),
        new("Command line", [
            new(":sp :vs :q :only", "split, close, keep only this pane"),
            new(":tabnew :tabn :tabp", "tabs"),
            new(":theme NAME", "color scheme, previewed live"),
            new(":set KEY=VALUE", "setting for this session"),
            new(":w FILE  :w! FILE", "write the scrollback (0600)"),
            new(":send PANE", "paste the yank into a pane / agent"),
            new(":layout NAME", "tiled, main-vertical, even-…"),
            new("Tab  Shift-Tab", "complete, cycle"),
        ]),
        new("Leave", [
            new("i a", "back to the prompt"),
            new("q Esc", "leave (Esc in visual: normal)"),
            new("g?  :help", "this cheat sheet"),
        ]),
    ];
}
