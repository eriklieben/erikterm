using ErikTerm.App.Worktrees;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>A port block per worktree: stable, unique, inside the range, shared safely between processes.</summary>
public class WorktreePortsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("etports").FullName;
    private string StatePath => Path.Combine(_dir, "state", WorktreePorts.FileName);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WorktreePorts Registry(Func<string, bool>? exists = null, int lockWaitMs = 30_000) => new(StatePath, exists ?? (_ => true), lockWaitMs);

    /// <summary>A state file in which the first <paramref name="count"/> blocks are taken, by /wt/0, /wt/1 and so on.</summary>
    /// <summary>How the registry stores a path on this platform.</summary>
    private static string Key(string path) => WorktreeList.Normalize(path, OperatingSystem.IsWindows());

    private void SeedBlocks(int count)
    {
        var lines = Enumerable.Range(0, count).Select(i => $"\"{Key($"/wt/{i}")}\": {WorktreePorts.RangeStart + i * WorktreePorts.BlockSize}");
        WriteState("{\"version\":1,\"blocks\":{" + string.Join(',', lines) + "}}");
    }

    private void WriteState(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, json);
    }

    private string MakeRepo(string name, bool linked)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        if (linked) File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /elsewhere/.git/worktrees/x\n");
        else Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    [Test]
    public async Task AWorktreeKeepsItsBlockAcrossRestarts()
    {
        // Arrange
        int? first = Registry().BlockFor("/r/a");
        Registry().BlockFor("/r/b");

        // Act
        int? afterRestart = new WorktreePorts(StatePath).BlockFor("/r/a");

        // Assert
        await Assert.That(first).IsNotNull();
        await Assert.That(afterRestart).IsEqualTo(first);
    }

    [Test]
    public async Task ATrailingSlashIsTheSameWorktree()
    {
        // Arrange
        int? first = Registry().BlockFor("/r/a");

        // Act
        int? again = Registry().BlockFor("/r/a/");

        // Assert
        await Assert.That(again).IsEqualTo(first);
    }

    [Test]
    public async Task WorktreesGetBlocksThatDoNotOverlap()
    {
        // Arrange
        var registry = Registry();

        // Act
        int a = registry.BlockFor("/r/a")!.Value, b = registry.BlockFor("/r/b")!.Value, c = registry.BlockFor("/r/c")!.Value;

        // Assert
        int[] sorted = [.. new[] { a, b, c }.Order()];
        await Assert.That(sorted[1] - sorted[0]).IsGreaterThanOrEqualTo(WorktreePorts.BlockSize);
        await Assert.That(sorted[2] - sorted[1]).IsGreaterThanOrEqualTo(WorktreePorts.BlockSize);
    }

    [Test]
    public async Task EveryBlockFitsBetweenTheFixedAndTheEphemeralPorts()
    {
        // Arrange
        var taken = new List<int>();

        // Act
        while (WorktreePorts.FirstFree(taken) is { } first) taken.Add(first);

        // Assert
        await Assert.That(taken.Count).IsEqualTo(450);
        await Assert.That(taken.Distinct().Count()).IsEqualTo(450);
        await Assert.That(taken.Min()).IsEqualTo(23000);
        await Assert.That(taken.Max() + WorktreePorts.BlockSize).IsLessThanOrEqualTo(32000);
    }

    [Test]
    public async Task AFullRangeGivesNoBlockRatherThanSharingOne()
    {
        // Arrange
        SeedBlocks(450);

        // Act
        int? block = Registry().BlockFor("/r/new");

        // Assert
        await Assert.That(block).IsNull();
    }

    [Test]
    public async Task AFullRangeTakesBackTheBlockOfARemovedWorktree()
    {
        // Arrange
        SeedBlocks(450);
        var registry = Registry(path => path != Key("/wt/7"));

        // Act
        int? block = registry.BlockFor("/r/new");
        int? stillThere = Registry().BlockFor("/wt/8");

        // Assert
        await Assert.That(block).IsEqualTo(WorktreePorts.RangeStart + 7 * WorktreePorts.BlockSize);
        await Assert.That(stillThere).IsEqualTo(WorktreePorts.RangeStart + 8 * WorktreePorts.BlockSize);
    }

    [Test]
    public async Task ARemovedWorktreeKeepsItsBlockWhileThereIsRoom()
    {
        // Arrange
        SeedBlocks(3);
        var registry = Registry(_ => false);

        // Act
        int? block = registry.BlockFor("/r/new");
        int? removed = registry.BlockFor("/wt/0");

        // Assert
        await Assert.That(block).IsEqualTo(WorktreePorts.RangeStart + 3 * WorktreePorts.BlockSize);
        await Assert.That(removed).IsEqualTo(WorktreePorts.RangeStart);
    }

    [Test]
    public async Task ProcessesAllocatingAtOnceDoNotCollide()
    {
        // Arrange
        var work = Enumerable.Range(0, 4).Select(p => Task.Run(() =>
            Enumerable.Range(0, 6).Select(i => Registry().BlockFor($"/r/{p}/{i}")).ToList()));

        // Act
        var blocks = (await Task.WhenAll(work)).SelectMany(b => b).ToList();

        // Assert
        await Assert.That(blocks.Count(b => b is null)).IsEqualTo(0);
        await Assert.That(blocks.Distinct().Count()).IsEqualTo(24);
    }

    [Test]
    public async Task ADamagedFileStartsOverInsteadOfFailing()
    {
        // Arrange
        WriteState("{ not json");

        // Act
        int? block = Registry().BlockFor("/r/a");

        // Assert
        await Assert.That(block).IsEqualTo(WorktreePorts.RangeStart);
    }

    [Test]
    public async Task EntriesOutsideTheRangeOrClaimingABlockTwiceAreIgnored()
    {
        // Arrange
        WriteState("""{"version":1,"blocks":{"/low":15000,"/high":32000,"/odd":23007,"/one":23000,"/two":23000}}""");
        var registry = Registry();

        // Act
        int? one = registry.BlockFor("/one"), two = registry.BlockFor("/two"), low = registry.BlockFor("/low");

        // Assert
        await Assert.That(new[] { one, two, low }.Distinct().Count()).IsEqualTo(3);
        await Assert.That(low!.Value).IsGreaterThanOrEqualTo(WorktreePorts.RangeStart);
    }

    [Test]
    public async Task AnUnwritableStateFileGivesNoBlock()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_dir, "state"), "a file where the directory should be");

        // Act
        int? block = new WorktreePorts(StatePath, lockWaitMs: 0).BlockFor("/r/a");

        // Assert
        await Assert.That(block).IsNull();
    }

    [Test]
    public async Task APaneInALinkedWorktreeGetsItsBlock()
    {
        // Arrange
        string root = MakeRepo("wt", linked: true);
        string sub = Directory.CreateDirectory(Path.Combine(root, "src", "app")).FullName;
        var env = new Dictionary<string, string>();

        // Act
        Registry().AddEnvironment(env, sub);

        // Assert
        await Assert.That(env[WorktreePorts.BaseVariable]).IsEqualTo(Registry().BlockFor(root).ToString());
        await Assert.That(env[WorktreePorts.CountVariable]).IsEqualTo("20");
    }

    [Test]
    public async Task AWorktreeInsideAnotherHasItsOwnBlock()
    {
        // Arrange
        string outer = MakeRepo("main", linked: false);
        string inner = Path.Combine(outer, ".claude", "worktrees", "a");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".git"), "gitdir: x\n");

        // Act
        string? innerRoot = WorktreePorts.WorktreeRoot(inner), outerRoot = WorktreePorts.WorktreeRoot(outer);

        // Assert
        await Assert.That(innerRoot).IsEqualTo(inner);
        await Assert.That(outerRoot).IsEqualTo(outer);
    }

    [Test]
    public async Task APaneOutsideAWorktreeLosesAnInheritedBlock()
    {
        // Arrange
        string plain = Directory.CreateDirectory(Path.Combine(_dir, "plain")).FullName;
        var env = new Dictionary<string, string> { [WorktreePorts.BaseVariable] = "23000", [WorktreePorts.CountVariable] = "20" };

        // Act
        Registry().AddEnvironment(env, plain);

        // Assert
        await Assert.That(env.ContainsKey(WorktreePorts.BaseVariable)).IsFalse();
        await Assert.That(env.ContainsKey(WorktreePorts.CountVariable)).IsFalse();
    }
}

