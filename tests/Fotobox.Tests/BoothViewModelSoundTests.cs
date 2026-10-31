using System.Diagnostics;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

// Countdown-Piepen und Kamera-Sound in der Gastansicht (Regeln in BoothSoundCues; hier der Verlauf mit echtem Controller und Fake-Spieler).
public partial class BoothViewModelTests
{
    private sealed class SoundRig
    {
        public Harness H { get; } = new();
        public FakeBoothSoundPlayer Sound { get; } = new();
        public BoothViewModel ViewModel { get; }

        public SoundRig(Action<FlowSettings>? configure = null)
        {
            var settings = H.Settings.Current;
            settings.Flow.CountdownSeconds = 3;
            settings.Flow.PreviewSeconds = 1;
            settings.Flow.PauseBetweenShotsSeconds = 0;
            settings.Flow.PrintingEnabled = false;
            settings.Video.Enabled = true;
            settings.Video.CountdownSeconds = 3;
            configure?.Invoke(settings.Flow);
            H.Camera.Photo = CreateBitmap();
            H.Composer.Result = CreateBitmap();
            H.Storage.Works = true;
            ViewModel = new BoothViewModel(H.Controller, H.Camera, H.Settings, null, null, null, null, Sound);
        }

        public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });

        /// <summary>Stellt die Zeit sekundenweise vor, bis der Durchgang vorbei ist (der Ablauf läuft teils auf dem Threadpool).</summary>
        public void RunToIdle()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                Pump();
                if (H.Controller.State == BoothState.Idle)
                    return;
                H.Time.Advance(TimeSpan.FromSeconds(1));
                Thread.Sleep(5);
            }
            Pump();
            Assert.Equal(BoothState.Idle, H.Controller.State);
        }

        public void CancelAndWait()
        {
            Assert.True(H.Controller.TryCancel());
            Assert.True(SpinWait.SpinUntil(() => H.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10)));
            Pump();
        }
    }

    private const BoothSoundCue Soft = BoothSoundCue.BeepSoft;
    private const BoothSoundCue SoftFinal = BoothSoundCue.BeepSoftFinal;
    private const BoothSoundCue Slr = BoothSoundCue.ShutterSlr;

    [Fact]
    public void Sound_PhotoCountdown_BeepsEachDigit_HigherOnOne_ThenShutterAtTheFlash()
    {
        var rig = new SoundRig();

        Assert.True(rig.ViewModel.TryTrigger());
        Assert.Equal([Soft], rig.Sound.PlayedCues);          // 3
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([Soft, Soft], rig.Sound.PlayedCues);    // 2
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([Soft, Soft, SoftFinal], rig.Sound.PlayedCues); // 1: der letzte, höhere Piep

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));          // Aufnahme: Blitz = Kamera-Sound
        Assert.Equal([Soft, Soft, SoftFinal, Slr], rig.Sound.PlayedCues);
        Assert.All(rig.Sound.Played, p => Assert.Equal(70, p.Volume));

        rig.RunToIdle();
        Assert.Equal([Soft, Soft, SoftFinal, Slr], rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_DuplicateChangeNotifications_BeepOnlyOncePerValue()
    {
        var rig = new SoundRig();
        Assert.True(rig.ViewModel.TryTrigger());

        rig.H.Settings.RaiseSettingsChanged();
        rig.H.Settings.RaiseSettingsChanged();

        Assert.Equal([Soft], rig.Sound.PlayedCues);
        rig.CancelAndWait();
    }

    [Fact]
    public void Sound_StripWithSeveralPhotos_BeepsBeforeEveryPhoto()
    {
        var rig = new SoundRig(flow => flow.CountdownSeconds = 2);
        rig.H.Composer.CountFor = _ => 2;

        Assert.True(rig.ViewModel.TryTrigger());
        rig.RunToIdle();

        Assert.Equal([Soft, SoftFinal, Slr, Soft, SoftFinal, Slr], rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_VideoCountdown_Beeps_ButNoShutter()
    {
        var rig = new SoundRig();

        Assert.True(rig.ViewModel.TryStartVideo());
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(BoothState.VideoCountdown, rig.H.Controller.State);
        Assert.Equal([Soft, Soft, SoftFinal], rig.Sound.PlayedCues);

        rig.CancelAndWait();
        Assert.DoesNotContain(rig.Sound.PlayedCues, c => c == Slr);
    }

    [Fact]
    public void Sound_VideoStart_HasNoShutterEvenWhenRecordingBegins()
    {
        var rig = new SoundRig(_ => { });
        rig.H.Settings.Current.Video.CountdownSeconds = 1;
        rig.H.Settings.Current.Video.MinDurationSeconds = 0;

        Assert.True(rig.ViewModel.TryStartVideo());
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => rig.H.Controller.State == BoothState.Recording, TimeSpan.FromSeconds(10)));
        SoundRig.Pump();

        Assert.Equal([SoftFinal], rig.Sound.PlayedCues);

        Assert.True(rig.ViewModel.TryStopVideo());
        rig.RunToIdle();
        Assert.Equal([SoftFinal], rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_Peace_BeepsAndShutter()
    {
        var rig = new SoundRig();

        Assert.True(rig.H.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Peace, CountdownSeconds = 2 }));
        rig.RunToIdle();

        Assert.Equal([Soft, SoftFinal, Slr], rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_SelfDestruct_IsSilent_NeitherBeepNorShutter()
    {
        var rig = new SoundRig();

        Assert.True(rig.H.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.SelfDestruct, CountdownSeconds = 3 }));
        rig.RunToIdle();

        Assert.Empty(rig.Sound.Played);
    }

    [Fact]
    public void Sound_Kisscam_NoBeep_ButShutter()
    {
        var rig = new SoundRig();

        Assert.True(rig.H.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = 3 }));
        rig.RunToIdle();

        Assert.Equal([Slr], rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_SwitchedOff_IsSilent()
    {
        var rig = new SoundRig(flow =>
        {
            flow.CountdownBeepEnabled = false;
            flow.ShutterSoundEnabled = false;
        });

        Assert.True(rig.ViewModel.TryTrigger());
        rig.RunToIdle();

        Assert.Empty(rig.Sound.Played);
        Assert.Empty(rig.Sound.Prepared);
    }

    [Fact]
    public void Sound_OnlyBeepSwitchedOff_StillPlaysShutter_AndViceVersa()
    {
        var beepOff = new SoundRig(flow => flow.CountdownBeepEnabled = false);
        Assert.True(beepOff.ViewModel.TryTrigger());
        beepOff.RunToIdle();
        Assert.Equal([Slr], beepOff.Sound.PlayedCues);

        var shutterOff = new SoundRig(flow => flow.ShutterSoundEnabled = false);
        Assert.True(shutterOff.ViewModel.TryTrigger());
        shutterOff.RunToIdle();
        Assert.Equal([Soft, Soft, SoftFinal], shutterOff.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_UsesChosenVariantsAndVolumes()
    {
        var rig = new SoundRig(flow =>
        {
            flow.CountdownSeconds = 2;
            flow.CountdownBeepSound = CountdownBeepSound.WoodBlock;
            flow.CountdownBeepVolumePercent = 40;
            flow.ShutterSound = ShutterSound.Retro;
            flow.ShutterVolumePercent = 95;
        });

        Assert.True(rig.ViewModel.TryTrigger());
        rig.RunToIdle();

        Assert.Equal(
            [(BoothSoundCue.BeepWoodBlock, 40), (BoothSoundCue.BeepWoodBlockFinal, 40), (BoothSoundCue.ShutterRetro, 95)],
            rig.Sound.Played);
    }

    [Fact]
    public void Sound_ChangedSettingsApplyToTheNextCountdown()
    {
        var rig = new SoundRig();
        rig.H.Settings.Current.Flow.CountdownBeepSound = CountdownBeepSound.Bright;
        rig.H.Settings.Current.Flow.ShutterSound = ShutterSound.Compact;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.True(rig.ViewModel.TryTrigger());
        rig.RunToIdle();

        Assert.Equal([BoothSoundCue.BeepBright, BoothSoundCue.BeepBright, BoothSoundCue.BeepBrightFinal, BoothSoundCue.ShutterCompact],
            rig.Sound.PlayedCues);
    }

    [Fact]
    public void Sound_BuffersArePreparedBeforeTheFirstCountdown_AndAfterSettingsChange()
    {
        var rig = new SoundRig();

        // Beim Anlegen: Piep, letzter Piep und Auslöser der Einstellungen.
        Assert.Equal([Soft, SoftFinal, Slr], rig.Sound.Prepared.Select(p => p.Cue).ToArray());
        Assert.Empty(rig.Sound.Played);

        // Ohne Änderung der Einstellungen wird nichts erneut vorbereitet.
        Assert.True(rig.ViewModel.TryTrigger());
        Assert.Equal(3, rig.Sound.Prepared.Count);

        rig.CancelAndWait();
        rig.H.Settings.Current.Flow.ShutterSound = ShutterSound.Retro;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothSoundCue.ShutterRetro, rig.Sound.Prepared[^1].Cue);
    }

    [Fact]
    public void Sound_FailingPlayer_NeverBreaksTheSession()
    {
        var rig = new SoundRig();
        rig.Sound.Throw = true;

        Assert.True(rig.ViewModel.TryTrigger());
        rig.RunToIdle();

        Assert.NotNull(rig.H.Storage.LastStorage);
    }
}
