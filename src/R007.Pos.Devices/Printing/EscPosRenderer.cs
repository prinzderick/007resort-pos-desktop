using System.Text;

namespace R007.Pos.Devices.Printing;

/// <summary>
/// Renders a <see cref="ReceiptDocument"/> to ESC/POS bytes for an 80 mm thermal printer, and to a plain-text preview
/// (used by the file/console printers and snapshot tests). The receipt <em>content</em> comes from the API; this class
/// only does layout and byte encoding. Text is encoded as CP437-safe ASCII: the Naira sign and other non-ASCII
/// characters are transliterated (printers vary widely in code page support, so we do not depend on any).
/// </summary>
public static class EscPosRenderer
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private const byte Lf = 0x0A;

    /// <summary>ESC p 0 25 250: pulse drawer pin 2 (the common wiring for RJ11 drawers behind the printer).</summary>
    public static byte[] DrawerKick() => [Esc, 0x70, 0x00, 0x19, 0xFA];

    public static byte[] Render(ReceiptDocument document, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bytes = new List<byte>(512);

        bytes.AddRange([Esc, 0x40]);                 // ESC @  initialise
        bytes.AddRange([Esc, 0x74, 0x00]);           // ESC t 0  code page CP437
        if (document.OpenDrawer)
        {
            bytes.AddRange(DrawerKick());
        }

        var alignment = ReceiptAlignment.Left;
        var bold = false;
        var doubled = false;
        SetAlignment(bytes, alignment);

        foreach (var line in document.Lines)
        {
            if (line.Alignment != alignment)
            {
                alignment = line.Alignment;
                SetAlignment(bytes, alignment);
            }

            if (line.Bold != bold)
            {
                bold = line.Bold;
                bytes.AddRange([Esc, 0x45, (byte)(bold ? 1 : 0)]);
            }

            if (line.DoubleSize != doubled)
            {
                doubled = line.DoubleSize;
                bytes.AddRange([Gs, 0x21, (byte)(doubled ? 0x11 : 0x00)]);
            }

            var width = doubled ? Math.Max(1, charactersPerLine / 2) : charactersPerLine;
            foreach (var wrapped in Wrap(Sanitize(line.Text), width))
            {
                bytes.AddRange(Encoding.ASCII.GetBytes(wrapped));
                bytes.Add(Lf);
            }
        }

        if (bold)
        {
            bytes.AddRange([Esc, 0x45, 0x00]);
        }

        if (doubled)
        {
            bytes.AddRange([Gs, 0x21, 0x00]);
        }

        if (!string.IsNullOrEmpty(document.QrData))
        {
            SetAlignment(bytes, ReceiptAlignment.Center);
            AppendQr(bytes, document.QrData);
            bytes.Add(Lf);
        }

        SetAlignment(bytes, ReceiptAlignment.Left);
        if (document.CutPaper)
        {
            bytes.AddRange([Lf, Lf, Lf]);
            bytes.AddRange([Gs, 0x56, 0x42, 0x00]);  // GS V 66 0  feed to cut position and partial cut
        }

        return [.. bytes];
    }

    /// <summary>A fixed-width text preview of what the receipt looks like on paper.</summary>
    public static string RenderText(ReceiptDocument document, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        foreach (var line in document.Lines)
        {
            var width = line.DoubleSize ? Math.Max(1, charactersPerLine / 2) : charactersPerLine;
            foreach (var wrapped in Wrap(Sanitize(line.Text), width))
            {
                var pad = line.Alignment switch
                {
                    ReceiptAlignment.Center => Math.Max(0, (charactersPerLine - wrapped.Length * (line.DoubleSize ? 2 : 1)) / 2),
                    ReceiptAlignment.Right => Math.Max(0, charactersPerLine - wrapped.Length * (line.DoubleSize ? 2 : 1)),
                    _ => 0,
                };
                sb.Append(' ', pad).Append(wrapped).Append('\n');
            }
        }

        if (!string.IsNullOrEmpty(document.QrData))
        {
            var label = $"[QR: {document.QrData}]";
            sb.Append(' ', Math.Max(0, (charactersPerLine - label.Length) / 2)).Append(label).Append('\n');
        }

        if (document.OpenDrawer)
        {
            sb.Append("[drawer kick]\n");
        }

        if (document.CutPaper)
        {
            sb.Append("[cut]\n");
        }

        return sb.ToString();
    }

    /// <summary>Transliterates to printable ASCII (Naira sign becomes "N"); unknown characters become "?".</summary>
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '₦':
                    sb.Append('N');
                    break;
                case '‘' or '’':
                    sb.Append('\'');
                    break;
                case '“' or '”':
                    sb.Append('"');
                    break;
                case '–' or '—':
                    sb.Append('-');
                    break;
                case '\t':
                    sb.Append("    ");
                    break;
                case >= ' ' and <= '~':
                    sb.Append(ch);
                    break;
                default:
                    var folded = new string(ch, 1).Normalize(NormalizationForm.FormD);
                    sb.Append(folded.Length > 0 && folded[0] is >= ' ' and <= '~' ? folded[0] : '?');
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Greedy word wrap; words longer than the width are hard-split. An empty string yields one empty line.</summary>
    public static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length <= width)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var w = word;
            while (w.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return w[..width];
                w = w[width..];
            }

            if (current.Length == 0)
            {
                current.Append(w);
            }
            else if (current.Length + 1 + w.Length <= width)
            {
                current.Append(' ').Append(w);
            }
            else
            {
                yield return current.ToString();
                current.Clear().Append(w);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void SetAlignment(List<byte> bytes, ReceiptAlignment alignment) =>
        bytes.AddRange([Esc, 0x61, (byte)alignment]);

    /// <summary>GS ( k function 165/167/169/180/181: QR model 2, module size 6, error correction M.</summary>
    private static void AppendQr(List<byte> bytes, string data)
    {
        var payload = Encoding.ASCII.GetBytes(Sanitize(data));
        var storeLength = payload.Length + 3;
        bytes.AddRange([Gs, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]);                 // model 2
        bytes.AddRange([Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06]);                       // module size 6
        bytes.AddRange([Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31]);                       // error correction M
        bytes.AddRange([Gs, 0x28, 0x6B, (byte)(storeLength & 0xFF), (byte)((storeLength >> 8) & 0xFF), 0x31, 0x50, 0x30]);
        bytes.AddRange(payload);                                                              // store data
        bytes.AddRange([Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]);                       // print symbol
    }
}