/// <summary>Reading <c>aspire ps --format json</c>; the sample has the fields Aspire 13.6.4 prints.</summary>
public class AspireDashboardsTests
{
    private const string Running = """
        [
          {
            "appHostPath": "/r/wt-a/AppHost/AppHost.csproj",
            "appHostPid": 3144906,
            "status": "running",
            "sdkVersion": "13.6.4",
            "cliPid": 3144378,
            "dashboardUrl": "https://localhost:38653/login?t=abc",
            "logFilePath": "/home/u/.aspire/logs/x.log"
          },
          {
            "appHostPath": "/r/wt-b/apphost.cs",
            "appHostPid": 2,
            "status": "running",
            "cliPid": 3,
            "dashboardUrl": "http://localhost:41795/login?t=def"
          }
        ]
        """;

    [Test]
    public async Task ReadsPathLinkAndPortOfEachRunningAppHost()
    {
        // Act
        var dashboards = AspireDashboards.Parse(Running);

        // Assert
        await Assert.That(dashboards.Count).IsEqualTo(2);
        await Assert.That(dashboards[0].AppHostPath).IsEqualTo("/r/wt-a/AppHost/AppHost.csproj");
        await Assert.That(dashboards[0].Url).IsEqualTo("https://localhost:38653/login?t=abc");
        await Assert.That(dashboards[0].Port).IsEqualTo(38653);
        await Assert.That(dashboards[0].Label).IsEqualTo("aspire :38653");
    }

