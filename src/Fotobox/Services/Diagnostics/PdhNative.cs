using System.Runtime.InteropServices;

namespace Fotobox.Services.Diagnostics;

/// <summary>
/// Schlanker Zugriff auf die Leistungsindikatoren (PDH, <c>pdh.dll</c>). Zählerpfade werden mit
/// <c>PdhAddEnglishCounterW</c> angelegt (englische Namen, unabhängig von der Windows-Sprache). Einzelne Aufrufe liefern den
/// PDH-Status zurück statt zu werfen; nicht nutzbare Werte sind am Status erkennbar. Nicht threadsicher (der Aufrufer serialisiert).
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    internal const uint Success = 0;
    internal const uint MoreData = 0x800007D2;
    internal const uint NoData = 0x800007D5;

    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint CStatusValidData = 0;
    private const uint CStatusNewData = 1;
    private const int MaxArrayAttempts = 4;

    private IntPtr _query;

    private PdhQuery(IntPtr query) => _query = query;

    /// <summary>Öffnet eine Abfrage; bei Fehler wird der Status in <paramref name="status"/> geliefert und null zurückgegeben.</summary>
    public static PdhQuery? Open(out uint status)
    {
        status = PdhOpenQueryW(null, IntPtr.Zero, out var query);
        return status == Success && query != IntPtr.Zero ? new PdhQuery(query) : null;
    }

    /// <summary>Fügt einen Zähler mit englischem Pfad hinzu (Platzhalter in Instanzen erlaubt).</summary>
    public uint TryAddCounter(string englishPath, out IntPtr counter)
    {
        counter = IntPtr.Zero;
        if (_query == IntPtr.Zero)
            return NoData;
        return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out counter);
    }

    /// <summary>Liest alle Zähler der Abfrage einmal ein (Ratenzähler brauchen zwei Aufrufe mit Abstand).</summary>
    public uint Collect() => _query == IntPtr.Zero ? NoData : PdhCollectQueryData(_query);

    /// <summary>Formatierter Wert eines Einzelzählers (ohne 100-%-Kappung, der Aufrufer klemmt selbst).</summary>
    public static uint TryGetDouble(IntPtr counter, out double value)
    {
        value = 0;
        var status = PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out var raw);
        if (status != Success)
            return status;
        if (raw.CStatus != CStatusValidData && raw.CStatus != CStatusNewData)
            return raw.CStatus;
        value = raw.DoubleValue;
        return Success;
    }

    /// <summary>
    /// Alle gültigen Instanzen eines Platzhalter-Zählers (Name, Wert). Instanzen mit ungültigem Wert (z. B. neu, erst ein
    /// Messpunkt) fehlen. Bei Fehler bleibt die Liste leer und der Status wird geliefert.
    /// </summary>
    public static uint TryGetArray(IntPtr counter, List<(string Name, double Value)> items)
    {
        items.Clear();
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero);
        if (status == Success || status == NoData)
            return status;
        if (status != MoreData)
            return status;

        for (var attempt = 0; attempt < MaxArrayAttempts; attempt++)
        {
            // Zwischen den Aufrufen können Instanzen dazukommen: etwas Reserve.
            var capacity = checked((int)Math.Min(size + 1024u + size / 8, int.MaxValue / 2));
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                uint bufferSize = (uint)capacity;
                status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref bufferSize, out var count, buffer);
                if (status == MoreData)
                {
                    size = bufferSize;
                    continue;
                }
                if (status != Success)
                    return status;

                var itemSize = Marshal.SizeOf<FormattedItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<FormattedItem>(buffer + i * itemSize);
                    if ((item.CStatus != CStatusValidData && item.CStatus != CStatusNewData) || item.Name == IntPtr.Zero)
                        continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name is not null)
                        items.Add((name, item.Value));
                }
                return Success;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return MoreData;
    }

    public void Dispose()
    {
        var query = _query;
        _query = IntPtr.Zero;
        if (query != IntPtr.Zero)
            _ = PdhCloseQuery(query);
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: Zeiger auf den Namen, dann PDH_FMT_COUNTERVALUE (CStatus, Union ab Offset 8).
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedItem
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;
    }

    // PDH_FMT_COUNTERVALUE mit double-Wert.
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType, out FormattedValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
