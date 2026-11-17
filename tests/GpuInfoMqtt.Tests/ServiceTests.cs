using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpuInfoMqtt.Tests;

public class GpuReaderTests
{
    [Fact]
    public void Parses_nvidia_smi_csv()
    {
        var gpus = GpuReader.Parse("0, GPU-abc, NVIDIA GeForce RTX 5090, 33.71, 600.00, 2216, 32607, 49, 0, 17\n");

        var g = Assert.Single(gpus);
        Assert.Equal("NVIDIA GeForce RTX 5090", g.Name);
        Assert.Equal(33.71, g.Power);
        Assert.Equal(600, g.PowerLimit);
        Assert.Equal(49, g.Temperature);
        Assert.Equal(0, g.Fan);
        Assert.Equal(6.8, g.MemoryPercent);
    }

    [Fact]
    public void Unsupported_values_become_null_and_multiple_gpus_are_read()
    {
        var gpus = GpuReader.Parse(
            "0, GPU-a, Tesla T4, [N/A], [N/A], 100, 15360, 40, [Not Supported], 0\r\n" +
            "1, GPU-b, RTX A2000, 20.5, 70.00, 0, 6138, 35, 30, 1\r\n");

        Assert.Equal(2, gpus.Length);
        Assert.Null(gpus[0].Power);
        Assert.Null(gpus[0].Fan);
        Assert.Equal(1, gpus[1].Index);
    }

    [Fact]
    public void Gpu_name_containing_a_comma_is_kept_whole()
    {
        var g = Assert.Single(GpuReader.Parse("0, GPU-a, Weird, Name, 10, 100, 1, 2, 3, 4, 5"));
        Assert.Equal("Weird,Name", g.Name);
        Assert.Equal(10, g.Power);
    }
}

public class NvidiaSmiLocatorTests
{
    [Theory]
    [InlineData("nvidia-smi")]
    [InlineData("NVIDIA-SMI.exe")]
    [InlineData("")]
    public void Bare_names_are_allowed(string path) => Assert.True(NvidiaSmiLocator.IsAllowed(path));

    [Fact]
    public void System32_copy_is_allowed() =>
        Assert.True(NvidiaSmiLocator.IsAllowed(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")));

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Users\Public\nvidia-smi.exe")]
    [InlineData(@"C:\Windows\System32\..\Temp\nvidia-smi.exe")]
    [InlineData(@"\\server\share\nvidia-smi.exe")]
    public void Anything_else_is_rejected(string path) => Assert.False(NvidiaSmiLocator.IsAllowed(path));
}

public class SettingsValidationTests
{
    private static SettingsUpdate Valid() => new(
        true, "homeassistant.local", 1883, false, false, "gpuinfo", null, false,
        "homeassistant", "gpuinfo", "DESKTOP", 10, "nvidia-smi");

    [Fact]
    public void Valid_settings_pass() => Assert.Empty(Valid().Validate());

    [Fact]
    public void Wildcards_and_bad_ranges_are_rejected()
    {
        var errors = (Valid() with { Port = 0, IntervalSeconds = 1, BaseTopic = "gpu/#" }).Validate().ToList();
        Assert.Equal(3, errors.Count);
    }
}

[Collection("settings")]
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpuinfomqtt-test-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Environment.SetEnvironmentVariable("GPUINFOMQTT_DATA", _dir);

    [Fact]
    public void Password_is_encrypted_at_rest_and_redirect_requires_reentry()
    {
        // SettingsStore.Directory is resolved once per process, so read where it actually points.
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance);
        var update = new SettingsUpdate(false, "broker.test", 1883, false, false, "u", "Pa55word!", false,
            "homeassistant", "gpuinfo", "DEV", 10, "nvidia-smi");
        store.Save(update);

        var json = File.ReadAllText(SettingsStore.FilePath, Encoding.UTF8);
        Assert.DoesNotContain("Pa55word!", json);
        Assert.Contains("\"v2:", json);
        Assert.Equal("Pa55word!", store.GetPassword(store.Current));

        var redirect = update with { Host = "evil.example", Password = null };
        Assert.NotEmpty(store.ValidateCredentialBinding(redirect));
        Assert.Empty(store.ValidateCredentialBinding(update with { DeviceName = "OTHER", Password = null }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
