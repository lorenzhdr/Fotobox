using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fotobox.Views.Admin;

/// <summary>Gemeinsames Verhalten der PIN-Felder: nur Ziffern, Ziffernblock-Eingaben, Caret am Ende.</summary>
internal static class PinBoxHelper
{
    private static readonly MethodInfo? SelectMethod =
        typeof(PasswordBox).GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Beschränkt das Feld auf Ziffern (Tippen; Einfügen ist gesperrt).</summary>
    public static void Configure(PasswordBox box)
    {
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
                e.Handled = true;
        };
        DataObject.AddPastingHandler(box, (_, e) => e.CancelCommand());
    }

    public static void Append(PasswordBox box, char digit)
    {
        if (!char.IsAsciiDigit(digit) || box.Password.Length >= box.MaxLength)
            return;
        box.Password += digit;
        MoveCaretToEnd(box);
    }

    public static void Backspace(PasswordBox box)
    {
        if (box.Password.Length == 0)
            return;
        box.Password = box.Password[..^1];
        MoveCaretToEnd(box);
    }

    private static void MoveCaretToEnd(PasswordBox box)
    {
        try
        {
            // PasswordBox bietet keine öffentliche Caret-API; Select(start, length) ist intern.
            SelectMethod?.Invoke(box, [box.Password.Length, 0]);
        }
        catch (Exception)
        {
            // Rein kosmetisch.
        }
    }
}
