using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikTerm.App.Session;

/// <summary>
/// session.json: what a window looked like, so the next start can rebuild it. Version 1:
/// <code>
/// { "version": 1, "saved_at": "…", "windows": [ { "width": 1000, "height": 640, "fullscreen": false, "font_zoom": 1,
///   "active_tab": 0, "tabs": [ { "title": "logs", "focused": 1, "zoomed": null,
///     "layout": { "axis": "horizontal", "ratio": 0.5, "first": { "pane": { "cwd": "/tmp" } },
///                 "second": { "pane": { "cwd": "/src", "program": { "name": "nvim", "argv": ["nvim", "x.c"] },
///                                       "agent": { "id": "claude-code", "session_id": "…" } } } } } ] } ] }
/// </code>
/// <c>windows</c> holds every window of the process that saved (in the order they were opened; each is rebuilt as a
/// window on the next start). <c>x</c>/<c>y</c> (the window's desktop position in points) are only written where the
/// platform tells applications their window positions (not on Wayland). <c>focused</c>/<c>zoomed</c> are leaf
/// indices in depth-first order (first child before second).
/// </summary>
internal sealed class SessionFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset? SavedAt { get; set; }
    public List<SessionWindow> Windows { get; set; } = [];
}

internal sealed class SessionWindow
{
    /// <summary>Window size in points (not device pixels); the windowed size when full screen.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Desktop position in points; null where unknown (Wayland) — the window manager places the window then.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Fullscreen { get; set; }
    /// <summary>Ctrl+= / Ctrl+- zoom factor on top of font_size.</summary>
    public double FontZoom { get; set; } = 1;
    public int ActiveTab { get; set; }
    public List<SessionTab> Tabs { get; set; } = [];
}

internal sealed class SessionTab
{
    /// <summary>A custom title (renamed tab); null for automatic titles.</summary>
    public string? Title { get; set; }
    /// <summary>The tab's color (a scheme color name or #rrggbb, Config/TabColors.cs); null for none.</summary>
    public string? Color { get; set; }
    public int Focused { get; set; }
    public int? Zoomed { get; set; }
    public SessionNode Layout { get; set; } = new();
}

/// <summary>A layout node: a leaf with <see cref="Pane"/>, or a split with <see cref="First"/>/<see cref="Second"/>.</summary>
internal sealed class SessionNode
{
    public SessionPane? Pane { get; set; }
    /// <summary>"horizontal" (side by side) or "vertical" (stacked).</summary>
    public string? Axis { get; set; }
    /// <summary>Share of the first child.</summary>
    public double? Ratio { get; set; }
    public SessionNode? First { get; set; }
    public SessionNode? Second { get; set; }

    [JsonIgnore] public bool IsSplit => First is not null && Second is not null;
}

internal sealed class SessionPane
{
    public string? Cwd { get; set; }
    /// <summary>The foreground program when it wasn't the shell.</summary>
    public SessionProgram? Program { get; set; }
    /// <summary>The coding agent that was running with a known session id (absent in files from before it was saved).</summary>
    public SessionAgent? Agent { get; set; }
    /// <summary>The container the pane's shell ran in (WindowFeatures/WindowContainers.cs); the shell opens there again.</summary>
    public SessionContainer? Container { get; set; }
    /// <summary>restore_scrollback_lines: the last lines of the pane, text with SGR, oldest first.</summary>
    public List<string>? Scrollback { get; set; }
}

/// <summary>What "Resume N agents" needs (Agents/WindowAgentResume.cs): which agent, and its own session id.</summary>
internal sealed class SessionAgent
{
    /// <summary>AgentCatalog's id ("claude-code", "codex", …).</summary>
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
}

/// <summary>A shell in a container: which engine and container, its dev container folder, the user and the directory inside.</summary>
internal sealed class SessionContainer
{
    /// <summary>docker or podman (a name or a path).</summary>
    public string Engine { get; set; } = "";
    /// <summary>Global engine options (another context, host or connection).</summary>
    public List<string>? EngineOptions { get; set; }
    public string Id { get; set; } = "";
    /// <summary>The host folder of a dev container: finds it again when the id is gone (rebuilt).</summary>
    public string? WorkspaceFolder { get; set; }
    public string? User { get; set; }
    public string? Workdir { get; set; }

    /// <summary>
    /// The pane command (<c>erikterm devcontainer shell</c>), or null when the entry can't be used: an engine that isn't
    /// docker or podman, or a value that couldn't be restored safely (the file may have been edited by hand).
    /// </summary>
    public string[]? Command(string self, bool shellIntegration)
    {
        if (Containers.ContainerExec.EngineName(Engine) is null || Id.Length == 0) return null;
        var argv = Containers.ContainerShellCommand.Build(self, new Containers.ContainerShellSpec(Engine, EngineOptions ?? [], Id, WorkspaceFolder, User, Workdir), shellIntegration);
        return RestoreLaunch.IsSafe(argv) && !Id.StartsWith('-') && argv.Skip(1).All(a => a.Length > 0) ? argv : null;
    }
}

internal sealed class SessionProgram
{
    /// <summary>Program name (argv[0]'s file name, or the process name).</summary>
    public string Name { get; set; } = "";
    /// <summary>The original argv — only recorded for programs in restore_programs.</summary>
    public List<string>? Argv { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

internal static class SessionJson
{
    public static string Serialize(SessionFile file) => JsonSerializer.Serialize(file, SessionJsonContext.Default.SessionFile);

    /// <summary>
    /// Parses a session file. Returns null (with a reason) for malformed JSON or an unknown version — an
    /// incompatible file is ignored, never fatal.
    /// </summary>
    public static SessionFile? Deserialize(string json, out string? problem)
    {
        problem = null;
        try
        {
            // Look at the version before binding the rest, so a future layout can't fail half way.
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { problem = "not a JSON object"; return null; }
                if (!doc.RootElement.TryGetProperty("version", out var v) || !v.TryGetInt32(out int version))
                {
                    problem = "no version";
                    return null;
                }
                if (version != SessionFile.CurrentVersion) { problem = $"version {version} (this build reads {SessionFile.CurrentVersion})"; return null; }
            }
            var file = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionFile);
            if (file is null) { problem = "empty"; return null; }
            file.Windows ??= [];
            return file;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
