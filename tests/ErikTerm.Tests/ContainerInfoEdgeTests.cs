using ErikTerm.App.Containers;

namespace ErikTerm.Tests;

/// <summary>Container inspect output that's partial or odd, display names and the mount path edge cases.</summary>
public class ContainerInfoEdgeTests
{
    private static ContainerInfo Info(string id = "0123456789abcdef", string name = "", string? folder = null, string? workingDir = null,
        IReadOnlyList<ContainerMount>? mounts = null, string? composeDir = null) =>
        new(id, name, true, "", folder, null, workingDir, mounts ?? [], composeDir);

    [Test]
    [Arguments("0123456789abcdef", "0123456789ab")]
    [Arguments("abc", "abc")]
    public async Task ShortIdIsTwelveCharactersAtMost(string id, string expected)
    {
        // Act
        string shortId = Info(id).ShortId;

        // Assert
        await Assert.That(shortId).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/home/me/src/app/", "web", "app")]   // the folder's name, without its trailing slash
    [Arguments("C:\\src\\shop", "web", "shop")]
    [Arguments("app", "web", "app")]                  // no directory part
    [Arguments("", "web", "web")]                     // an empty label: the container's name
    [Arguments(null, "web", "web")]
    [Arguments(null, "", "0123456789ab")]             // nothing named: the short id
    public async Task DisplayNamePrefersTheFolderThenTheName(string? folder, string name, string expected)
    {
        // Act
        string display = Info(name: name, folder: folder).DisplayName;

        // Assert
        await Assert.That(display).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/home/me/app", "/srv", "/workspaces/app")]   // the folder's mount
    [Arguments("/home/me/other", "/srv", "/srv")]            // not mounted: the working directory
    [Arguments(null, "/srv", "/srv")]
    [Arguments(null, "", "/")]
    [Arguments(null, null, "/")]
    public async Task WorkspaceFolderFallsBackToTheWorkingDirectoryThenRoot(string? folder, string? workingDir, string expected)
    {
        // Arrange
        var mounts = new[] { new ContainerMount("/home/me/app", "/workspaces/app") };

        // Act
        string workspace = Info(folder: folder, workingDir: workingDir, mounts: mounts).WorkspaceFolder;

        // Assert
        await Assert.That(workspace).IsEqualTo(expected);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("42")]
    [Arguments("[{\"Name\":\"x\"}]")]   // no id
    [Arguments("[{\"Id\":\"\"}]")]
    [Arguments("{not json")]
    public async Task InspectWithoutAContainerIsNull(string json)
    {
        // Act
        var info = ContainerInfo.ParseInspect(json);

        // Assert
        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task InspectOfASingleObjectWithoutStateOrConfig()
    {
        // Act
        var info = ContainerInfo.ParseInspect("""{"Id":"abc","Name":"/web","State":"running","Config":null,"Mounts":{}}""");

        // Assert
        await Assert.That(info).IsEqualTo(new ContainerInfo("abc", "web", false, "", null, null, null, info!.Mounts));
        await Assert.That(info.Mounts).IsEmpty();
    }

    [Test]
    public async Task InspectWithLabelsThatArentAnObject()
    {
        // Act
        var info = ContainerInfo.ParseInspect("""{"Id":"abc","State":{"Running":false,"StartedAt":"2026-01-01"},"Config":{"WorkingDir":"/w","Labels":null}}""");

        // Assert
        await Assert.That((info!.Running, info.StartedAt, info.WorkingDir, info.LocalFolder)).IsEqualTo((false, "2026-01-01", "/w", (string?)null));
    }

    [Test]
    public async Task OnlyBindMountsWithBothEndsMapPaths()
    {
        // Arrange
        const string json = """
            {"Id":"abc","Mounts":[
              "not an object",
              {"Type":"volume","Source":"/var/lib/docker/volumes/x","Destination":"/data"},
              {"Type":"bind","Source":"","Destination":"/empty"},
              {"Type":"bind","Source":"/home/me","Destination":7},
              {"Type":"BIND","Source":"/home/me/app","Destination":"/app"}]}
            """;

        // Act
        var info = ContainerInfo.ParseInspect(json);

        // Assert
        await Assert.That(info!.Mounts).IsEquivalentTo(new[] { new ContainerMount("/home/me/app", "/app") }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null)]
    [Arguments("  ")]
    [Arguments("not json")]
    [Arguments("[1, \"x\"]")]   // entries that aren't objects
    public async Task RemoteUserWithoutUsableMetadataIsNull(string? metadata)
    {
        // Act
        string? user = ContainerInfo.RemoteUserOf(metadata);

        // Assert
        await Assert.That(user).IsNull();
    }

    [Test]
    public async Task RemoteUserFromASingleObject()
    {
        // Act
        string? user = ContainerInfo.RemoteUserOf("""{"remoteUser":"vscode","containerUser":"root"}""");

        // Assert
        await Assert.That(user).IsEqualTo("vscode");
    }

    [Test]
    public async Task LaterMetadataEntriesWin()
    {
        // Act
        string? user = ContainerInfo.RemoteUserOf("""[{"remoteUser":"node"},{"containerUser":"root"},{"remoteUser":"dev"}]""");

        // Assert
        await Assert.That(user).IsEqualTo("dev");
    }

    [Test]
    public async Task ComposeWithoutACwdTakesTheOnlyCandidate()
    {
        // Act
        var picked = ContainerInfo.PickCompose([Info(composeDir: "/home/me/shop")], null);

        // Assert
        await Assert.That(picked).IsNotNull();
    }

    [Test]
    public async Task ComposeWithSeveralCandidatesAndNoProjectDirsIsUndecided()
    {
        // Act
        var picked = ContainerInfo.PickCompose([Info(), Info(id: "other")], "/home/me");

        // Assert
        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task ComposeProjectDirWithATrailingSlashHoldsTheCwd()
    {
        // Arrange
        var shop = Info(id: "shop", composeDir: "/home/me/shop/");

        // Act
        var picked = ContainerInfo.PickCompose([Info(id: "x", composeDir: "/elsewhere"), shop], "/home/me/shop");

        // Assert
        await Assert.That(picked).IsEqualTo(shop);
    }

    [Test]
    [Arguments("")]
    [Arguments("relative/path")]
    [Arguments("C:")]          // too short to be a drive path
    [Arguments("1:/x")]        // no drive letter
    public async Task PathsThatArentAbsoluteDontMap(string path)
    {
        // Arrange
        var mounts = new[] { new ContainerMount("/", "/host") };

        // Act
        string? mapped = ContainerPaths.ToHost(mounts, path);

        // Assert
        await Assert.That(mapped).IsNull();
    }

    [Test]
    [Arguments("C:/src/app/x", "/app/x")]
    [Arguments("C:\\src", null)]   // a backslash drive path is absolute, but no mount uses that spelling
    public async Task DrivePathsMapThroughTheirMount(string path, string? expected)
    {
        // Arrange
        var mounts = new[] { new ContainerMount("C:/src/app", "/app") };

        // Act
        string? mapped = ContainerPaths.ToContainer(mounts, path);

        // Assert
        await Assert.That(mapped).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/etc/hosts", "/host/etc/hosts")]
    [Arguments("/", "/host")]
    public async Task ARootMountHoldsEveryPath(string path, string expected)
    {
        // Arrange
        var mounts = new[] { new ContainerMount("/host/", "/") };

        // Act
        string? mapped = ContainerPaths.ToHost(mounts, path);

        // Assert
        await Assert.That(mapped).IsEqualTo(expected);
    }

    [Test]
    public async Task AMountIntoTheRootDirectoryJoinsWithoutADoubleSlash()
    {
        // Arrange
        var mounts = new[] { new ContainerMount("/home/me/app", "/") };

        // Act
        string? mapped = ContainerPaths.ToContainer(mounts, "/home/me/app/src");

        // Assert
        await Assert.That(mapped).IsEqualTo("/src");
    }

    [Test]
    public async Task APathThatOnlySharesAPrefixIsNotInTheMount()
    {
        // Arrange
        var mounts = new[] { new ContainerMount("/home/me/app", "/app") };

        // Act
        string? mapped = ContainerPaths.ToContainer(mounts, "/home/me/application");

        // Assert
        await Assert.That(mapped).IsNull();
    }
}
