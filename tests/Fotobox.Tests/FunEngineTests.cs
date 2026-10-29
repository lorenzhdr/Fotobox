using Fotobox.Core;
using Fotobox.Core.Fun;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ablauf der Zielerfassung: Erstauslösung, Zuwachs, Text, Pause, Episoden, Labels, Filter, Simulation.</summary>
public sealed class FunEngineTests
{
    /// <summary>Engine mit Fake-Zeit; Aufrufe in 100-ms-Schritten wie ein Erkennungsdienst mit Tick.</summary>
    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        public FunEngine Engine { get; }
        public List<FunSoundCue> Cues { get; } = [];
        public int Changes { get; private set; }
        public FunSettings Settings { get; }

        public Rig(Action<FunSettings>? configure = null, bool mirror = false)
        {
            Settings = new FunSettings
            {
                Enabled = true,
                SoundEnabled = true,
                Scope = TargetScope.FullBody,
                DwellMilliseconds = 0,
                MinSizePercent = 5,
                TextSeconds = 6,
                CooldownSeconds = 20,
            };
            configure?.Invoke(Settings);
            Engine = new FunEngine(Time);
            Engine.Configure(Settings, mirror);
            Engine.SoundCue += (_, cue) => Cues.Add(cue);
            Engine.Changed += (_, _) => Changes++;
        }

        public FunSnapshot Snap => Engine.Snapshot;

        /// <summary>Läuft <paramref name="seconds"/> in 100-ms-Schritten; pro Schritt kommt eine Analyse mit <paramref name="people"/> plus Tick.</summary>
        public void Run(double seconds, params Detection[] people)
        {
            var steps = (int)Math.Round(seconds * 10);
            for (var i = 0; i < steps; i++)
            {
                Time.Advance(TimeSpan.FromMilliseconds(100));
                Engine.OnDetections(people);
                Engine.Tick();
            }
        }

