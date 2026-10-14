using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;

namespace Vali.Core;

/// <summary>
/// A raster of byte values over latitude/longitude cells, read with <c>external:&lt;key&gt;</c> in
/// expressions when a map definition names it in <c>gridFiles</c>. Lets a filter use any gridded
/// dataset (urbanisation, land cover, population classes...) without per-location data files.
///
/// File format (gzip-compressed, little-endian): the ASCII magic "VGRD", int32 version (1), a
/// projection byte (0: longitude/latitude in degrees; 1: Mollweide on a sphere, in metres, as
/// ESRI:54009 and GHSL use), double sphere radius in metres (unused for 0), int32 width, int32
/// height, double left and top edges, double cell width and height, a nodata byte, then each row
/// from top to bottom as int32 run count followed by that many (int32 start column, byte value)
/// runs, starting at column 0. Cells holding the nodata value read as NaN.
/// </summary>
public sealed class ValueGrid
{
    private static readonly ConcurrentDictionary<string, Lazy<ValueGrid>> Loaded = new();
    private static FrozenDictionary<string, ValueGrid> _active = FrozenDictionary<string, ValueGrid>.Empty;

    private readonly Projection _projection;
    private readonly double _radius;
    private readonly int _width;
    private readonly int _height;
    private readonly double _left;
    private readonly double _top;
    private readonly double _cellWidth;
    private readonly double _cellHeight;
    private readonly byte _nodata;
    private readonly int[][] _runStarts;
    private readonly byte[][] _runValues;

    public enum Projection : byte
    {
        LngLat = 0,
        Mollweide = 1,
    }

    private ValueGrid(Projection projection, double radius, int width, int height, double left, double top, double cellWidth, double cellHeight, byte nodata, int[][] runStarts, byte[][] runValues)
    {
        _projection = projection;
        _radius = radius;
        _width = width;
        _height = height;
        _left = left;
        _top = top;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _nodata = nodata;
        _runStarts = runStarts;
        _runValues = runValues;
    }

    /// <summary>The value at a point, or NaN outside the grid or on nodata.</summary>
    public double Sample(double lat, double lng)
    {
        var (x, y) = _projection == Projection.Mollweide ? Mollweide(lat, lng, _radius) : (lng, lat);
        var row = (int)Math.Floor((_top - y) / _cellHeight);
        var col = (int)Math.Floor((x - _left) / _cellWidth);
        if (row < 0 || row >= _height || col < 0 || col >= _width)
        {
            return double.NaN;
        }

        var starts = _runStarts[row];
        var i = Array.BinarySearch(starts, col);
        if (i < 0)
        {
            i = ~i - 1;
        }

        if (i < 0)
        {
            return double.NaN;
        }

        var value = _runValues[row][i];
        return value == _nodata ? double.NaN : value;
    }

    /// <summary>Mollweide on a sphere: solves 2θ + sin 2θ = π sin φ by Newton's method.</summary>
    public static (double x, double y) Mollweide(double lat, double lng, double radius)
    {
        var phi = lat * Math.PI / 180;
        var lambda = lng * Math.PI / 180;
        double theta;
        if (Math.Abs(Math.Abs(phi) - Math.PI / 2) < 1e-10)
        {
            theta = Math.CopySign(Math.PI / 2, phi);
        }
        else
        {
            theta = phi;
            var target = Math.PI * Math.Sin(phi);
            for (var i = 0; i < 50; i++)
            {
                var step = (2 * theta + Math.Sin(2 * theta) - target) / (2 + 2 * Math.Cos(2 * theta));
                theta -= step;
                if (Math.Abs(step) < 1e-12)
                {
                    break;
                }
            }
        }

        return (2 * Math.Sqrt(2) / Math.PI * radius * lambda * Math.Cos(theta), Math.Sqrt(2) * radius * Math.Sin(theta));
    }

    public static ValueGrid Load(string path) =>
        Loaded.GetOrAdd(Path.GetFullPath(path), static p => new Lazy<ValueGrid>(() => Read(p))).Value;

    /// <summary>The grids <c>external:&lt;key&gt;</c> reads for the map being generated.</summary>
    public static void Activate(IReadOnlyDictionary<string, string> gridFiles) =>
        _active = gridFiles.ToFrozenDictionary(x => x.Key, x => Load(x.Value));

    /// <summary>The active grid named <paramref name="key"/> at a point; NaN when there is none.</summary>
    public static double SampleActive(string key, double lat, double lng) =>
        _active.TryGetValue(key, out var grid) ? grid.Sample(lat, lng) : double.NaN;

    private static ValueGrid Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.ASCII);
        if (new string(reader.ReadChars(4)) != "VGRD")
        {
            throw new InvalidDataException($"{path} is not a value grid (missing VGRD header).");
        }

        var version = reader.ReadInt32();
        if (version != 1)
        {
            throw new InvalidDataException($"{path} is value grid version {version}; only version 1 is supported.");
        }

        var projection = (Projection)reader.ReadByte();
        if (!Enum.IsDefined(projection))
        {
            throw new InvalidDataException($"{path} uses unknown projection {(byte)projection}.");
        }

        var radius = reader.ReadDouble();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var left = reader.ReadDouble();
        var top = reader.ReadDouble();
        var cellWidth = reader.ReadDouble();
        var cellHeight = reader.ReadDouble();
        var nodata = reader.ReadByte();
        if (width <= 0 || height <= 0 || cellWidth <= 0 || cellHeight <= 0 || (projection == Projection.Mollweide && radius <= 0))
        {
            throw new InvalidDataException($"{path} has an invalid grid size.");
        }

        var runStarts = new int[height][];
        var runValues = new byte[height][];
        for (var row = 0; row < height; row++)
        {
            var count = reader.ReadInt32();
            var starts = new int[count];
            var values = new byte[count];
            for (var i = 0; i < count; i++)
            {
                starts[i] = reader.ReadInt32();
                values[i] = reader.ReadByte();
            }

            runStarts[row] = starts;
            runValues[row] = values;
        }

        return new ValueGrid(projection, radius, width, height, left, top, cellWidth, cellHeight, nodata, runStarts, runValues);
    }
}
