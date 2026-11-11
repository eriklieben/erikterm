using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>One directory's git state (shared by the panes in it).</summary>
internal sealed class RepoInfo
{
    public required string Cwd;
    public RefreshState State;
    public GitStatus? Status;
    public string? Root, Slug;   // work tree root; "owner/repo" of its GitHub remote
    public long RemotesAt;
    public int Version;          // bumped whenever Status/Root/Slug change
#pragma warning disable S3459 // A mutable struct: its state changes through its methods (Quiet.Checked), which this rule doesn't see as assignment.
    public QuietPoll Quiet;
#pragma warning restore S3459
}

/// <summary>A repository + branch's GitHub state (WindowGitHub).</summary>
internal sealed class GhInfo
{
    public required string Root, Branch, Slug;
    public RefreshState State;
    public GitHubStatus? Status;
    public int Version;
}

/// <summary>What the integrations know about one pane (main thread only).</summary>
internal sealed class PaneTrack
{
    public string? OscUri;           // the Term.WorkingDirectoryUri instance last looked at
    public bool OscSeen;
    public string? Cwd;
    public long CwdPolledAt;
    public long CommandsSeen = -1;   // Term.CommandsStarted when last idle
    public long FocusedAt;
    public bool Live;
    public RepoInfo? Repo;
    public int RepoVersion = -1, Epoch = -1;
    public GhInfo? Gh;
    public int GhVersion = -1;
    public RepoBadgeModel? Badge;
    public long PillUntil;
    public Tween Pill = new(0);
}

/// <summary>
/// The integrations' per-pane table (by pane id), shared by git status, GitHub, the merge/rebase bar, links and dev
/// containers. <see cref="WindowIntegrations"/> fills and prunes it in its scan; the others only read it.
/// </summary>
internal sealed class PaneTracks
{
    private readonly Dictionary<int, PaneTrack> _tracks = [];
    private readonly List<int> _dead = [];

    /// <summary>When the next scan is due (0: at the next tick).</summary>
    public long ScanAt;

    public Dictionary<int, PaneTrack> All => _tracks;

    /// <summary>Scan at the next tick (a refresh was asked for).</summary>
    public void ScanSoon() => ScanAt = 0;

    public PaneTrack Track(Pane pane)
    {
        if (!_tracks.TryGetValue(pane.Id, out var tr)) _tracks[pane.Id] = tr = new PaneTrack();
        return tr;
    }

    public PaneTrack? Of(Pane? pane) => pane is not null && _tracks.TryGetValue(pane.Id, out var tr) ? tr : null;

    /// <summary>The pane's directory as the integrations see it (OSC 7, else /proc).</summary>
    public string? Cwd(Pane pane) => Of(pane)?.Cwd ?? PaneDirectory(pane);

    /// <summary>
    /// OSC 7, else the foreground program's directory, else the shell's (Pane.StartDirectory / PaneCwd; /proc
    /// lookups allocate: call it on a timer, not per frame).
    /// </summary>
    public static string? PaneDirectory(Pane pane)
    {
        try { return pane.StartDirectory(null); }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>Forgets the panes the last walk didn't mark live.</summary>
    public void DropDead()
    {
        if (_tracks.Count == 0) return;
        foreach (var (id, tr) in _tracks) if (!tr.Live) _dead.Add(id);
        foreach (int id in _dead) _tracks.Remove(id);
        _dead.Clear();
    }
}
