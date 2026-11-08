using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using PS5LM.Companion.App.About;
using PS5LM.Companion.App.Components.Pages;
using PS5LM.Companion.App.Updates;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.Tests.App;

public sealed class AboutPageTests
{
    [Fact]
    public async Task The_about_page_credits_the_project_and_every_shipped_dependency()
    {
        var checker = new LatestReleaseChecker(new HttpClient(), new FixedVersion("1.2.3"));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(checker);

        var cut = ctx.Render<About>();

        Assert.Contains("Made by Nefarius Software Solutions e.U. and their trusty AI Orb.", cut.Markup);
        Assert.Contains("Version v1.2.3", cut.Markup);
        Assert.Contains("href=\"https://github.com/nefarius/PS5LM.Companion/\"", cut.Markup);
        Assert.Contains("noopener", cut.Markup);
        Assert.Contains("MIT licence", cut.Markup);

        foreach (var dependency in ThirdPartyDependencies.All)
        {
            Assert.Contains(dependency.Name, cut.Markup);
            Assert.Contains(dependency.License, cut.Markup);
            Assert.Contains($"href=\"{dependency.Project}\"", cut.Markup);
            foreach (var packageId in dependency.PackageIds)
            {
                Assert.Contains(packageId, cut.Markup);
            }
        }
    }

    private sealed class FixedVersion(string? version) : IAssemblyVersionSource
    {
        public string? TryGetVersion() => version;
    }
}
