namespace ErikTerm.App;

/// <summary>What one erikterm executable was started as. Program.Main dispatches on it, in this order.</summary>
internal enum EntryKind
{
    /// <summary>macOS: a pane's child becoming its program (must run before anything else).</summary>
    SpawnHelper,
    /// <summary>The tmux emulation: run as <c>tmux</c> (shim link) or <c>erikterm tmux-shim …</c>.</summary>
    TmuxShim,
    /// <summary><c>--version</c> / <c>-V</c>, also as <c>cli --version</c>.</summary>
    Version,
    /// <summary><c>--terminfo</c>: the xterm-erikterm source.</summary>
    Terminfo,
    /// <summary><c>erikterm cli …</c>: the control client.</summary>
    Cli,
    /// <summary><c>erikterm ssh …</c>: ssh with erikterm's terminfo installed on the remote (Cli/SshCommand.cs).</summary>
    Ssh,
    /// <summary><c>erikterm devcontainer …</c>: a shell in a folder's dev container, set up like erikterm's own (Cli/DevcontainerCommand.cs).</summary>
    Devcontainer,
    /// <summary><c>erikterm mergetool [LOCAL BASE REMOTE MERGED]</c>: the conflict resolver in the terminal (Compare/).</summary>
    MergeTool,
    /// <summary><c>erikterm compare A B</c>: side-by-side compare in the terminal (Compare/).</summary>
    Compare,
    /// <summary><c>erikterm review [--base BRANCH]</c>: this branch as a local pull request (Compare/ReviewTool.cs).</summary>
    Review,
    /// <summary><c>erikterm settings</c>: the settings page in the terminal (SettingsPage/).</summary>
    Settings,
    /// <summary>The terminal itself.</summary>
    Window,
}

/// <summary>
/// The dispatch order of Program.Main as a pure function, so it can be tested: the tmux shim must come before
/// <c>--version</c>, or <c>tmux -V</c> answers with erikterm's version and tmux clients (Claude Code) misjudge which
/// tmux features they may use.
/// </summary>
internal static class EntryCommand
{
    public static EntryKind Classify(string[] args, bool invokedAsTmux, string spawnHelperArgument)
    {
        if (args.Length > 0 && args[0] == spawnHelperArgument) return EntryKind.SpawnHelper;
        if (invokedAsTmux || args is ["tmux-shim", ..]) return EntryKind.TmuxShim;
        if (args is ["--version" or "-V"] or ["cli", "--version" or "-V"]) return EntryKind.Version;
        if (args is ["--terminfo"]) return EntryKind.Terminfo;
        return (args.Length > 0 ? args[0] : "") switch
        {
            "cli" => EntryKind.Cli,
            "ssh" => EntryKind.Ssh,
            "devcontainer" => EntryKind.Devcontainer,
            "mergetool" => EntryKind.MergeTool,
            "compare" => EntryKind.Compare,
            "review" => EntryKind.Review,
            "settings" => EntryKind.Settings,
            _ => EntryKind.Window,
        };
    }
}
