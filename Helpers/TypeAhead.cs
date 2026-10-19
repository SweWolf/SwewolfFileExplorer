using System.Globalization;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Typing in a list jumps to an item by its name, as Windows Explorer does: a letter goes to the next item that starts with it,
/// the same letter again goes on to the next one after that (and round to the top), and several letters typed in a row
/// (less than a second apart) find the first item that starts with all of them, from the item the user is on.
/// Only the decision is here (no controls), so it can be tried on its own.
/// </summary>
public sealed class TypeAhead
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    private string _buffer = "";
    private DateTime _last = DateTime.MinValue;

    /// <summary>Forgets what was typed (the folder changed, another control got the focus).</summary>
    public void Reset() => _buffer = "";

    /// <summary>
    /// The index of the item to go to after <paramref name="typed"/> was typed, or -1 if no name starts with it.
    /// <paramref name="current"/> is the item the user is on (-1 for none).
    /// </summary>
    public int Next(IReadOnlyList<string> names, int current, string typed, DateTime now)
    {
        if (names.Count == 0 || typed.Length == 0) return -1;

        if (now - _last > Pause) _buffer = "";
        _last = now;
        if (_buffer.Length == 0 && string.IsNullOrWhiteSpace(typed)) return -1;   // a space does not start a name
        _buffer += typed;

        // "hhh": the same letter over and over means "the next one starting with h", not an item that starts with "hhh".
        bool sameLetter = _buffer.Length == 1 || _buffer.All(c => SameChar(c, _buffer[0]));
        string prefix   = sameLetter ? _buffer[..1] : _buffer;

        // One letter (or a repeat) starts after the current item; a longer word may stay on the current item.
        int start = sameLetter ? current + 1 : Math.Max(current, 0);

        for (int i = 0; i < names.Count; i++)
        {
            int index = (start + i) % names.Count;
            if (names[index].StartsWith(prefix, ignoreCase: true, CultureInfo.CurrentCulture))
                return index;
        }

        return -1;
    }

    private static bool SameChar(char a, char b) => char.ToUpper(a, CultureInfo.CurrentCulture) == char.ToUpper(b, CultureInfo.CurrentCulture);
}