    [Test]
    [Arguments("[]")]
    [Arguments("")]
    [Arguments("Error: something went wrong")]
    [Arguments("{\"appHostPath\":\"/r/a\"}")]
    [Arguments("[1, \"x\", null]")]
    public async Task OutputWithoutAppHostsGivesNothing(string output)
    {
        // Act
        var dashboards = AspireDashboards.Parse(output);

        // Assert
        await Assert.That(dashboards.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""[{"appHostPath":"/r/a","status":"running"}]""")]
    [Arguments("""[{"appHostPath":"/r/a","status":"running","dashboardUrl":null}]""")]
    [Arguments("""[{"appHostPath":"/r/a","status":"starting","dashboardUrl":"https://localhost:1/"}]""")]
    [Arguments("""[{"status":"running","dashboardUrl":"https://localhost:1/"}]""")]
    [Arguments("""[{"appHostPath":"/r/a","dashboardUrl":"not a url"}]""")]
    [Arguments("""[{"appHostPath":"/r/a","dashboardUrl":"file:///etc/hostname"}]""")]
    public async Task AnAppHostWithoutAUsableDashboardIsLeftOut(string output)
    {
        // Act
        var dashboards = AspireDashboards.Parse(output);

        // Assert
        await Assert.That(dashboards.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnAppHostWithoutAStatusStillCounts()
    {
        // Act
        var dashboards = AspireDashboards.Parse("""[{"appHostPath":"/r/a","dashboardUrl":"https://localhost:1234/"}]""");

        // Assert
        await Assert.That(dashboards.Single().Port).IsEqualTo(1234);
    }

    [Test]
    public async Task EachDashboardGoesToTheWorktreeHoldingItsAppHost()
    {
        // Arrange
        var dashboards = AspireDashboards.Parse(Running);

        // Act
        var byRoot = AspireDashboards.ByWorktree(dashboards, ["/r", "/r/wt-a", "/r/wt-b", "/r/wt-c"]);

        // Assert
        await Assert.That(byRoot.Count).IsEqualTo(2);
        await Assert.That(byRoot["/r/wt-a"].Single().Port).IsEqualTo(38653);
        await Assert.That(byRoot["/r/wt-b"].Single().Port).IsEqualTo(41795);
    }

    [Test]
    public async Task AnAppHostOutsideEveryWorktreeIsNotShown()
    {
        // Arrange
        var dashboards = AspireDashboards.Parse(Running);

        // Act
        var byRoot = AspireDashboards.ByWorktree(dashboards, ["/other", "/r/wt-ab"]);

        // Assert
        await Assert.That(byRoot.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeveralAppHostsInAWorktreeAreInPortOrder()
    {
        // Arrange
        AspireDashboard[] dashboards =
        [
            new("/r/a/two/AppHost.csproj", "https://localhost:9/", 41000),
            new("/r/a/one/AppHost.csproj", "https://localhost:8/", 39000),
        ];

        // Act
        var byRoot = AspireDashboards.ByWorktree(dashboards, ["/r/a"]);

        // Assert
        await Assert.That(byRoot["/r/a"].Select(d => d.Port).ToArray()).IsEquivalentTo([39000, 41000]);
        await Assert.That(byRoot["/r/a"][0].Port).IsEqualTo(39000);
    }
}
