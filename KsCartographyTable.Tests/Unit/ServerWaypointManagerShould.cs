using System;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using System.Collections.Generic;
using Vintagestory.GameContent;
using Vintagestory.API.Common;
using System.IO;
using System.Linq;
using Vintagestory.API.Config;

namespace KsCartographyTable.test.Unit;

[TestFixture("test valid playeruid", "RbeoiIPDZi9wTxVqQNIHVEVe", true, true)]
[TestFixture("test invalid playeruid", "invalid characters in guid<>:\"/\\|?*", false, true)]
// This UID is a valid Windows filename, but its legacy Base64 encoding contains '/'.
[TestFixture("test invalid base64 playeruid", "a\u00bfoiIPDZi9wTxVqQNIHVEVe", true, false)]
[NonParallelizable]
public class ServerWaypointManagerShould
{
    private ServerWaypointManager serverWaypointManager;
    private FakeCoreServerApi fakeCoreServerApi;
    private string testDataPath;

    private readonly FakePlayer player;
    private readonly Waypoint waypoint;

    private readonly bool fileV1ExpectedToExist;
    private readonly bool fileV2ExpectedToExist;

    public ServerWaypointManagerShould(string savegameIdentifier, string playerUID, bool testFileV1ExpectedToExist, bool testFileV2ExpectedToExist)
    {
        player = new FakePlayer(playerUID);
        waypoint = new Waypoint
        {
            Color = 1,
            Position = new Vintagestory.API.MathTools.Vec3d(),
            Guid = Guid.NewGuid().ToString(),
            Icon = "star",
            OwningPlayerUid = player.PlayerUID,
            Title = "test waypoint"
        };
        fileV1ExpectedToExist = testFileV1ExpectedToExist;
        fileV2ExpectedToExist = testFileV2ExpectedToExist;
        fakeCoreServerApi = new FakeCoreServerApi(savegameIdentifier);
    }

    
    [SetUp]
    public void Setup()
    {
        testDataPath = Directory.CreateTempSubdirectory("KsCartographyTable.Tests-").FullName;
        string originalDataPath = GamePaths.DataPath;
        try
        {
            // The constructor creates its data directory immediately, so redirect before calling it.
            GamePaths.DataPath = testDataPath;
            serverWaypointManager = new ServerWaypointManager(fakeCoreServerApi);
        }
        finally
        {
            GamePaths.DataPath = originalDataPath;
        }
    }

    [Test]
    public void ReturnEmptyListIfFileDoesntExist()
    {
        List<string> ids = [];
        try
        {
            ids = serverWaypointManager.GetDeletedWaypointsIds(player);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while recovering deleted waypoints: " + ex.Message);
        }
        Assert.That(ids, Is.Empty);
    }

    [Test]
    public void SaveDeletedWaypointIdsOnFile()
    {
        List<string> ids = [];
        try
        {
            serverWaypointManager.AddDeletedWaypointId(waypoint, player);
            ids = serverWaypointManager.GetDeletedWaypointsIds(player);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while adding deleted waypoints: " + ex.Message);
        }
        Assert.That(ids, Is.Not.Empty);
        Assert.That(ids, Does.Contain(waypoint.Guid));
        Assert.That(ids, Has.Count.EqualTo(1));
    }

    [Test]
    public void RenameV1FilenameIfPresent()
    {
        List<string> testIds = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
        string filePath = Path.Combine(serverWaypointManager.modDataPath, player.PlayerUID + ".json");

        if (!fileV1ExpectedToExist)
        {
            Assert.Pass();
        }

        try
        {
            string json = JsonUtil.ToString(testIds.ToList());
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while writing test ids file to disk: " + ex.Message);
        }

        List<string> ids = [];
        try
        {
            serverWaypointManager.AddDeletedWaypointId(waypoint, player);
            ids = serverWaypointManager.GetDeletedWaypointsIds(player);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while adding deleted waypoints: " + ex.Message);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(serverWaypointManager.GetWaypointsFilePath(player)), Is.True);
            Assert.That(ids, Is.Not.Empty);
        }

        Assert.That(ids, Does.Contain(testIds[0]));
        Assert.That(ids, Does.Contain(testIds[1]));
        Assert.That(ids, Does.Contain(waypoint.Guid));
        Assert.That(ids, Has.Count.EqualTo(3));
    }

    [Test]
    public void RenameV2FilenameIfPresent()
    {
        List<string> testIds = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];
        string filePath = Path.Combine(
            serverWaypointManager.modDataPath,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(player.PlayerUID)).TrimEnd('=') + ".json"
        );

        if (!fileV2ExpectedToExist)
        {
            Assert.Pass();
        }

        try
        {
            string json = JsonUtil.ToString(testIds.ToList());
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while writing test ids file to disk: " + ex.Message);
        }


        List<string> ids = [];
        try
        {
            serverWaypointManager.AddDeletedWaypointId(waypoint, player);
            ids = serverWaypointManager.GetDeletedWaypointsIds(player);
        }
        catch (Exception ex)
        {
            Assert.Fail("Exception while adding deleted waypoints: " + ex.Message);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(serverWaypointManager.GetWaypointsFilePath(player)), Is.True);
            Assert.That(ids, Is.Not.Empty);
        }

        Assert.That(ids, Does.Contain(testIds[0]));
        Assert.That(ids, Does.Contain(testIds[1]));
        Assert.That(ids, Does.Contain(waypoint.Guid));
        Assert.That(ids, Has.Count.EqualTo(3));
    }

    [TearDown]
    public void CleanUp()
    {
        if (testDataPath == null || !Directory.Exists(testDataPath)) return;

        string resolvedPath = Path.GetFullPath(testDataPath);
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.That(string.Equals(Path.GetDirectoryName(resolvedPath), temporaryRoot, comparison), Is.True,
            "Only the temporary directory created for this test may be deleted.");
        Directory.Delete(resolvedPath, true);
    }
}
