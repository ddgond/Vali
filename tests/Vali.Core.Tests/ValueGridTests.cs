using System.IO.Compression;
using System.Text;
using Shouldly;
using Xunit;

namespace Vali.Core.Tests;

// ValueGrid.Activate is process-wide, so these run apart from tests that might read it.
[Collection(nameof(ValueGridTests))]
public class ValueGridTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vali-grid-{Guid.NewGuid():N}.grid");

    // A 4 × 2 grid of 1° cells from (0° E, 2° N): row 0 is 10 10 30 30, row 1 is 255 (nodata) then 13 13 13.
    public ValueGridTests() => WriteGrid(_path, west: 0, north: 2, cell: 1, nodata: 255,
    [
        [(0, 10), (2, 30)],
        [(0, 255), (1, 13)],
    ], width: 4);

    public void Dispose()
    {
        ValueGrid.Activate(new Dictionary<string, string>());
        File.Delete(_path);
    }

    [Theory]
    [InlineData(1.5, 0.5, 10)]
    [InlineData(1.5, 1.99, 10)]
    [InlineData(1.5, 2.0, 30)] // the first cell of the second run
    [InlineData(1.5, 3.5, 30)]
    [InlineData(0.5, 1.5, 13)]
    [InlineData(0.5, 3.9, 13)]
    public void Sample_ReadsTheRunHoldingTheCell(double lat, double lng, double expected)
    {
        ValueGrid.Load(_path).Sample(lat, lng).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.5, 0.5)] // nodata
    [InlineData(2.5, 1)] // north of the grid
    [InlineData(-0.5, 1)] // south of it
    [InlineData(1, -0.5)] // west of it
    [InlineData(1, 4.5)] // east of it
    public void Sample_IsNaNOnNodataAndOutsideTheGrid(double lat, double lng)
    {
        double.IsNaN(ValueGrid.Load(_path).Sample(lat, lng)).ShouldBeTrue();
    }

    [Fact]
    public void ExternalNumber_FallsBackToTheActiveGrid()
    {
        ValueGrid.Activate(new Dictionary<string, string> { ["urbanisation"] = _path });
        NewLocation(1.5, 2.5).ExternalNumber("urbanisation").ShouldBe(30);
        double.IsNaN(NewLocation(1.5, 2.5).ExternalNumber("other")).ShouldBeTrue();
    }

    [Fact]
    public void ExternalNumber_PrefersTheLocationsOwnData()
    {
        ValueGrid.Activate(new Dictionary<string, string> { ["urbanisation"] = _path });
        var location = NewLocation(1.5, 2.5);
        location.ExternalData["urbanisation"] = "21";
        location.ExternalNumber("urbanisation").ShouldBe(21);
    }

    [Fact]
    public void Expressions_FilterByTheGrid()
    {
        ValueGrid.Activate(new Dictionary<string, string> { ["urbanisation"] = _path });
        var city = LocationLakeFilterer.CompileBoolLocationExpression("external:urbanisation eq 30");
        var settled = LocationLakeFilterer.CompileBoolLocationExpression("external:urbanisation gte 13");
        city(NewLocation(1.5, 2.5)).ShouldBeTrue();
        city(NewLocation(1.5, 0.5)).ShouldBeFalse();
        settled(NewLocation(0.5, 2.5)).ShouldBeTrue();
        settled(NewLocation(1.5, 0.5)).ShouldBeFalse();
        settled(NewLocation(0.5, 0.5)).ShouldBeFalse(); // nodata fails every comparison
    }

    [Theory]
    // Expected values from PROJ (ESRI:54009, World Mollweide).
    [InlineData(40.7, -74.0, -6243953.32, 4867490.99)]
    [InlineData(-33.9, 151.2, 13500482.59, -4096821.01)]
    [InlineData(78.2, 15.6, 567259.28, 8405416.53)]
    [InlineData(90, 10, 0, 9020047.85)]
    public void Mollweide_MatchesProj(double lat, double lng, double x, double y)
    {
        var (mx, my) = ValueGrid.Mollweide(lat, lng, 6378137);
        mx.ShouldBe(x, 0.01);
        my.ShouldBe(y, 0.01);
    }

    [Fact]
    public void Sample_ProjectsPointsOntoAMollweideGrid()
    {
        // Two 1,000 km cells either side of the prime meridian, just north of the equator.
        var path = Path.Combine(Path.GetTempPath(), $"vali-grid-{Guid.NewGuid():N}.grid");
        WriteGrid(path, west: -1_000_000, north: 1_000_000, cell: 1_000_000, nodata: 255, [[(0, 11), (1, 30)]], width: 2,
            ValueGrid.Projection.Mollweide, radius: 6378137);
        try
        {
            var grid = ValueGrid.Load(path);
            grid.Sample(5, -5).ShouldBe(11);
            grid.Sample(5, 5).ShouldBe(30);
            double.IsNaN(grid.Sample(-5, 5)).ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReadsEachFileOnce()
    {
        ValueGrid.Load(_path).ShouldBeSameAs(ValueGrid.Load(_path));
    }

    [Fact]
    public void Load_RejectsOtherFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vali-grid-{Guid.NewGuid():N}.grid");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            gzip.Write("NOPE"u8);
        }

        try
        {
            Should.Throw<InvalidDataException>(() => ValueGrid.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Location NewLocation(double lat, double lng) => new()
    {
        Lat = lat,
        Lng = lng,
        Google = new GoogleData { PanoId = "", CountryCode = "NO" },
        Osm = new OsmData(),
        Nominatim = new NominatimData { CountryCode = "NO", SubdivisionCode = "NO-03" },
    };

    private static void WriteGrid(string path, double west, double north, double cell, byte nodata, (int start, byte value)[][] rows, int width,
        ValueGrid.Projection projection = ValueGrid.Projection.LngLat, double radius = 0)
    {
        using var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest);
        using var writer = new BinaryWriter(gzip, Encoding.ASCII);
        writer.Write("VGRD"u8);
        writer.Write(1);
        writer.Write((byte)projection);
        writer.Write(radius);
        writer.Write(width);
        writer.Write(rows.Length);
        writer.Write(west);
        writer.Write(north);
        writer.Write(cell);
        writer.Write(cell);
        writer.Write(nodata);
        foreach (var row in rows)
        {
            writer.Write(row.Length);
            foreach (var (start, value) in row)
            {
                writer.Write(start);
                writer.Write(value);
            }
        }
    }
}
