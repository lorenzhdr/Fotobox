using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Normalisierung der Video-Einstellungen, Tasten-Konflikte und die Migration von Version 1 auf 2.</summary>
public sealed class VideoSettingsNormalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private JsonSettingsStore StoreWithFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, json);
        return new JsonSettingsStore(_dir);
    }

    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    // ---- Zahlen

    [Fact]
    public void Video_NumbersAboveLimits_AreClamped()
    {
        var v = Normalized(s =>
        {
            s.Video.MaxDurationSeconds = 99_999;
            s.Video.CountdownSeconds = 99;
            s.Video.MinDurationSeconds = 99;
            s.Video.WarningSeconds = 999;
            s.Video.SuccessDisplaySeconds = 999;
        }).Video;

        Assert.Equal(SettingsLimits.VideoMaxDurationMax, v.MaxDurationSeconds);
        Assert.Equal(SettingsLimits.VideoCountdownMax, v.CountdownSeconds);
        Assert.Equal(SettingsLimits.VideoMinDurationMax, v.MinDurationSeconds);
        Assert.Equal(SettingsLimits.VideoWarningMax, v.WarningSeconds);
        Assert.Equal(SettingsLimits.VideoSuccessDisplayMax, v.SuccessDisplaySeconds);
    }

    [Fact]
    public void Video_NumbersBelowLimits_AreClamped()
    {
        var v = Normalized(s =>
        {
            s.Video.MaxDurationSeconds = -5;
            s.Video.CountdownSeconds = -1;
            s.Video.MinDurationSeconds = -3;
            s.Video.WarningSeconds = -10;
            s.Video.SuccessDisplaySeconds = 0;
        }).Video;

        Assert.Equal(SettingsLimits.VideoMaxDurationMin, v.MaxDurationSeconds);
        Assert.Equal(SettingsLimits.VideoCountdownMin, v.CountdownSeconds);
        Assert.Equal(SettingsLimits.VideoMinDurationMin, v.MinDurationSeconds);
        Assert.Equal(SettingsLimits.VideoWarningMin, v.WarningSeconds);
        Assert.Equal(SettingsLimits.VideoSuccessDisplayMin, v.SuccessDisplaySeconds);
    }

    [Theory]
    [InlineData(10, 10, 9)]    // gleich → Höchstdauer − 1
    [InlineData(10, 20, 9)]    // darüber → Höchstdauer − 1
    [InlineData(15, 30, 14)]
    [InlineData(10, 9, 9)]     // knapp darunter bleibt
    [InlineData(300, 2, 2)]    // Standard bleibt
    [InlineData(10, 0, 0)]     // 0 = kein Mindestlänge-Schutz bleibt
    public void Video_MinDurationIsAlwaysBelowMaxDuration(int max, int min, int expectedMin)
    {
        var v = Normalized(s =>
        {
            s.Video.MaxDurationSeconds = max;
            s.Video.MinDurationSeconds = min;
        }).Video;

        Assert.Equal(max, v.MaxDurationSeconds);
        Assert.Equal(expectedMin, v.MinDurationSeconds);
        Assert.True(v.MinDurationSeconds < v.MaxDurationSeconds);
    }

    [Fact]
    public void Video_MinAboveMaxAfterClamping_UsesLimitsOnlyForBothValues()
    {
        // Max wird auf 10 geklemmt (−7 → 10), Min auf 30 (45 → 30) → danach Min = Max − 1.
        var v = Normalized(s =>
        {
            s.Video.MaxDurationSeconds = -7;
            s.Video.MinDurationSeconds = 45;
        }).Video;

        Assert.Equal(SettingsLimits.VideoMaxDurationMin, v.MaxDurationSeconds);
        Assert.Equal(SettingsLimits.VideoMaxDurationMin - 1, v.MinDurationSeconds);
    }

    // ---- Enum, Texte, Ordner

    [Theory]
    [InlineData("\"Ultra\"", VideoQuality.High)]
    [InlineData("42", VideoQuality.High)]
    [InlineData("\"Medium\"", VideoQuality.Medium)]
    [InlineData("\"Low\"", VideoQuality.Low)]
    public void Quality_FromJson_UnknownBecomesHigh_KnownIsKept(string json, VideoQuality expected)
    {
        var store = StoreWithFile("{ \"Version\": 2, \"Video\": { \"Quality\": " + json + " } }");

        Assert.False(store.LoadFailed);
        Assert.Equal(expected, store.Current.Video.Quality);
    }

    [Fact]
    public void Quality_UndefinedValue_BecomesHigh()
    {
        var v = Normalized(s => s.Video.Quality = (VideoQuality)99).Video;

        Assert.Equal(VideoQuality.High, v.Quality);
    }

    [Fact]
    public void VideoNull_IsReplacedWithDefaults()
    {
        var store = StoreWithFile("{ \"Version\": 2, \"Video\": null }");

        Assert.False(store.LoadFailed);
        Assert.NotNull(store.Current.Video);
        Assert.Equal(300, store.Current.Video.MaxDurationSeconds);
    }

    [Fact]
    public void SuccessText_IsCleanedTruncated_AndWhitespaceBecomesNull()
    {
        Assert.Null(Normalized(s => s.Video.SuccessText = "   ").Video.SuccessText);
        Assert.Null(Normalized(s => s.Video.SuccessText = "").Video.SuccessText);
        Assert.Null(Normalized(s => s.Video.SuccessText = null).Video.SuccessText);
        Assert.Equal("Super  Clip",Normalized(s => s.Video.SuccessText = "  Super\r\nClip ").Video.SuccessText);

        var longText = Normalized(s => s.Video.SuccessText = new string('x', SettingsLimits.VideoSuccessTextMaxLength + 30)).Video.SuccessText;
        Assert.Equal(SettingsLimits.VideoSuccessTextMaxLength, longText!.Length);
    }

    [Fact]
    public void StopHintText_IsCleanedTruncated_AndWhitespaceBecomesNull()
    {
        Assert.Null(Normalized(s => s.Video.StopHintText = "   ").Video.StopHintText);
        Assert.Null(Normalized(s => s.Video.StopHintText = "").Video.StopHintText);
        Assert.Null(Normalized(s => s.Video.StopHintText = null).Video.StopHintText);
        Assert.Equal("Stopp  mit {Taste}", Normalized(s => s.Video.StopHintText = "  Stopp\r\nmit {Taste} ").Video.StopHintText);

        var longText = Normalized(s => s.Video.StopHintText = new string('x', SettingsLimits.HintTextMaxLength + 30)).Video.StopHintText;
        Assert.Equal(SettingsLimits.HintTextMaxLength, longText!.Length);
    }

    [Fact]
    public void VideoFilePrefix_IsSanitized_AndEmptyBecomesVideoDefault()
    {
        Assert.Equal("Fotobox+_Video", Normalized(s => s.Video.FilePrefix = "").Video.FilePrefix);
        Assert.Equal("Fotobox+_Video", Normalized(s => s.Video.FilePrefix = "???").Video.FilePrefix);
        Assert.Equal("Fotobox+_Video", Normalized(s => s.Video.FilePrefix = "   ").Video.FilePrefix);
        Assert.Equal("Fest_Clip", Normalized(s => s.Video.FilePrefix = "Fest/_Clip?").Video.FilePrefix);
        Assert.Equal(SettingsLimits.FilePrefixMaxLength,
            Normalized(s => s.Video.FilePrefix = new string('a', 200)).Video.FilePrefix.Length);
    }

    [Fact]
    public void PhotoPrefixDefault_IsNotAffectedByVideoFallback()
    {
        Assert.Equal("Fotobox+", SettingsNormalizer.SanitizePrefix(""));
        Assert.Equal("Fotobox+_Video", SettingsNormalizer.SanitizePrefix("", VideoSettings.DefaultFilePrefix));
    }

    [Fact]
    public void Microphone_TooLongOrEmptyId_IsDropped_WithItsName()
    {
        var tooLong = Normalized(s =>
        {
            s.Video.MicrophoneId = new string('m', SettingsLimits.MicrophoneIdMaxLength + 1);
            s.Video.MicrophoneName = "Mikro";
        }).Video;
        Assert.Null(tooLong.MicrophoneId);
        Assert.Null(tooLong.MicrophoneName);

        var empty = Normalized(s =>
        {
            s.Video.MicrophoneId = "  ";
            s.Video.MicrophoneName = "Mikro";
        }).Video;
        Assert.Null(empty.MicrophoneId);
        Assert.Null(empty.MicrophoneName);
    }

    [Fact]
    public void Microphone_ValidId_IsKeptExactly_NameIsTrimmedAndTruncated()
    {
        var id = new string('m', SettingsLimits.MicrophoneIdMaxLength);
        var v = Normalized(s =>
        {
            s.Video.MicrophoneId = id;
            s.Video.MicrophoneName = "  " + new string('n', SettingsLimits.MicrophoneNameMaxLength + 50);
        }).Video;

        Assert.Equal(id, v.MicrophoneId);
        Assert.Equal(SettingsLimits.MicrophoneNameMaxLength, v.MicrophoneName!.Length);

        var blankName = Normalized(s =>
        {
            s.Video.MicrophoneId = "mic-1";
            s.Video.MicrophoneName = "   ";
        }).Video;
        Assert.Equal("mic-1", blankName.MicrophoneId);
        Assert.Null(blankName.MicrophoneName);
    }

    [Fact]
    public void VideoFolder_BlankBecomesNull_RealPathIsKept()
    {
        Assert.Null(Normalized(s => s.Video.Folder = "").Video.Folder);
        Assert.Null(Normalized(s => s.Video.Folder = "   ").Video.Folder);
        Assert.Equal("D:\\Clips", Normalized(s => s.Video.Folder = "D:\\Clips").Video.Folder);
    }

    [Fact]
    public void VideoHint_UnknownStyleBecomesPill_TextIsCleaned()
    {
        var g = Normalized(s =>
        {
            s.GuestScreen.VideoHintStyle = (HintStyle)77;
            s.GuestScreen.VideoHintText = "  Video\r\nmit {Videotaste_1}  ";
        }).GuestScreen;

        Assert.Equal(HintStyle.Pill, g.VideoHintStyle);
        Assert.Equal("Video  mit {Videotaste_1}", g.VideoHintText);

        Assert.Null(Normalized(s => s.GuestScreen.VideoHintText = "   ").GuestScreen.VideoHintText);
        Assert.Equal(SettingsLimits.HintTextMaxLength,
            Normalized(s => s.GuestScreen.VideoHintText = new string('v', 500)).GuestScreen.VideoHintText!.Length);
        Assert.Equal(HintStyle.Banner, Normalized(s => s.GuestScreen.VideoHintStyle = HintStyle.Banner).GuestScreen.VideoHintStyle);
    }

    [Fact]
    public void VideoHintStyle_UnknownNameInJson_BecomesPill()
    {
        var store = StoreWithFile("{ \"Version\": 2, \"GuestScreen\": { \"VideoHintStyle\": \"Hologramm\" } }");

        Assert.False(store.LoadFailed);
        Assert.Equal(HintStyle.Pill, store.Current.GuestScreen.VideoHintStyle);
    }

    // ---- Migration

    [Fact]
    public void Migration_Version1WithLegacyPrefix_BecomesFotoboxPlus_AndVersion2()
    {
        var store = StoreWithFile("{ \"Version\": 1, \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        Assert.False(store.LoadFailed);
        Assert.Equal("Fotobox+", store.Current.Storage.FilePrefix);
        Assert.Equal(2, store.Current.Version);
    }

    [Fact]
    public void Migration_Version1WithCustomPrefix_KeepsPrefix()
    {
        var store = StoreWithFile("{ \"Version\": 1, \"Storage\": { \"FilePrefix\": \"Hochzeit\" } }");

        Assert.Equal("Hochzeit", store.Current.Storage.FilePrefix);
        Assert.Equal(2, store.Current.Version);
    }

    [Theory]
    [InlineData("fotobox")]
    [InlineData("Fotobox_")]
    [InlineData("Fotobox2")]
    public void Migration_OnlyTheExactLegacyPrefixIsReplaced(string prefix)
    {
        var store = StoreWithFile("{ \"Version\": 1, \"Storage\": { \"FilePrefix\": \"" + prefix + "\" } }");

        Assert.Equal(prefix, store.Current.Storage.FilePrefix);
    }

    [Fact]
    public void Migration_Version2WithPrefixFotobox_StaysFotobox()
    {
        var store = StoreWithFile("{ \"Version\": 2, \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        Assert.Equal("Fotobox", store.Current.Storage.FilePrefix);
        Assert.Equal(2, store.Current.Version);
    }

    [Fact]
    public void Migration_NewerVersion_IsNeverLowered_AndPrefixStays()
    {
        var store = StoreWithFile("{ \"Version\": 5, \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        Assert.Equal(5, store.Current.Version);
        Assert.Equal("Fotobox", store.Current.Storage.FilePrefix);
    }

    [Fact]
    public void Migration_VersionMissingInFile_IsTreatedAsCurrent_NoPrefixChange()
    {
        // Die App schreibt Version immer; fehlt das Feld, gilt der Standard der aktuellen App (2).
        var store = StoreWithFile("{ \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        Assert.Equal(AppSettings.CurrentVersion, store.Current.Version);
        Assert.Equal("Fotobox", store.Current.Storage.FilePrefix);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Migration_ZeroOrNegativeVersion_IsMigrated(int version)
    {
        var s = new AppSettings { Version = version };
        s.Storage.FilePrefix = "Fotobox";

        SettingsNormalizer.Normalize(s);

        Assert.Equal(2, s.Version);
        Assert.Equal("Fotobox+", s.Storage.FilePrefix);
    }

    [Fact]
    public void Migration_IsIdempotent_AndSavedFileCarriesNewVersionAndPrefix()
    {
        var store = StoreWithFile("{ \"Version\": 1, \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        store.Save(store.Current);
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(2, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("Fotobox+", doc.RootElement.GetProperty("Storage").GetProperty("FilePrefix").GetString());

        var reloaded = new JsonSettingsStore(_dir);
        Assert.Equal("Fotobox+", reloaded.Current.Storage.FilePrefix);
        Assert.Equal(2, reloaded.Current.Version);
    }

    [Fact]
    public void Migration_OldFileWithoutVideoSection_GetsVideoDefaults()
    {
        var store = StoreWithFile("{ \"Version\": 1 }");

        Assert.Equal(300, store.Current.Video.MaxDurationSeconds);
        Assert.Equal(["Enter"], store.Current.Input.VideoStartKeys);
        Assert.Equal(["Enter"], store.Current.Input.VideoStopKeys);
    }

    [Fact]
    public void Migration_OldFileWithEnterAsTrigger_LeavesVideoKeysEmpty()
    {
        var store = StoreWithFile("{ \"Version\": 1, \"Input\": { \"TriggerKeys\": [\"Enter\"] } }");

        Assert.Equal(["Enter"], store.Current.Input.TriggerKeys);
        Assert.Empty(store.Current.Input.VideoStartKeys);
        Assert.Empty(store.Current.Input.VideoStopKeys);
    }

    [Fact]
    public void FreshStore_UsesFotoboxPlusAndCurrentVersion()
    {
        var store = new JsonSettingsStore(_dir);

        Assert.Equal(AppSettings.CurrentVersion, store.Current.Version);
        Assert.Equal("Fotobox+", store.Current.Storage.FilePrefix);
    }

    // ---- Tasten

    private static InputSettings Keys(
        string[]? admin = null, string[]? trigger = null, string[]? cancel = null, string[]? start = null, string[]? stop = null) => new()
    {
        AdminKeys = [.. admin ?? ["Ctrl+Alt+A"]],
        TriggerKeys = [.. trigger ?? ["Space"]],
        CancelCountdownKeys = [.. cancel ?? ["Escape"]],
        VideoStartKeys = [.. start ?? []],
        VideoStopKeys = [.. stop ?? []],
    };

    [Fact]
    public void VideoKeys_Defaults_SurviveNormalization()
    {
        var input = new InputSettings();

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Enter"], input.VideoStartKeys);
        Assert.Equal(["Enter"], input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_KeyOfAdminTriggerOrCancel_IsRemovedFromVideoLists()
    {
        var input = Keys(
            start: ["Ctrl+Alt+A", "Space", "Escape", "F5"],
            stop: ["Escape", "Space", "Ctrl+Alt+A", "F6"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["F5"], input.VideoStartKeys);
        Assert.Equal(["F6"], input.VideoStopKeys);
        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);
        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Escape"], input.CancelCountdownKeys);
    }

    [Fact]
    public void VideoKeys_SameKeyInStartAndStop_IsAllowed()
    {
        var input = Keys(start: ["Enter", "F5"], stop: ["Enter", "F5"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Enter", "F5"], input.VideoStartKeys);
        Assert.Equal(["Enter", "F5"], input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_DuplicatesInOneList_AreRemoved_OrderKept()
    {
        var input = Keys(start: ["F6", "Enter", "f6", "Enter", "F7"], stop: ["F8", "F8"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["F6", "Enter", "F7"], input.VideoStartKeys);
        Assert.Equal(["F8"], input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_InvalidAndSystemCombinations_AreDroppedIndividually()
    {
        var input = Keys(start: ["Alt+F4", "Quatsch", "", "Alt+Tab", "Ctrl+Alt+Delete", "F5"], stop: ["Win", "LWin", "Enter"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["F5"], input.VideoStartKeys);
        Assert.Equal(["Enter"], input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_AreCappedAtKeysPerActionMax()
    {
        string[] many = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];
        var input = Keys(start: many, stop: many);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(SettingsLimits.KeysPerActionMax, input.VideoStartKeys.Count);
        Assert.Equal(SettingsLimits.KeysPerActionMax, input.VideoStopKeys.Count);
        Assert.Equal("F1", input.VideoStartKeys[0]);
    }

    [Fact]
    public void VideoKeys_RejectedConflictsDoNotUseUpTheCap()
    {
        string[] start = ["Space", "Escape", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8"];
        var input = Keys(start: start);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(SettingsLimits.KeysPerActionMax, input.VideoStartKeys.Count);
        Assert.DoesNotContain("Space", input.VideoStartKeys);
        Assert.Equal("F1", input.VideoStartKeys[0]);
    }

    [Fact]
    public void VideoKeys_MayBeEmpty_WithoutFallback()
    {
        var input = Keys(start: [], stop: []);
        input.VideoStartKeys = null!;

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Empty(input.VideoStartKeys);
        Assert.Empty(input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_TriggerFallbackSpace_IsRemovedFromVideoLists()
    {
        // Auslösen leer → Ersatz Leertaste; die Leertaste darf deshalb nicht in den Video-Listen bleiben.
        var input = Keys(trigger: [], start: ["Space", "Enter"], stop: ["Space"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Enter"], input.VideoStartKeys);
        Assert.Empty(input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_AdminFallbackDefault_IsRemovedFromVideoLists()
    {
        var input = Keys(admin: [], start: ["Ctrl+Alt+A", "Enter"], stop: ["Ctrl+Alt+A"]);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);
        Assert.Equal(["Enter"], input.VideoStartKeys);
        Assert.Empty(input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_Normalizing_IsIdempotent()
    {
        var input = Keys(admin: ["Space"], trigger: [], cancel: ["Ctrl+Alt+A"], start: ["Space", "Ctrl+Alt+A", "Enter"], stop: ["Enter", "Space"]);

        SettingsNormalizer.NormalizeKeys(input);
        var first = (input.AdminKeys.ToArray(), input.TriggerKeys.ToArray(), input.CancelCountdownKeys.ToArray(),
            input.VideoStartKeys.ToArray(), input.VideoStopKeys.ToArray());
        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(first.Item1, input.AdminKeys);
        Assert.Equal(first.Item2, input.TriggerKeys);
        Assert.Equal(first.Item3, input.CancelCountdownKeys);
        Assert.Equal(first.Item4, input.VideoStartKeys);
        Assert.Equal(first.Item5, input.VideoStopKeys);
        Assert.Equal(["Enter"], input.VideoStartKeys);
        Assert.Equal(["Enter"], input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_AreNormalizedByNormalizeAndSurviveSave()
    {
        var store = new JsonSettingsStore(_dir);
        var s = store.Current.Clone();
        s.Input.VideoStartKeys = ["F9", "Space"];
        s.Input.VideoStopKeys = ["F9", "F10"];
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir);
        Assert.Equal(["F9"], reloaded.Current.Input.VideoStartKeys);
        Assert.Equal(["F9", "F10"], reloaded.Current.Input.VideoStopKeys);
    }
}
