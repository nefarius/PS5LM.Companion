using System.Reflection;

namespace PS5LM.Companion.App.Updates;

internal interface IAssemblyVersionSource
{
    string? TryGetVersion();
}

/// <summary>Reads the informational version MinVer stamps on the running app.</summary>
internal sealed class AssemblyVersionSource : IAssemblyVersionSource
{
    public string? TryGetVersion()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }
}
