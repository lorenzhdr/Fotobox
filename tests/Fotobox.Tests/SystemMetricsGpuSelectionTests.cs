using Fotobox.Services.Diagnostics;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Auswahl der Grafikkarte für die Messung: Hersteller-/Geräte-Id, dann Name, dann der erste Anzeige-Adapter (nie NPU, nie Software).</summary>
public sealed class SystemMetricsGpuSelectionTests
{
    private static GraphicsAdapterInfo Adapter(ulong luid, uint vendor, uint device, string? name, bool render = true, bool display = true,
        bool software = false, bool computeOnly = false) =>
        new(luid, vendor, device, name, render, display, software, computeOnly);

    [Fact]
    public void ByIds_WinsOverTheName()
    {
        var a = Adapter(1, 0x1111, 0x0001, "Grafik A");
        var b = Adapter(2, 0x2222, 0x0002, "Grafik B");

        Assert.Same(b, SystemMetricsLogic.SelectGpu([a, b], new GpuSelection(0x2222, 0x0002, "Grafik A")));
    }

    [Fact]
    public void IdsFromAnotherIdSpace_FallBackToTheName()
    {
        var igpu = Adapter(1, 0x1111, 0x0001, "Integrierte Grafik 100");
        var other = Adapter(2, 0x2222, 0x0002, "Zweite Grafik 200 Serie");

        // Die Ids der Auswahl passen auf keinen Adapter (anderer Id-Raum), der Name schon.
        Assert.Same(other, SystemMetricsLogic.SelectGpu([igpu, other], new GpuSelection(0xAAAA, 0xBBBB, "Zweite Grafik 200 Serie")));
    }

    [Fact]
    public void Name_IsNormalized_AndEitherSideMayContainTheOther()
    {
        var a = Adapter(1, 0x1111, 0x0001, "Hersteller(R) Grafik(TM) 3000 Serie");
        var b = Adapter(2, 0x2222, 0x0002, "Anderer Hersteller Grafik 4000");

        Assert.Same(a, SystemMetricsLogic.SelectGpu([b, a], new GpuSelection(0, 0, "hersteller grafik 3000")));          // Auswahl im Adapternamen
        Assert.Same(b, SystemMetricsLogic.SelectGpu([a, b], new GpuSelection(9, 9, "ANDERER Hersteller Grafik 4000 Pro")));   // Adaptername in der Auswahl
    }

    [Fact]
    public void ExactName_BeatsAContainedName()
    {
        var shorter = Adapter(1, 0x1111, 0x0001, "Grafik 100");
        var longer = Adapter(2, 0x2222, 0x0002, "Grafik 100 Pro");

        Assert.Same(longer, SystemMetricsLogic.SelectGpu([shorter, longer], new GpuSelection(9, 9, "Grafik 100 Pro")));
    }

    [Fact]
    public void ShortOrUnknownNames_DoNotMatch_FallBackToTheFirstDisplayAdapter()
    {
        var first = Adapter(1, 0x1111, 0x0001, "Grafik A");
        var second = Adapter(2, 0x2222, 0x0002, "Grafik GPU B");

        Assert.Same(first, SystemMetricsLogic.SelectGpu([first, second], new GpuSelection(9, 9, "GPU")));          // zu kurz: passt auf alles
        Assert.Same(first, SystemMetricsLogic.SelectGpu([first, second], new GpuSelection(9, 9, "Völlig anderes Gerät")));
        Assert.Same(first, SystemMetricsLogic.SelectGpu([first, second], new GpuSelection(0, 0, null)));
    }

    [Fact]
    public void UnknownIds_ZeroZero_NeverMatchAnAdapterWithZeroIds()
    {
        var zero = Adapter(1, 0, 0, "Grafik A");
        var real = Adapter(2, 0x2222, 0x0002, "Grafik B");

        // Selektion 0/0 ist „unbekannt“; es entscheidet der Name, nicht die Gleichheit der Nullen.
        Assert.Same(real, SystemMetricsLogic.SelectGpu([zero, real], new GpuSelection(0, 0, "Grafik B")));
    }

    [Fact]
    public void ComputeOnlyAdapters_AreNeverChosen_EvenWhenIdsOrNameMatch()
    {
        var npu = Adapter(1, 0x1111, 0x0001, "KI-Einheit 7", render: false, display: false, computeOnly: true);
        var gpu = Adapter(2, 0x2222, 0x0002, "Grafik B");

        Assert.Same(gpu, SystemMetricsLogic.SelectGpu([npu, gpu], new GpuSelection(0x1111, 0x0001, "KI-Einheit 7")));
        Assert.Same(gpu, SystemMetricsLogic.SelectGpu([npu, gpu], new GpuSelection(9, 9, "KI-Einheit 7")));
        Assert.Null(SystemMetricsLogic.SelectGpu([npu], new GpuSelection(0x1111, 0x0001, "KI-Einheit 7")));
    }

    [Fact]
    public void SoftwareAdapters_AreNeverChosen_ByName()
    {
        var warp = Adapter(1, 0x1414, 0x008C, "Microsoft Basic Render Driver");
        var real = Adapter(2, 0x2222, 0x0002, "Grafik B");

        Assert.Same(real, SystemMetricsLogic.SelectGpu([warp, real], new GpuSelection(9, 9, "Microsoft Basic Render Driver")));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("Hersteller(R) Grafik(TM) 3000", "herstellergrafik3000")]
    [InlineData("Marke® Karte™ X-1", "markekartex1")]
    public void NormalizeAdapterName_Cases(string? name, string expected) =>
        Assert.Equal(expected, SystemMetricsLogic.NormalizeAdapterName(name));
}
