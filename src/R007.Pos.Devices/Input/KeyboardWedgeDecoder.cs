namespace R007.Pos.Devices.Input;

/// <summary>
/// Decodes "keyboard wedge" devices (USB/BT barcode scanners and NFC readers that type their payload followed by Enter)
/// out of the character stream. A real scan is a burst: characters arrive faster than a person can type and end with
/// Enter. Anything slower is treated as ordinary typing and is not reported. Pure logic (no WPF) so it is unit-tested.
/// </summary>
public sealed class KeyboardWedgeDecoder(TimeSpan maxKeyInterval, int minLength = 4)
{
    private readonly System.Text.StringBuilder _buffer = new();
    private DateTimeOffset? _last;

    /// <summary>True once 3+ characters arrived as a burst, so the UI can swallow the rest instead of typing it into a box.</summary>
    public bool IsBursting => _buffer.Length >= 3;

    /// <summary>Feed one character (with its arrival time). Returns the completed payload on Enter if it was a burst, else null.</summary>
    public string? Feed(char character, DateTimeOffset at)
    {
        if (character is '\r' or '\n')
        {
            var result = _buffer.Length >= minLength ? _buffer.ToString() : null;
            Reset();
            return result;
        }

        if (char.IsControl(character))
        {
            return null;
        }

        if (_last is { } last && at - last > maxKeyInterval)
        {
            // Too slow to be a scanner: restart (this character may begin a new burst).
            Reset();
        }

        _buffer.Append(character);
        _last = at;
        return null;
    }

    /// <summary>Discard any partial input (focus changed, timeout).</summary>
    public void Reset()
    {
        _buffer.Clear();
        _last = null;
    }
}
