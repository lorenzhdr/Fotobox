using System.IO;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

public sealed class PinServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private (JsonSettingsStore Store, PinService Pin) Create()
    {
        var store = new JsonSettingsStore(_dir);
        return (store, new PinService(store));
    }

    [Fact]
    public void NewStore_HasNoPin_AndVerifyFails()
    {
        var (_, pin) = Create();

        Assert.False(pin.HasPin);
        Assert.False(pin.Verify("1234"));
        Assert.False(pin.Verify(""));
    }

    [Fact]
    public void SetPin_ThenVerify_Succeeds()
    {
        var (store, pin) = Create();

        pin.SetPin("4711");

        Assert.True(pin.HasPin);
        Assert.True(pin.Verify("4711"));
        Assert.False(pin.Verify("4712"));
        Assert.False(pin.Verify("471"));
        Assert.False(pin.Verify(""));
        Assert.False(pin.Verify(null!));
        Assert.DoesNotContain("4711", File.ReadAllText(Path.Combine(store.ConfigDirectory, "settings.json")));
    }

    [Fact]
    public void SetPin_PersistsAcrossStoreInstances()
    {
        var (_, pin) = Create();
        pin.SetPin("987654");

        var pin2 = new PinService(new JsonSettingsStore(_dir));

        Assert.True(pin2.HasPin);
        Assert.True(pin2.Verify("987654"));
        Assert.False(pin2.Verify("987655"));
    }

    [Fact]
    public void SetPin_StoresSaltAndHashWithExpectedSizes()
    {
        var (store, pin) = Create();
        pin.SetPin("0000");

        var sec = store.Current.Security;
        Assert.Equal(16, Convert.FromBase64String(sec.PinSalt!).Length);
        Assert.Equal(32, Convert.FromBase64String(sec.PinHash!).Length);
        Assert.True(sec.PinIterations >= 10_000);
    }

    [Fact]
    public void SetPin_SamePinTwice_YieldsDifferentSaltAndHash()
    {
        var (store, pin) = Create();

        pin.SetPin("1234");
        var first = store.Current.Security;
        var (salt1, hash1) = (first.PinSalt, first.PinHash);
        pin.SetPin("1234");
        var second = store.Current.Security;

        Assert.NotEqual(salt1, second.PinSalt);
        Assert.NotEqual(hash1, second.PinHash);
        Assert.True(pin.Verify("1234"));
    }

    [Fact]
    public void SetPin_KeepsOtherSettings_AndRaisesSettingsChanged()
    {
        var (store, pin) = Create();
        var s = store.Current.Clone();
        s.Flow.CountdownSeconds = 9;
        store.Save(s);
        var changed = 0;
        store.SettingsChanged += (_, _) => changed++;

        pin.SetPin("5555");

        Assert.Equal(9, store.Current.Flow.CountdownSeconds);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("12a4")]
    [InlineData("12 34")]
    [InlineData("١٢٣٤")] // arabisch-indische Ziffern sind nicht erlaubt
    public void SetPin_InvalidPin_ThrowsArgumentException(string value)
    {
        var (_, pin) = Create();

        var ex = Assert.Throws<ArgumentException>(() => pin.SetPin(value));

        Assert.Contains("Ziffern", ex.Message);
        Assert.False(pin.HasPin);
    }

    [Fact]
    public void SetPin_Null_Throws()
    {
        var (_, pin) = Create();

        Assert.ThrowsAny<ArgumentException>(() => pin.SetPin(null!));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("123456789012")]
    public void SetPin_BoundaryLengths_AreAccepted(string value)
    {
        var (_, pin) = Create();

        pin.SetPin(value);

        Assert.True(pin.Verify(value));
    }

    [Fact]
    public void Verify_WithCorruptStoredData_ReturnsFalse()
    {
        var (store, pin) = Create();
        var s = store.Current.Clone();
        s.Security.PinHash = "###";
        s.Security.PinSalt = "###";
        store.Save(s);

        Assert.False(pin.Verify("1234"));
    }

    [Fact]
    public void Verify_ImplausiblyHighIterations_ReturnsFalseImmediately_AndKeepsStoredValue()
    {
        var (store, pin) = Create();
        pin.SetPin("1234");
        var s = store.Current.Clone();
        s.Security.PinIterations = int.MaxValue; // würde PBKDF2 minutenlang rechnen lassen
        store.Save(s);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ok = pin.Verify("1234");
        watch.Stop();

        Assert.False(ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Dauerte {watch.Elapsed}");
        Assert.Equal(int.MaxValue, store.Current.Security.PinIterations); // nicht geklemmt
    }

    [Fact]
    public void SetPin_WithImplausiblyHighStoredIterations_UsesDefaultInsteadOfHanging()
    {
        var (store, pin) = Create();
        var s = store.Current.Clone();
        s.Security.PinIterations = 2_000_000_000; // z. B. aus manipulierter Datei ohne Hash
        store.Save(s);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        pin.SetPin("1234");
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Dauerte {watch.Elapsed}");
        Assert.Equal(100_000, store.Current.Security.PinIterations);
        Assert.True(pin.Verify("1234"));
    }

    [Fact]
    public void Verify_HashOfWrongLength_ReturnsFalse()
    {
        var (store, pin) = Create();
        pin.SetPin("1234");
        var s = store.Current.Clone();
        s.Security.PinHash = Convert.ToBase64String(new byte[16]);
        store.Save(s);

        Assert.False(pin.Verify("1234"));

        s.Security.PinHash = Convert.ToBase64String(new byte[64]);
        store.Save(s);

        Assert.False(pin.Verify("1234"));
    }

    [Fact]
    public void Verify_WithRaisedButPlausibleIterations_StillWorks()
    {
        var (store, pin) = Create();
        var s = store.Current.Clone();
        s.Security.PinIterations = 20_000;
        store.Save(s);
        pin.SetPin("2468");

        Assert.True(pin.Verify("2468"));
    }
}
