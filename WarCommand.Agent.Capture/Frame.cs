namespace WarCommand.Agent.Capture;

/// <summary>
/// One captured frame, bottom-up BGRA. Lives for as long as the scan and never leaves the process:
/// binding rule 3 forbids writing a frame to disk under any flag, debug builds included.
/// </summary>
public sealed class Frame
{
    internal Frame(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>BGRA, 4 bytes per pixel, row 0 is the TOP row: GetDIBits is asked for top-down.</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// A frame rebuilt from a saved near-white MASK: white where the mask is set, black elsewhere.
    /// </summary>
    /// <remarks>
    /// This is how a decode is replayed without a screenshot. Binding rule 3 keeps frames off disk
    /// and off the wire, and a mask is not a frame: it is the 1-bit shape of the text the scanner
    /// already isolated, with no game imagery left in it. Caching that is what makes tuning the
    /// solver possible without a human holding a cursor still for every trial.
    /// </remarks>
    public static Frame FromMask(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var height = rows.Count;
        var width = height == 0 ? 0 : rows.Max(r => r.Length);
        var pixels = new byte[Math.Max(1, width * height * 4)];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (rows[y][x] == '.')
                {
                    continue;
                }

                var i = ((y * width) + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }
        }

        return new Frame(pixels, width, height);
    }

    /// <summary>The near-white mask of a region, as one string per row. Never the pixels.</summary>
    public IReadOnlyList<string> MaskOf(TextBlob blob, int threshold)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var rows = new List<string>(blob.Height);
        for (var y = blob.Top; y <= blob.Bottom; y++)
        {
            var row = new char[blob.Width];
            for (var x = 0; x < blob.Width; x++)
            {
                row[x] = IsNearWhite(blob.Left + x, y, threshold) ? '#' : '.';
            }

            rows.Add(new string(row));
        }

        return rows;
    }

    /// <summary>True when every channel is at or above the threshold. The readout is near-white text.</summary>
    public bool IsNearWhite(int x, int y, int threshold)
    {
        var i = ((y * Width) + x) * 4;
        return Pixels[i] >= threshold && Pixels[i + 1] >= threshold && Pixels[i + 2] >= threshold;
    }
}
