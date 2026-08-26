using AiStatus.Platform;

namespace AiStatus.Tests.Platform;

public sealed class AutostartServiceTests
{
    [Fact]
    public void RegistryRunKey_UsesCurrentUserRunSubKey()
    {
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            RegistryRunKey.SubKeyPath);
    }

    [Fact]
    public void IsEnabled_ReadsCurrentRunKeyValueEveryTime()
    {
        var runKey = new FakeRunKey();
        var service = new AutostartService(runKey, @"C:\Program Files\QuotaGlass\QuotaGlass.exe");

        runKey.Value = null;
        Assert.False(service.IsEnabled);

        runKey.Value = "\"C:\\Program Files\\QuotaGlass\\QuotaGlass.exe\"";
        Assert.True(service.IsEnabled);
        Assert.Equal(2, runKey.GetValueCalls);
    }

    [Fact]
    public void SetEnabled_TrueWritesQuotedExecutablePathUnderApplicationValue()
    {
        var runKey = new FakeRunKey();
        var service = new AutostartService(runKey, @"C:\Program Files\QuotaGlass\QuotaGlass.exe");

        service.SetEnabled(true);

        Assert.Equal("QuotaGlass", runKey.SetName);
        Assert.Equal("\"C:\\Program Files\\QuotaGlass\\QuotaGlass.exe\"", runKey.SetValueText);
    }

    [Fact]
    public void SetEnabled_TrueQuotesExecutablePathExactlyOnce()
    {
        var runKey = new FakeRunKey();
        var service = new AutostartService(runKey, "\"C:\\Apps\\AiStatus.exe\"");

        service.SetEnabled(true);

        Assert.Equal("\"C:\\Apps\\AiStatus.exe\"", runKey.SetValueText);
    }

    [Fact]
    public void SetEnabled_FalseDeletesOnlyApplicationValue()
    {
        var runKey = new FakeRunKey();
        var service = new AutostartService(runKey, @"C:\Apps\AiStatus.exe");

        service.SetEnabled(false);

        Assert.Equal("QuotaGlass", runKey.DeletedName);
        Assert.Null(runKey.SetName);
    }

    private sealed class FakeRunKey : IRunKey
    {
        public string? Value { get; set; }
        public int GetValueCalls { get; private set; }
        public string? SetName { get; private set; }
        public string? SetValueText { get; private set; }
        public string? DeletedName { get; private set; }

        public string? GetValue(string name)
        {
            Assert.Equal("QuotaGlass", name);
            GetValueCalls++;
            return Value;
        }

        public void SetValue(string name, string value)
        {
            SetName = name;
            SetValueText = value;
        }

        public void DeleteValue(string name)
        {
            DeletedName = name;
        }
    }
}
