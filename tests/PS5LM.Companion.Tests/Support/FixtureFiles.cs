namespace PS5LM.Companion.Tests.Support;

/// <summary>Recorded upstream responses under <c>Fixtures/</c>, copied next to the test assembly.</summary>
internal static class FixtureFiles
{
    public static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]));

    public static byte[] ReadBytes(params string[] parts) =>
        File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]));
}
