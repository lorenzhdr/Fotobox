using System.Security.Cryptography;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>PIN-Verwaltung: PBKDF2-SHA256 mit zufälligem 16-Byte-Salt, abgelegt in den Sicherheitseinstellungen.</summary>
public sealed class PinService(ISettingsStore settings) : IPinService
{
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public bool HasPin
    {
        get
        {
            var sec = settings.Current.Security;
            return !string.IsNullOrEmpty(sec.PinHash) && !string.IsNullOrEmpty(sec.PinSalt);
        }
    }

    public void SetPin(string pin)
    {
        if (!PinRules.IsValid(pin))
            throw new ArgumentException($"Die PIN muss aus {SettingsLimits.PinMinLength} bis {SettingsLimits.PinMaxLength} Ziffern bestehen.", nameof(pin));

        var copy = settings.Current.Clone();
        // Eine unplausibel hohe Zahl aus der Datei (z. B. bei fehlendem Hash) würde die Berechnung minutenlang blockieren.
        var iterations = copy.Security.PinIterations > SettingsNormalizer.MaxPlausiblePinIterations
            ? new SecuritySettings().PinIterations
            : Math.Max(copy.Security.PinIterations, SettingsNormalizer.MinPinIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(pin, salt, iterations, HashSize);

        copy.Security.PinIterations = iterations;
        copy.Security.PinSalt = Convert.ToBase64String(salt);
        copy.Security.PinHash = Convert.ToBase64String(hash);
        settings.Save(copy);
    }

    public bool Verify(string pin)
    {
        if (string.IsNullOrEmpty(pin))
            return false;

        var sec = settings.Current.Security;
        if (string.IsNullOrEmpty(sec.PinHash) || string.IsNullOrEmpty(sec.PinSalt) || sec.PinIterations < 1)
            return false;
        // Eine manipulierte oder kaputte settings.json darf die Box nicht per riesiger Iterationszahl minutenlang rechnen lassen
        // (und auch kein unerwarteter Hash/Salt): Beim Prüfen gilt jede Iterationszahl ab 1, die Regeln stehen in IsPlausibleStoredPin.
        if (!IsPlausibleStoredPin(sec, minIterations: 1))
        {
            AppLog.Warn($"PIN-Prüfung abgelehnt: unplausible gespeicherte PIN-Daten (Iterationen {sec.PinIterations}).");
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(sec.PinSalt);
            var expected = Convert.FromBase64String(sec.PinHash);
            var actual = Derive(pin, salt, sec.PinIterations, HashSize);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gemeinsame Plausibilitätsprüfung gespeicherter PIN-Daten (<see cref="Verify"/> und Import in <c>SettingsTransfer</c>):
    /// Hash als Base64 mit genau 32 Byte, Salt als Base64 mit mindestens einem Byte, Iterationen von
    /// <paramref name="minIterations"/> bis <see cref="SettingsNormalizer.MaxPlausiblePinIterations"/>. Die Prüfung rechnet nichts.
    /// </summary>
    /// <param name="minIterations">Untergrenze der Iterationen: beim Prüfen 1 (bestehende Werte bleiben gültig), beim Import
    /// <see cref="SettingsNormalizer.MinPinIterations"/> (eine schwache PIN aus fremder Datei wird nicht übernommen).</param>
    public static bool IsPlausibleStoredPin(SecuritySettings? s, int minIterations)
    {
        if (s is null || string.IsNullOrEmpty(s.PinHash) || string.IsNullOrEmpty(s.PinSalt))
            return false;
        if (s.PinIterations < Math.Max(1, minIterations) || s.PinIterations > SettingsNormalizer.MaxPlausiblePinIterations)
            return false;

        var hash = new byte[s.PinHash.Length];
        if (!Convert.TryFromBase64String(s.PinHash, hash, out var hashLength) || hashLength != HashSize)
            return false;
        var salt = new byte[s.PinSalt.Length];
        return Convert.TryFromBase64String(s.PinSalt, salt, out var saltLength) && saltLength > 0;
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, length);
}