        /// <summary>Bis zur Cooldown-Phase einer einzelnen Person.</summary>
        public void RunToCooldown(params Detection[] people)
        {
            Run(0.1, people);
            Run(1.2, people);
            Run(Settings.TextSeconds, people);
        }
    }

    private static Detection Person(double x, double w = 0.12, double h = 0.7, double confidence = 0.9, double y = 0.1) =>
        new(new NormRect(x, y, w, h), confidence, DetectionKind.Body);

    private static readonly Detection P1 = Person(0.1);
    private static readonly Detection P2 = Person(0.4);
    private static readonly Detection P3 = Person(0.7);

    // ---- Erstauslösung

    [Fact]
    public void FirstAppearance_LocksOn_ThenShowsText_ThenCooldown_ThenWatching()
    {
        var rig = new Rig();

        rig.Run(0.1, P1);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(1, rig.Snap.Count);
        var target = Assert.Single(rig.Snap.Targets);
        Assert.True(target.IsLockingOn);
        Assert.Equal("TARGET 1", target.Label);
        Assert.False(rig.Snap.ShowText);
        Assert.Equal([FunSoundCue.Acquire], rig.Cues);

        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.True(rig.Snap.ShowText);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
        Assert.False(rig.Snap.Targets[0].IsLockingOn);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);

        rig.Run(5.9, P1);
        Assert.True(rig.Snap.ShowText);
        rig.Run(0.1, P1);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        Assert.False(rig.Snap.ShowText);
        Assert.Equal("", rig.Snap.Text);
        Assert.Single(rig.Snap.Targets); // Box bleibt sichtbar

        rig.Run(20, P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Single(rig.Snap.Targets);
        Assert.Equal(3, rig.Cues.Count);
    }

    [Fact]
    public void Nobody_StaysEmpty_AndRaisesNothing()
    {
        var rig = new Rig();
        rig.Run(5);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Empty(rig.Snap.Targets);
        Assert.Equal(0, rig.Snap.Count);
        Assert.Equal(0, rig.Changes);
        Assert.Empty(rig.Cues);
    }

    [Fact]
    public void Dwell_DelaysTargetUntilConfirmed()
    {
        var rig = new Rig(s => s.DwellMilliseconds = 800);

        rig.Run(0.8, P1);
        Assert.Empty(rig.Snap.Targets);
        Assert.Empty(rig.Cues);

        rig.Run(0.1, P1); // 0,8 s nach dem ersten Treffer
        Assert.Equal(1, rig.Snap.Count);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire], rig.Cues);
    }

    [Fact]
    public void SoundDisabled_NoCues_ButFlowRuns()
    {
        var rig = new Rig(s => s.SoundEnabled = false);
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Empty(rig.Cues);
    }

    // ---- Pause und Zuwachs

    [Fact]
    public void Cooldown_DelaysFirstAppearanceOfNewGroupUntilItEnds()
    {
        var rig = new Rig();
        rig.RunToCooldown(P1);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        Assert.Equal(3, rig.Cues.Count);

        rig.Run(3); // niemand mehr: Episode endet
        Assert.Equal(0, rig.Snap.Count);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);

        rig.Run(0.1, P2); // neue Gruppe im Cooldown
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        Assert.Equal(1, rig.Snap.Count);
        Assert.False(rig.Snap.Targets[0].IsLockingOn);
        Assert.Equal(3, rig.Cues.Count);

        rig.Run(17.5, P2); // nach dem Cooldown (20 s) rastet die noch anwesende Gruppe nachträglich ein
        Assert.Contains(rig.Snap.Phase, new[] { FunPhase.LockingOn, FunPhase.ShowingText });
        Assert.Equal(FunSoundCue.Acquire, rig.Cues[3]);
    }

    [Fact]
    public void Increase_DuringCooldown_LocksAllPresentTargetsTogether()
    {
        var rig = new Rig();
        rig.RunToCooldown(P1);

        rig.Run(0.1, P1, P2);

        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(2, rig.Snap.Count);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());
        Assert.Equal(FunSoundCue.Acquire, rig.Cues[^1]);
        Assert.Equal(4, rig.Cues.Count);

        rig.Run(1.2, P1, P2);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultPluralText, rig.Snap.Text);
    }

    [Fact]
    public void Increase_DuringWatching_AfterCooldown_Locks()
    {
        var rig = new Rig(s => s.CooldownSeconds = 0);
        rig.RunToCooldown(P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);

        rig.Run(0.1, P1, P2);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(FunSoundCue.Acquire, rig.Cues[^1]);
    }

    [Fact]
    public void Increase_DuringText_KeepsTextFixed_AndLocksAllPresentTargetsAfterwards()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);   // Text läuft ab t = 1,3 bis 7,3 s
        rig.Run(2, P1);     // t = 3,3 s
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        var cuesBefore = rig.Cues.Count;

        rig.Run(0.1, P1, P2); // t = 3,4 s: P2 bekommt nur einen Rahmen, der Text bleibt
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(2, rig.Snap.Count);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
        Assert.All(rig.Snap.Targets, t => Assert.False(t.IsLockingOn));
        Assert.Equal(cuesBefore, rig.Cues.Count); // weder Ton noch Einrast-Animation

        rig.Run(3.8, P1, P2); // t = 7,2 s: der Text wurde nicht verlängert oder umgestellt
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
        Assert.Equal(cuesBefore, rig.Cues.Count);

        rig.Run(0.1, P1, P2); // t = 7,3 s: Textende, sofort gemeinsames Einrasten für P1 und P2
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.False(rig.Snap.ShowText);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());
        Assert.Equal([FunSoundCue.Acquire], rig.Cues.Skip(cuesBefore));

        rig.Run(1.2, P1, P2); // t = 8,5 s: neue Meldung mit aktueller Anzahl
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultPluralText, rig.Snap.Text);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues.Skip(cuesBefore));

        rig.Run(6, P1, P2);   // Text zu Ende, niemand vorgemerkt: Pause
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
    }

    [Fact]
    public void NewPersonLeavesBeforeTextEnds_GoesToCooldownInsteadOfNewMessage()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);   // Text bis 7,3 s
        rig.Run(2.0, P1);   // t = 3,3 s
        rig.Run(0.5, P1, P2);
        rig.Run(3.5, P1);   // t = 7,3 s; P2 ist längst weg

        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        Assert.Equal(1, rig.Cues.Count(c => c == FunSoundCue.Acquire));
        Assert.Equal(3, rig.Cues.Count);
    }

    [Fact]
    public void Increase_DuringLockOn_RestartsTheAnimationForAll_WithOneAcquireCue()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);      // t = 0,1; Einrasten bis 1,3
        rig.Run(0.9, P1);      // t = 1,0
        rig.Run(0.1, P1, P2);  // t = 1,1: P2 kommt, die Animation startet für beide neu (bis 2,3)
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(2, rig.Snap.Targets.Count);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());

        rig.Run(0.5, P1, P2);  // t = 1,6: das alte Ende (1,3) ist vorbei, beide rasten weiter ein
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));

        rig.Run(0.7, P1, P2);  // t = 2,3
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultPluralText, rig.Snap.Text);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void Increase_DuringLockOn_TwoNewInTheSameFrame_AreOneRestartAndOneCue()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(0.4, P1);                // t = 0,5
        rig.Run(0.1, P1, P2, P3);        // t = 0,6: zwei Neue im selben Durchgang
        Assert.Equal(3, rig.Snap.Targets.Count);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());
        Assert.Equal(2, rig.Cues.Count(c => c == FunSoundCue.Acquire));

        rig.Run(1.2, P1, P2, P3);        // t = 1,8 (Ende des einen Neustarts: 0,6 + 1,2)
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void Increase_DuringLockOn_AfterTheRestartLimit_LateComersLockOnIndividually()
    {
        var rig = new Rig(s => s.MaxLockOnRestarts = 2);
        var p4 = Person(0.85);
        rig.Run(0.1, P1);                // t = 0,1; Einrasten bis 1,3
        rig.Run(0.1, P1, P2);            // t = 0,2: Neustart 1 (bis 1,4)
        rig.Run(0.1, P1, P2, P3);        // t = 0,3: Neustart 2 (bis 1,5)
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());
        var restartedAt = rig.Snap.Targets[0].LockStartedAt;

        rig.Run(0.5, P1, P2, P3);        // t = 0,8
        rig.Run(0.1, P1, P2, P3, p4);    // t = 0,9: Grenze erreicht, P4 rastet einzeln ein (bis 2,1)
        var p4Target = rig.Snap.Targets.Single(t => t.Box.X > 0.8);
        Assert.True(p4Target.IsLockingOn);
        Assert.True(p4Target.LockStartedAt > restartedAt);
        Assert.All(rig.Snap.Targets.Where(t => t.Box.X < 0.8), t => Assert.Equal(restartedAt, t.LockStartedAt));
        Assert.Equal(4, rig.Cues.Count(c => c == FunSoundCue.Acquire));

        rig.Run(0.7, P1, P2, P3, p4);    // t = 1,6: die Gruppe ist fertig, P4 rastet noch ein
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(1, rig.Snap.Targets.Count(t => t.IsLockingOn));

        rig.Run(0.5, P1, P2, P3, p4);    // t = 2,1
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultPluralText, rig.Snap.Text);
    }

    [Fact]
    public void Increase_DuringLockOn_WithZeroRestarts_KeepsTheOldBehavior()
    {
        var rig = new Rig(s => s.MaxLockOnRestarts = 0);
        rig.Run(0.1, P1);      // t = 0,1; Einrasten bis 1,3
        rig.Run(0.9, P1);      // t = 1,0
        rig.Run(0.1, P1, P2);  // t = 1,1: P2 rastet einzeln bis 2,3 ein
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);

        rig.Run(0.5, P1, P2);  // t = 1,6: P1 fertig, P2 noch nicht
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.False(rig.Snap.Targets.Single(t => t.Box.X < 0.2).IsLockingOn);
        Assert.True(rig.Snap.Targets.Single(t => t.Box.X > 0.3).IsLockingOn);

        rig.Run(0.7, P1, P2);  // t = 2,3
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void RestartCounter_IsResetForEachLockOnRound()
    {
        var rig = new Rig(s => { s.MaxLockOnRestarts = 1; s.CooldownSeconds = 0; });
        rig.Run(0.1, P1);
        rig.Run(0.1, P1, P2);            // Neustart 1 von 1
        rig.Run(0.1, P1, P2, P3);        // Grenze erreicht: P3 rastet einzeln ein
        Assert.Equal(2, rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct().Count());

        rig.Run(1.5, P1, P2, P3);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        rig.Run(6.1, P1, P2, P3);        // Textende, Pause 0
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);

        rig.Run(4);                      // alle weg, die Episode endet
        rig.Run(0.1, P1);                // neue Runde
        rig.Run(0.1, P1, P2);            // der erste Neustart der neuen Runde ist wieder möglich
        Assert.Equal(2, rig.Snap.Targets.Count);
        Assert.Single(rig.Snap.Targets.Select(t => t.LockStartedAt).Distinct());
    }

    [Fact]
    public void Leaving_DuringLockOn_DoesNotRestart_AndTheTextCountsTheRemainingPerson()
    {
        var rig = new Rig();
        rig.Run(0.1, P1, P2);  // t = 0,1: beide rasten ein bis 1,3
        var cues = rig.Cues.Count;
        var started = rig.Snap.Targets[0].LockStartedAt;

        rig.Run(0.9, P1);      // P2 geht (nach 0,7 s ohne Treffer ist das Ziel weg)
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(started, Assert.Single(rig.Snap.Targets).LockStartedAt);
        Assert.Equal(cues, rig.Cues.Count);

        rig.Run(0.3, P1);      // t = 1,3: Einrastende, die Meldung gilt dem Rest
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
    }

    [Fact]
    public void Leaving_DuringLockOn_WithSwapTriggersOff_UnlockedNewcomerDoesNotCountForTheText()
    {
        var rig = new Rig(s => s.SwapTriggers = false);
        rig.Run(0.1, P1, P2);  // t = 0,1: beide rasten ein bis 1,3
        rig.Run(0.8, P1);      // t = 0,9: P2 ist weg
        rig.Run(0.1, P1, P3);  // t = 1,0: P3 kommt, die Anzahl erreicht die Höchstzahl nicht → kein Auslöser, P3 rastet nicht ein
        Assert.Equal(2, rig.Snap.Count);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.False(rig.Snap.Targets.Single(t => t.Box.X > 0.6).IsLockingOn);

        rig.Run(0.3, P1, P3);  // t = 1,3
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text); // nur P1 ist eingerastet
    }

    [Fact]
    public void Flicker_BetweenTwoAndThree_TriggersOnlyOnce()
    {
        var rig = new Rig();
        rig.Run(0.1, P1, P2);               // A (Acquire 1)
        rig.Run(1.2, P1, P2);               // Text
        rig.Run(0.1, P1, P2, P3);           // Zuwachs 2 → 3 während des Texts: vorgemerkt, noch kein Ton
        Assert.Equal(3, rig.Snap.Count);
        rig.Run(1.0, P1, P2);               // P3 weg (länger als 700 ms) → zurück auf 2
        Assert.Equal(2, rig.Snap.Count);
        rig.Run(0.3, P1, P2, P3);           // P3 wieder da: gleiche Person, kein neuer Zuwachs
        Assert.Equal(3, rig.Snap.Count);
        Assert.Equal(1, rig.Cues.Count(c => c == FunSoundCue.Acquire));

        rig.Run(5.0, P1, P2, P3);           // Textende (7,3 s): genau eine Meldung für P3
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal(2, rig.Cues.Count(c => c == FunSoundCue.Acquire));
    }

    [Fact]
    public void Flicker_DuringCooldown_DoesNotRetrigger()
    {
        var rig = new Rig();
        rig.RunToCooldown(P1, P2);
        var cues = rig.Cues.Count;

        rig.Run(1.5, P1);                   // P2 fehlt 1,5 s (länger als der Tracker-Timeout)
        Assert.Equal(1, rig.Snap.Count);
        rig.Run(0.5, P1, P2);               // P2 ist wieder da: gleiche Person
        Assert.Equal(2, rig.Snap.Count);
        Assert.Equal(cues, rig.Cues.Count);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
    }

    // ---- Personentausch

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Swap_NewPersonWithSameCount_TriggersOnlyWhenEnabled(bool swapTriggers)
    {
        var rig = new Rig(s => { s.CooldownSeconds = 0; s.TextSeconds = 2; s.SwapTriggers = swapTriggers; });
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        rig.Run(2.0, P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        var cuesBefore = rig.Cues.Count;

        rig.Run(1.0);          // P1 geht (Episode läuft weiter)
        rig.Run(0.1, P2);      // P2 kommt, die Anzahl bleibt 1

        Assert.Equal(swapTriggers, rig.Snap.Phase == FunPhase.LockingOn);
        Assert.Equal(swapTriggers ? cuesBefore + 1 : cuesBefore, rig.Cues.Count);
        if (swapTriggers)
            Assert.True(Assert.Single(rig.Snap.Targets).IsLockingOn);
    }

    [Fact]
    public void Swap_DuringText_IsPendingUntilTheTextEnds()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);   // Text bis 7,3 s
        rig.Run(2.0, P1);   // t = 3,3 s
        var cues = rig.Cues.Count;

        rig.Run(1.0, P2);   // P1 geht, P2 kommt: Anzahl bleibt 1, Text bleibt
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(cues, rig.Cues.Count);
        Assert.False(Assert.Single(rig.Snap.Targets).IsLockingOn);

        rig.Run(3.0, P2);   // t = 7,3 s
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.True(Assert.Single(rig.Snap.Targets).IsLockingOn);
        Assert.Equal(FunSoundCue.Acquire, rig.Cues[^1]);
    }

    // ---- Wiederholung

    [Fact]
    public void SameGroup_WithoutRepeat_StaysQuietFor90Seconds()
    {
        var rig = new Rig();
        rig.RunToCooldown(P1, P2);
        rig.Run(90, P1, P2);

        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal(3, rig.Cues.Count);
    }

    [Fact]
    public void Repeat_RelocksTheSameGroupAfterTheConfiguredTime_CountingFromEachTextEnd()
    {
        var rig = new Rig(s => { s.RepeatEnabled = true; s.RepeatSeconds = 60; });
        rig.Run(0.1, P1, P2);
        rig.Run(1.2, P1, P2);
        rig.Run(6, P1, P2);                     // Textende bei t = 7,3 s
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        var cues = rig.Cues.Count;

        rig.Run(59.9, P1, P2);                  // t = 67,2 s
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal(cues, rig.Cues.Count);

        rig.Run(0.1, P1, P2);                   // t = 67,3 s: 60 s nach dem Textende
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.All(rig.Snap.Targets, t => Assert.True(t.IsLockingOn));
        Assert.Equal(FunSoundCue.Acquire, rig.Cues[^1]);

        rig.Run(1.2, P1, P2);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(FunSettings.DefaultPluralText, rig.Snap.Text);

        rig.Run(6, P1, P2);                     // zweites Textende bei t = 74,5 s
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        rig.Run(59.9, P1, P2);                  // t = 134,4 s
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        rig.Run(0.1, P1, P2);                   // t = 134,5 s: wieder 60 s nach dem Textende
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
    }

    [Fact]
    public void Repeat_ShorterThanCooldown_WaitsForTheCooldown()
    {
        var rig = new Rig(s => { s.RepeatEnabled = true; s.RepeatSeconds = 10; s.CooldownSeconds = 30; });
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        rig.Run(6, P1);                         // Textende bei t = 7,3 s, Pause bis 37,3 s
        rig.Run(29.9, P1);                      // t = 37,2 s
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);

        rig.Run(0.1, P1);                       // t = 37,3 s: Pause vorbei, Wiederholung war längst fällig
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
    }

    [Fact]
    public void Repeat_DoesNotFire_AfterTheGroupLeftAndTheEpisodeEnded()
    {
        var rig = new Rig(s => { s.RepeatEnabled = true; s.RepeatSeconds = 10; s.CooldownSeconds = 0; });
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        rig.Run(6, P1);                         // Textende, Pause 0
        rig.Run(6);                             // niemand mehr, die Episode endet
        var cues = rig.Cues.Count;

        rig.Run(30);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal(cues, rig.Cues.Count);
    }

    // ---- Aussetzer der Erkennung

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void DetectionGap_DuringText_NeitherHidesTextNorTriggersAgain(double gapSeconds)
    {
        var rig = new Rig(s => s.DwellMilliseconds = 800);
        rig.Run(0.9, P1);   // bestätigt bei t = 0,9 s, Text von 2,1 bis 8,1 s
        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);

        rig.Run(gapSeconds);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.True(rig.Snap.ShowText);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
        Assert.Empty(rig.Snap.Targets);

        rig.Run(1.0, P1);   // dieselbe Person: keine neue Verweildauer, keine neue Auslösung
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Single(rig.Snap.Targets);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void DetectionGap_ReturnedPersonIsConfirmedAtOnce_WithoutNewDwell()
    {
        var rig = new Rig(s => { s.DwellMilliseconds = 800; s.CooldownSeconds = 0; s.TextSeconds = 2; });
        rig.Run(0.9, P1);
        rig.Run(1.2, P1);
        rig.Run(2.0, P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);

        rig.Run(1.5);
        rig.Run(0.1, P1);   // kein Warten auf die Verweildauer: sofort wieder bestätigt
        Assert.Single(rig.Snap.Targets);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
    }

    // ---- Verschwinden und Episode

    [Fact]
    public void NobodyLeft_DuringText_KeepsTextUntilTheEpisodeEnds_ThenStartsCooldown()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);   // Text 1,3 bis 7,3 s; P1 zuletzt bei t = 1,3 s gesehen
        Assert.True(rig.Snap.ShowText);

        rig.Run(0.6);       // Ziel wird noch 700 ms gehalten
        Assert.True(rig.Snap.ShowText);

        rig.Run(0.1);       // 700 ms ohne Treffer: kein Ziel mehr, der Text bleibt
        Assert.Equal(0, rig.Snap.Count);
        Assert.Empty(rig.Snap.Targets);
        Assert.True(rig.Snap.ShowText);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);

        rig.Run(2.2);       // t = 4,2 s: die Spur wird noch vorgemerkt
        Assert.True(rig.Snap.ShowText);

        rig.Run(0.1);       // t = 4,3 s: Episode zu Ende, jetzt beginnt die Pause
        Assert.False(rig.Snap.ShowText);
        Assert.Equal("", rig.Snap.Text);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);

        rig.Run(19.9);      // Pause ab dem Ende der Episode (20 s)
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        rig.Run(0.1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
    }

    [Fact]
    public void NobodyLeft_DuringLockOn_ReturnsToWatching_WithoutCooldownOrText()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);

        rig.Run(0.7);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal(0, rig.Snap.Count);
        Assert.False(rig.Snap.ShowText);

        rig.Run(5);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire], rig.Cues);
    }

    [Theory]
    [InlineData(1.5, false)]
    [InlineData(3.0, true)]
    public void Episode_EndsAfterTwoSecondsEmpty(double absentSeconds, bool triggersAgain)
    {
        var rig = new Rig(s => { s.CooldownSeconds = 0; s.TextSeconds = 2; });
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        rig.Run(2.0, P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        var cuesBefore = rig.Cues.Count;

        rig.Run(absentSeconds);
        rig.Run(0.1, P1);

        Assert.Equal(triggersAgain, rig.Snap.Phase == FunPhase.LockingOn);
        Assert.Equal(triggersAgain ? cuesBefore + 1 : cuesBefore, rig.Cues.Count);
    }

    // ---- Anzeige

    [Fact]
    public void Labels_AreNumberedLeftToRight()
    {
        var rig = new Rig();
        rig.Run(0.1, P3, P1, P2);

        Assert.Equal(["TARGET 1", "TARGET 2", "TARGET 3"], rig.Snap.Targets.Select(t => t.Label));
        Assert.Equal([P1.Box.X, P2.Box.X, P3.Box.X], rig.Snap.Targets.Select(t => t.Box.X));
    }

    [Fact]
    public void Labels_Mirrored_AreNumberedByScreenPosition()
    {
        var rig = new Rig(mirror: true);
        rig.Run(0.1, P1, P2, P3);

        // Gespiegelt steht die Person mit dem größten x links auf dem Bildschirm.
        Assert.Equal([P3.Box.X, P2.Box.X, P1.Box.X], rig.Snap.Targets.Select(t => t.Box.X));
        Assert.Equal(["TARGET 1", "TARGET 2", "TARGET 3"], rig.Snap.Targets.Select(t => t.Label));
    }

    [Fact]
    public void Labels_UpdateWhenMirrorChanges()
    {
        var rig = new Rig();
        rig.Run(0.1, P1, P3);
        Assert.Equal(P1.Box.X, rig.Snap.Targets[0].Box.X);

        rig.Engine.Configure(rig.Settings, mirror: true);
        Assert.Equal(P3.Box.X, rig.Snap.Targets[0].Box.X);
    }

    [Fact]
    public void Targets_OutsideVisibleRegion_DoNotCount()
    {
        var rig = new Rig();
        rig.Engine.SetVisibleRegion(new NormRect(0.125, 0, 0.75, 1));
        rig.Run(0.1, Person(0.0), Person(0.9, w: 0.1), P2);

        Assert.Equal(1, rig.Snap.Count);
        Assert.Equal(P2.Box.X, rig.Snap.Targets[0].Box.X);
    }

    [Fact]
    public void MaxTargets_KeepsTheLargest()
    {
        var rig = new Rig(s => s.MaxTargets = 2);
        var small = Person(0.1, w: 0.1, h: 0.5);
        var big = Person(0.4, w: 0.2, h: 0.8);
        var medium = Person(0.7, w: 0.12, h: 0.7);
        rig.Run(0.1, small, big, medium);

        Assert.Equal(2, rig.Snap.Count);
        Assert.DoesNotContain(rig.Snap.Targets, t => Math.Abs(t.Box.X - small.Box.X) < 1e-9);
    }

    [Fact]
    public void Filters_ConfidenceAndMinimumSize()
    {
        var rig = new Rig(s => { s.MinSizePercent = 50; s.SensitivityPercent = 50; });
        rig.Run(0.1, Person(0.1, h: 0.4), Person(0.4, confidence: 0.3), P3);

        Assert.Equal(1, rig.Snap.Count);
        Assert.Equal(P3.Box.X, rig.Snap.Targets[0].Box.X);
    }

    [Fact]
    public void FaceDetections_UseScopeGeometry()
    {
        var rig = new Rig(s => s.Scope = TargetScope.Head);
        rig.Run(0.1, new Detection(new NormRect(0.4, 0.3, 0.1, 0.2), 1, DetectionKind.Face));

        var box = Assert.Single(rig.Snap.Targets).Box;
        Assert.Equal(0.28, box.Height, 6); // 1,4 × Gesichtshöhe
    }

    // ---- Text live

    [Fact]
    public void Text_FollowsSettingsLive_ButStaysFixedForTheCountDuringDisplay()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);

        rig.Settings.SingularText = "Hallo {Anzahl}";
        rig.Engine.Configure(rig.Settings, mirror: false);
        Assert.Equal("Hallo 1", rig.Snap.Text);

        rig.Settings.PluralText = "Hallo {Anzahl}!";
        rig.Engine.Configure(rig.Settings, mirror: false);
        rig.Run(0.1, P1, P2);               // Zuwachs: Text bleibt bei „du“
        Assert.Equal("Hallo 1", rig.Snap.Text);
        Assert.Equal(2, rig.Snap.Count);

        rig.Run(1.0, P1);                   // P2 geht wieder: ebenfalls unverändert
        Assert.Equal("Hallo 1", rig.Snap.Text);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
    }

    [Fact]
    public void Changed_OnlyWhenTheSnapshotChanges()
    {
        var rig = new Rig();
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);

        var changes = rig.Changes;
        rig.Run(3, P1);
        Assert.Equal(changes, rig.Changes);

        rig.Engine.Tick();
        rig.Engine.Tick();
        Assert.Equal(changes, rig.Changes);
    }

    [Fact]
    public void ThrowingHandlers_DoNotBreakTheEngine()
    {
        var rig = new Rig();
        rig.Engine.Changed += (_, _) => throw new InvalidOperationException("Test");
        rig.Engine.SoundCue += (_, _) => throw new InvalidOperationException("Test");

        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
    }

    [Fact]
    public void Cooldown_Zero_GoesStraightToWatching()
    {
        var rig = new Rig(s => s.CooldownSeconds = 0);
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        rig.Run(6, P1);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
    }

    // ---- Reset und Simulation

    [Fact]
    public void Reset_ClearsEverything_AndAllowsFirstAppearanceAgain()
    {
        var rig = new Rig();
        rig.RunToCooldown(P1);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        var changes = rig.Changes;

        rig.Engine.Reset();

        Assert.Empty(rig.Snap.Targets);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal(0, rig.Snap.Count);
        Assert.False(rig.Snap.ShowText);
        Assert.Equal(changes + 1, rig.Changes);

        rig.Cues.Clear();
        rig.Run(0.1, P1);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire], rig.Cues);
    }

    [Fact]
    public void Reset_WhenAlreadyEmpty_RaisesNothing()
    {
        var rig = new Rig();
        rig.Engine.Reset();
        Assert.Equal(0, rig.Changes);
        Assert.Equal(FunSnapshot.Empty.Phase, rig.Snap.Phase);
    }

    [Fact]
    public void Simulate_RunsLikeFirstAppearance_AndIgnoresRealDetections()
    {
        var rig = new Rig();
        rig.Engine.Simulate();

        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        var target = Assert.Single(rig.Snap.Targets);
        Assert.True(target.IsLockingOn);
        Assert.Equal("TARGET 1", target.Label);
        Assert.Equal(new NormRect(0.38, 0.2, 0.24, 0.7), target.Box);
        Assert.Equal([FunSoundCue.Acquire], rig.Cues);

        rig.Run(1.2, P1, P2); // echte Erkennungen werden ignoriert
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.Equal(1, rig.Snap.Count);
        Assert.Equal(FunSettings.DefaultSingularText, rig.Snap.Text);

        rig.Run(5.9, P1, P2);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        rig.Run(0.1, P1, P2);
        Assert.Equal(FunPhase.Cooldown, rig.Snap.Phase);
        Assert.Empty(rig.Snap.Targets);
        Assert.Equal(0, rig.Snap.Count);

        rig.Run(20);
        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void Simulate_IgnoresDwellAndCooldown()
    {
        var rig = new Rig(s => s.DwellMilliseconds = 3000);
        rig.Run(2, P1); // bleibt wegen der langen Verweildauer ohne Ziel
        Assert.Equal(0, rig.Snap.Count);

        rig.Engine.Simulate();
        Assert.Equal(1, rig.Snap.Count);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);

        // Nach einem echten Durchlauf mit Cooldown startet die Simulation trotzdem sofort.
        var rig2 = new Rig();
        rig2.RunToCooldown(P1);
        Assert.Equal(FunPhase.Cooldown, rig2.Snap.Phase);
        rig2.Engine.Simulate();
        Assert.Equal(FunPhase.LockingOn, rig2.Snap.Phase);
        Assert.Equal(FunSoundCue.Acquire, rig2.Cues[^1]);
    }

    [Fact]
    public void Simulate_WithLateTick_RunsThroughAllPhases()
    {
        var rig = new Rig();
        rig.Engine.Simulate();

        rig.Time.Advance(TimeSpan.FromSeconds(100));
        rig.Engine.Tick();

        Assert.Equal(FunPhase.Watching, rig.Snap.Phase);
        Assert.Empty(rig.Snap.Targets);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void Simulate_FollowsScope()
    {
        var rig = new Rig(s => s.Scope = TargetScope.Head);
        rig.Engine.Simulate();
        var head = Assert.Single(rig.Snap.Targets).Box;

        Assert.True(head.Height < 0.2); // Kopf statt ganzer Körper
        Assert.Equal(0.5, head.CenterX, 6);
    }

    // ---- Einstellbare Einrast-Dauer

    [Theory]
    [InlineData(500)]
    [InlineData(1200)]
    [InlineData(5000)]
    public void LockOnDuration_IsConfigurable_TextComesAfterDwellPlusDuration(int milliseconds)
    {
        var rig = new Rig(s =>
        {
            s.DwellMilliseconds = 300;
            s.LockOnMilliseconds = milliseconds;
        });

        // Dwell: Die Person wird nach 300 ms bestätigt, das Einrasten beginnt dann.
        var confirmedAt = TimeSpan.Zero;
        while (rig.Snap.Phase != FunPhase.LockingOn)
        {
            rig.Run(0.1, P1);
            confirmedAt += TimeSpan.FromMilliseconds(100);
            Assert.True(confirmedAt < TimeSpan.FromSeconds(2), "Einrasten begann nicht.");
        }
        Assert.True(rig.Snap.Targets[0].IsLockingOn);

        // Kurz vor dem Ende rastet es noch ein, nach der eingestellten Dauer kommt der Text.
        rig.Run(milliseconds / 1000.0 - 0.1, P1);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.True(rig.Snap.Targets[0].IsLockingOn);
        Assert.False(rig.Snap.ShowText);

        rig.Run(0.1, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.True(rig.Snap.ShowText);
        Assert.False(rig.Snap.Targets[0].IsLockingOn);
        Assert.Equal([FunSoundCue.Acquire, FunSoundCue.Lock, FunSoundCue.Text], rig.Cues);
    }

    [Fact]
    public void LockOnDuration_OutOfRange_IsClampedByTheEngine()
    {
        var rig = new Rig(s => s.LockOnMilliseconds = 100);
        rig.Run(0.1, P1);
        rig.Run(0.3, P1);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase); // mindestens 500 ms
        rig.Run(0.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
    }

    [Fact]
    public void LockOnDuration_ChangedDuringLockOn_RecalculatesTheEnd_AndNeverHangs()
    {
        var rig = new Rig(s => s.LockOnMilliseconds = 1200);
        rig.Run(0.1, P1);      // t = 0,1; Einrasten ab 0,1
        rig.Run(0.4, P1);      // t = 0,5
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);

        // Verlängern auf 3 s: Ende bei 3,1 statt 1,3.
        var longer = rig.Settings.Clone();
        longer.LockOnMilliseconds = 3000;
        rig.Engine.Configure(longer, false);
        rig.Run(1.0, P1);      // t = 1,5, nach dem alten Ende
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.True(rig.Snap.Targets[0].IsLockingOn);
        rig.Run(1.6, P1);      // t = 3,1
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
        Assert.False(rig.Snap.Targets[0].IsLockingOn);

        // Verkürzen mitten im Einrasten (neuer Durchgang nach Episodenende): endet sofort, wenn die neue Dauer schon um ist.
        var rig2 = new Rig(s => s.LockOnMilliseconds = 5000);
        rig2.Run(0.1, P1);
        rig2.Run(1.0, P1);     // t = 1,1
        var shorter = rig2.Settings.Clone();
        shorter.LockOnMilliseconds = 500;
        rig2.Engine.Configure(shorter, false);
        Assert.Equal(FunPhase.ShowingText, rig2.Snap.Phase);
        Assert.False(rig2.Snap.Targets[0].IsLockingOn);
    }

    [Fact]
    public void LockOnDuration_AppliesToIncreaseDuringLockOn_AndToLateComersAfterTheText()
    {
        var rig = new Rig(s => s.LockOnMilliseconds = 2000);
        rig.Run(0.1, P1);      // t = 0,1; Einrasten bis 2,1
        rig.Run(0.9, P1);      // t = 1,0
        rig.Run(0.1, P1, P2);  // t = 1,1: P2 rastet bis 3,1 ein
        rig.Run(1.9, P1, P2);  // t = 3,0
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        Assert.True(rig.Snap.Targets.Single(t => t.Box.X > 0.3).IsLockingOn);
        rig.Run(0.1, P1, P2);  // t = 3,1
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);

        // Nachzügler während des Texts: eigene Meldung nach dem Textende mit derselben Dauer.
        rig.Run(1.0, P1, P2, P3);
        var textEnd = 3.1 + 6.0;
        rig.Run(textEnd - 4.1, P1, P2, P3);   // bis zum Textende
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        rig.Run(1.8, P1, P2, P3);
        Assert.Equal(FunPhase.LockingOn, rig.Snap.Phase);
        rig.Run(0.2, P1, P2, P3);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);
    }

    [Fact]
    public void SlowAnalysis_OnePerSecond_StillReachesText()
    {
        // Updates im 1-s-Takt, Ticks alle 100 ms: Die Spur darf zwischen zwei Analysen nicht verfallen, sonst würde sie nie bestätigt.
        var rig = new Rig(s => { s.AnalysesPerSecond = 1; s.DwellMilliseconds = 800; s.LockOnMilliseconds = 1200; });
        var reached = false;
        for (var step = 1; step <= 60 && !reached; step++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            if (step % 10 == 1)
                rig.Engine.OnDetections([P1]);
            rig.Engine.Tick();
            reached = rig.Snap.Phase == FunPhase.ShowingText;
        }

        Assert.True(reached);
        Assert.Equal(1, rig.Snap.Count);
    }

    [Fact]
    public void SlowAnalysis_OnePerSecond_EpisodeStaysOpenWhileTrackIsNotYetLost()
    {
        var rig = new Rig(s => { s.AnalysesPerSecond = 1; s.DwellMilliseconds = 0; });
        rig.Run(0.1, P1);
        rig.Run(1.2, P1);
        Assert.Equal(FunPhase.ShowingText, rig.Snap.Phase);

        // Die Erkennung bleibt aus: Die bestätigte Spur gilt erst nach der Verlustfrist (2,5 s bei 1 Analyse/s) als verloren.
        rig.Run(2.0);
        Assert.Equal(1, rig.Snap.Count); // Spur gilt erst nach 2,5 s ohne Treffer als verloren
        rig.Run(1.0);
        Assert.Equal(0, rig.Snap.Count);
    }
}
