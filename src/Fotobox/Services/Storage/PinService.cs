using System.Security.Cryptography;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>PIN-Verwaltung: PBKDF2-SHA256 mit zufälligem 16-Byte-Salt, abgelegt in den Sicherheitseinstellungen.</summary>
public sealed class PinService(ISettingsStore settings) : IPinService
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MinLength = 4;
    private const int MaxLength = 12;

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
        if (!IsValidPin(pin))
            throw new ArgumentException($"Die PIN muss aus {MinLength} bis {MaxLength} Ziffern bestehen.", nameof(pin));

        var copy = settings.Current.Clone();
        var iterations = Math.Max(copy.Security.PinIterations, SettingsNormalizer.MinPinIterations);
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

        try
        {
            var salt = Convert.FromBase64String(sec.PinSalt);
            var expected = Convert.FromBase64String(sec.PinHash);
            if (salt.Length == 0 || expected.Length == 0)
                return false;
            var actual = Derive(pin, salt, sec.PinIterations, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidPin(string? pin) =>
        pin is { Length: >= MinLength and <= MaxLength } && pin.All(c => c is >= '0' and <= '9');

    private static byte[] Derive(string pin, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, length);
}
