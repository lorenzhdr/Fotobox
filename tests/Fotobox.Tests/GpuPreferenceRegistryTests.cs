using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Windows-Grafikeinstellung „Hohe Leistung“ mit Fake-Registry (der echte Registry-Zugriff wird in Tests nie berührt).</summary>
public sealed class GpuPreferenceRegistryTests
{
    private const string Exe = "C:\\Testordner\\Fotobox.exe";

    private sealed class FakeStore : IGpuPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Failure { get; set; }
        public int Writes { get; private set; }

        public string? Read(string exePath)
        {
            if (Failure is not null) throw Failure;
            return Values.GetValueOrDefault(exePath);
        }

        public void Write(string exePath, string value)
        {
            if (Failure is not null) throw Failure;
            Writes++;
            Values[exePath] = value;
        }

        public void Delete(string exePath)
        {
            if (Failure is not null) throw Failure;
            Values.Remove(exePath);
        }
    }

    [Fact]
    public void On_SetsTheValueForTheOwnExe_Once()
    {
        var store = new FakeStore();
        var registry = new GpuPreferenceRegistry(store, Exe);

        Assert.Equal(GpuPreferenceResult.Set, registry.Apply(true));
        Assert.Equal("GpuPreference=2;", store.Values[Exe]);
        Assert.Equal(GpuPreferenceResult.AlreadySet, registry.Apply(true));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void On_DoesNotOverwriteAForeignValue()
    {
        var store = new FakeStore();
        store.Values[Exe] = "GpuPreference=1;";

        var result = new GpuPreferenceRegistry(store, Exe).Apply(true);

        Assert.Equal(GpuPreferenceResult.ForeignValueKept, result);
        Assert.Equal("GpuPreference=1;", store.Values[Exe]);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Off_RemovesOnlyOurOwnValue()
    {
        var store = new FakeStore();
        store.Values[Exe] = "GpuPreference=2;";
        store.Values["C:\\Anderes\\Programm.exe"] = "GpuPreference=2;";

        var result = new GpuPreferenceRegistry(store, Exe).Apply(false);

        Assert.Equal(GpuPreferenceResult.Removed, result);
        Assert.DoesNotContain(Exe, store.Values.Keys);
        Assert.Contains("C:\\Anderes\\Programm.exe", store.Values.Keys); // fremde Programme bleiben
    }

    [Theory]
    [InlineData("GpuPreference=1;")]
    [InlineData("GpuPreference=0;")]
    [InlineData("GpuPreference=2")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    public void Off_LeavesAnyOtherValueUntouched(string value)
    {
        var store = new FakeStore();
        store.Values[Exe] = value;

        var result = new GpuPreferenceRegistry(store, Exe).Apply(false);

        Assert.Equal(GpuPreferenceResult.ForeignValueKept, result);
        Assert.Equal(value, store.Values[Exe]);
    }

    [Fact]
    public void Off_WithoutAnyValue_DoesNothing()
    {
        var store = new FakeStore();

        Assert.Equal(GpuPreferenceResult.NothingToRemove, new GpuPreferenceRegistry(store, Exe).Apply(false));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void RegistryFailure_IsSwallowed()
    {
        var store = new FakeStore { Failure = new UnauthorizedAccessException("kein Zugriff") };

        Assert.Equal(GpuPreferenceResult.Failed, new GpuPreferenceRegistry(store, Exe).Apply(true));
        Assert.Equal(GpuPreferenceResult.Failed, new GpuPreferenceRegistry(store, Exe).Apply(false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownExePath_DoesNotTouchTheStore(string? exe)
    {
        var store = new FakeStore();

        Assert.Equal(GpuPreferenceResult.Failed, new GpuPreferenceRegistry(store, exe).Apply(true));
        Assert.Empty(store.Values);
    }
}
