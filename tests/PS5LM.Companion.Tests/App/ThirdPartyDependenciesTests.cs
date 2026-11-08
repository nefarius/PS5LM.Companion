using System.Xml.Linq;

using PS5LM.Companion.App.About;

namespace PS5LM.Companion.Tests.App;

public sealed class ThirdPartyDependenciesTests
{
    [Fact]
    public void The_credit_list_matches_the_packages_the_shipped_app_references()
    {
        var referenced = ShippedPackageReferences(FindRepositoryRoot());
        var listed = ThirdPartyDependencies.All.SelectMany(dependency => dependency.PackageIds).ToList();

        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            referenced.OrderBy(id => id, StringComparer.Ordinal),
            listed.OrderBy(id => id, StringComparer.Ordinal));

        foreach (var dependency in ThirdPartyDependencies.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(dependency.Name));
            Assert.False(string.IsNullOrWhiteSpace(dependency.License));
            Assert.Equal(Uri.UriSchemeHttps, dependency.Project.Scheme);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find Directory.Packages.props above the test output.");
    }

    private static HashSet<string> ShippedPackageReferences(string root)
    {
        var files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "Directory.Build.props"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var id = element.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
