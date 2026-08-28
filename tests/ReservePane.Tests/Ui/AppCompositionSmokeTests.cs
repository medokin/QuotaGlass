using System.Reflection;

namespace QuotaGlass.Tests.Ui;

public sealed class AppCompositionSmokeTests
{
    [Fact]
    public void Application_OwnsStartupAndExitWithoutStartupUri()
    {
        string xaml = File.ReadAllText(FindAppXaml());

        Assert.DoesNotContain("StartupUri=", xaml, StringComparison.Ordinal);
        Assert.Equal(
            typeof(App),
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType);
        Assert.Equal(
            typeof(App),
            typeof(App).GetMethod("OnExit", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType);
        Assert.NotNull(typeof(App).GetField("_hotkey", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static string FindAppXaml()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "QuotaGlass", "App.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("App.xaml was not found.");
    }
}
