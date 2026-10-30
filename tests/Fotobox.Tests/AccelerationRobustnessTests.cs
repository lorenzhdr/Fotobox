using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Software-Grafikadapter (WARP) und der null-sichere Umgang mit dem Windows-ML-Katalog (ohne WinRT).</summary>
public sealed class AccelerationRobustnessTests
{
    [Theory]
    [InlineData(0x1414u, 0x8Cu, "irgendwas", true)]               // WARP über Hersteller- und Geräte-Id
    [InlineData(0u, 0u, "Microsoft Basic Render Driver", true)]    // über die Beschreibung
    [InlineData(0u, 0u, "microsoft BASIC render driver", true)]
    [InlineData(0x1414u, 0x1234u, "Microsoft Test-Grafik", false)] // anderes Microsoft-Gerät
    [InlineData(0x8086u, 0x8Cu, "Testkarte", false)]
    [InlineData(1u, 1u, null, false)]
    [InlineData(1u, 1u, "", false)]
    public void IsSoftwareAdapter_RecognisesWarpOnly(uint vendor, uint device, string? description, bool expected) =>
        Assert.Equal(expected, AcceleratorSelector.IsSoftwareAdapter(vendor, device, description));

    [Fact]
    public void MapProviders_NullList_GivesAnEmptyList()
    {
        var states = AcceleratorCatalog.MapProviders<string>(null, _ => new ProviderState("x", "", "Ready", true));

        Assert.Empty(states);
    }

    [Fact]
    public void MapProviders_SkipsNullEntries_AndUnreadableProviders()
    {
        string?[] providers = ["gut", null, "kaputt", "auch gut"];

        var states = AcceleratorCatalog.MapProviders<string>(providers!, p =>
            p == "kaputt" ? throw new InvalidOperationException("nicht lesbar") : new ProviderState(p, "", "Ready", true));

        Assert.Equal(["gut", "auch gut"], states.Select(s => s.Name));
    }

    [Fact]
    public void ProviderNamesMissing_DoNotBreakTheFingerprint()
    {
        var fingerprint = AccelerationCache.Fingerprint(AccelDevices.WithCpu(), [new ProviderState("", "", "Ready", true)], "1.0", "ort");

        Assert.NotEmpty(fingerprint);
    }
}
