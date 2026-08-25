using System.Text.RegularExpressions;

namespace AiStatus.Tests.Fixtures;

public sealed class FixtureSecurityTests
{
    private static readonly Regex BearerToken = new(
        @"Bearer\s+[A-Za-z0-9._-]{20,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Jwt = new(
        @"[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        RegexOptions.CultureInvariant);

    private static readonly Regex Email = new(
        @"\b[^\s@]+@[^\s@]+\.[^\s@]+\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Fixtures_ContainNoCredentialsOrIdentityData()
    {
        string fixtureDirectory = FindFixtureDirectory();
        string[] fixtures = Directory.GetFiles(fixtureDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(file => !string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(fixtures);

        foreach (string fixture in fixtures)
        {
            string contents = File.ReadAllText(fixture);
            Assert.DoesNotMatch(BearerToken, contents);
            Assert.DoesNotMatch(Jwt, contents);
            Assert.DoesNotMatch(Email, contents);
        }
    }

    private static string FindFixtureDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "AiStatus.Tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The source fixture directory was not found.");
    }
}
