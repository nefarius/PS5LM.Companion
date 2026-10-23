using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>
/// Everything that differs between the two apps, as data. The rest of the code asks the profile
/// instead of branching on the variant.
/// </summary>
/// <param name="Variant">Which app.</param>
/// <param name="TitleId">The homebrew title ID.</param>
/// <param name="DisplayName">Name for the UI.</param>
/// <param name="StateRoots">
/// Candidate folders holding the apps' state (<c>settings.json</c>, <c>app.log</c>, the <c>load</c> and <c>unload</c>
/// command files), in preference order. Both apps use <c>/data/PS5LM</c>; PS5LM Plus falls back to its own sandbox
/// when the HEN does not open <c>/data</c>.
/// </param>
/// <param name="ExtraModelRoots">Read-only or removable-drive model folders outside the state roots.</param>
public sealed record VariantProfile(
    PS5Variant Variant,
    string TitleId,
    string DisplayName,
    IReadOnlyList<StorageRoot> StateRoots,
    IReadOnlyList<StorageRoot> ExtraModelRoots)
{
    public const string HomebrewRoot = "/data/homebrew";

    public const string ModelsFolder = "models";

    /// <summary>Command file the app polls: contains the model path to load.</summary>
    public const string LoadCommandFile = "load";

    /// <summary>Command file the app polls: presence requests unloading.</summary>
    public const string UnloadCommandFile = "unload";

    public string InstallPath => $"{HomebrewRoot}/{TitleId}";

    public static VariantProfile PS5LM { get; } = new(
        PS5Variant.PS5LM,
        "PPSA99575",
        "PS5LM",
        [new StorageRoot("Internal storage", "/data/PS5LM", "/data/PS5LM", Writable: true)],
        RemovableDrives());

    public static VariantProfile PS5LMPlus { get; } = new(
        PS5Variant.PS5LMPlus,
        "PPSA99581",
        "PS5LM Plus",
        [
            new StorageRoot("Internal storage", "/data/PS5LM", "/data/PS5LM", Writable: true),
            new StorageRoot("App space", "/mnt/sandbox/PPSA99581_000/download0/PS5LM", "/download0/PS5LM", Writable: true),
        ],
        [
            new StorageRoot("App folder", $"{HomebrewRoot}/PPSA99581/models", "/app0/models", Writable: false),
            .. RemovableDrives(),
        ]);

    public static IReadOnlyList<VariantProfile> All { get; } = [PS5LM, PS5LMPlus];

    public static VariantProfile? For(PS5Variant variant) => All.FirstOrDefault(p => p.Variant == variant);

    /// <summary>The root's models folder, as a storage root.</summary>
    public static StorageRoot ModelsOf(StorageRoot stateRoot) => new(
        stateRoot.Name,
        $"{stateRoot.FtpPath}/{ModelsFolder}",
        $"{stateRoot.AppPath}/{ModelsFolder}",
        stateRoot.Writable);

    private static StorageRoot[] RemovableDrives() =>
    [
        new StorageRoot("M.2 drive", "/mnt/ext0/PS5LM/models", "/mnt/ext0/PS5LM/models", Writable: true),
        new StorageRoot("USB 0", "/mnt/usb0/PS5LM/models", "/mnt/usb0/PS5LM/models", Writable: true),
        new StorageRoot("USB 1", "/mnt/usb1/PS5LM/models", "/mnt/usb1/PS5LM/models", Writable: true),
    ];
}
