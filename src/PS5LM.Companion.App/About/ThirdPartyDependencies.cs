namespace PS5LM.Companion.App.About;

/// <summary>A NuGet dependency the shipped companion references, with the licence of the pinned version.</summary>
public sealed record ThirdPartyDependency(string Name, IReadOnlyList<string> PackageIds, string License, Uri Project);

/// <summary>
/// Credits for NuGet packages referenced by the shipped app (<c>src</c> projects and <c>Directory.Build.props</c>).
/// Test-only packages are not listed. Keep this in step with those references; see
/// <c>.cursor/rules/dependency-credits.mdc</c>.
/// </summary>
public static class ThirdPartyDependencies
{
    public static IReadOnlyList<ThirdPartyDependency> All { get; } =
    [
        new("FastEndpoints", ["FastEndpoints"], "MIT", new("https://github.com/FastEndpoints/FastEndpoints")),
        new("FluentFTP", ["FluentFTP"], "MIT", new("https://github.com/robinrodricks/FluentFTP")),
        new(
            "Microsoft.Extensions",
            ["Microsoft.Extensions.Http", "Microsoft.Extensions.Options.DataAnnotations"],
            "MIT",
            new("https://github.com/dotnet/runtime")),
        new(
            "Microsoft.Extensions.Http.Resilience",
            ["Microsoft.Extensions.Http.Resilience"],
            "MIT",
            new("https://github.com/dotnet/extensions")),
        new("MinVer (build-time)", ["MinVer"], "Apache-2.0", new("https://github.com/adamralph/minver")),
        new("MudBlazor", ["MudBlazor"], "MIT", new("https://github.com/MudBlazor/MudBlazor")),
        new("Refit", ["Refit", "Refit.HttpClientFactory"], "MIT", new("https://github.com/reactiveui/refit")),
        new("Serilog.AspNetCore", ["Serilog.AspNetCore"], "Apache-2.0", new("https://github.com/serilog/serilog-aspnetcore")),
        new("Serilog.Sinks.Console", ["Serilog.Sinks.Console"], "Apache-2.0", new("https://github.com/serilog/serilog-sinks-console")),
        new("Serilog.Sinks.File", ["Serilog.Sinks.File"], "Apache-2.0", new("https://github.com/serilog/serilog-sinks-file")),
        new("YARP", ["Yarp.ReverseProxy"], "MIT", new("https://github.com/dotnet/yarp")),
    ];
}
