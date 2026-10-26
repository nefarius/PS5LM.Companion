using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Core;

public sealed class DriveSpaceLocatorTests
{
    private static readonly ConsoleSensors Sensors = new(null, null, null, null,
    [
        new DriveSpace("/user", 1000, 400),
        new DriveSpace("/mnt/usb0", 2000, 900),
        new DriveSpace("/mnt/sandbox/download/PPSA99581", 3000, 700),
    ]);

    private static StorageRoot Root(string ftpPath) => new("x", ftpPath, ftpPath, true);

    [Theory]
    [InlineData("/data/PS5LM/models", 400)]
    [InlineData("/mnt/usb0/PS5LM/models", 900)]
    [InlineData("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models", 700)]
    public void Finds_the_drive_a_folder_lives_on(string path, long expected) =>
        Assert.Equal(expected, DriveSpaceLocator.FreeBytes(Sensors, Root(path)));

    [Theory]
    [InlineData("/mnt/ext0/PS5LM/models")]
    [InlineData("/somewhere/else")]
    public void Is_unknown_for_drives_the_exporter_does_not_list(string path) =>
        Assert.Null(DriveSpaceLocator.FreeBytes(Sensors, Root(path)));

    [Fact]
    public void Is_unknown_without_the_exporter()
    {
        Assert.Null(DriveSpaceLocator.FreeBytes(null, Root("/data/PS5LM/models")));
        Assert.Null(DriveSpaceLocator.FreeBytes(new ConsoleSensors(null, null, null, null, []), Root("/data/PS5LM/models")));
    }

    [Fact]
    public void Data_mount_is_the_data_partition_not_the_smallest_drive()
    {
        var sensors = new ConsoleSensors(null, null, null, null,
        [
            new DriveSpace("/", 2_000_000, 804 * 1024),
            new DriveSpace("/system_tmp", 4_000_000, (long)(1.7 * 1024 * 1024)),
            new DriveSpace("/mnt/usb0", 2000, 100),
            new DriveSpace("/user", 900_000_000_000, 500_000_000_000),
            new DriveSpace("/data", 800_000_000_000, 400_000_000_000),
        ]);

        var mount = DriveSpaceLocator.DataMount(sensors);

        Assert.Equal("/data", mount!.MountPoint);
        Assert.Equal(400_000_000_000, mount.FreeBytes);
    }

    [Fact]
    public void Data_mount_falls_back_to_the_user_partition()
    {
        var sensors = new ConsoleSensors(null, null, null, null,
        [
            new DriveSpace("/mnt/usb0", 2000, 50),
            new DriveSpace("/user", 1000, 400),
            new DriveSpace("/data", 0, 0),
        ]);

        Assert.Equal("/user", DriveSpaceLocator.DataMount(sensors)!.MountPoint);
    }

    [Fact]
    public void Data_mount_is_unknown_without_the_data_partition()
    {
        Assert.Null(DriveSpaceLocator.DataMount(null));
        Assert.Null(DriveSpaceLocator.DataMount(new ConsoleSensors(null, null, null, null, [])));
        Assert.Null(DriveSpaceLocator.DataMount(new ConsoleSensors(null, null, null, null, [new DriveSpace("/mnt/usb0", 2000, 50)])));
    }
}
