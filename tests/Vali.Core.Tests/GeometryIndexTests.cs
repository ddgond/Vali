using NetTopologySuite.Geometries;
using Shouldly;
using Xunit;

namespace Vali.Core.Tests;

public class GeometryIndexTests
{
    private static readonly GeometryFactory GeometryFactory = new();

    private static Polygon Square(double x0, double y0, double size, LinearRing? hole = null) =>
        GeometryFactory.CreatePolygon(Ring(x0, y0, size), hole is null ? null : [hole]);

    private static LinearRing Ring(double x0, double y0, double size) =>
        GeometryFactory.CreateLinearRing([
            new Coordinate(x0, y0), new Coordinate(x0 + size, y0), new Coordinate(x0 + size, y0 + size),
            new Coordinate(x0, y0 + size), new Coordinate(x0, y0)
        ]);

    // Two squares, the first with a hole in the middle.
    private static Geometry[] TwoSquares() =>
    [
        GeometryFactory.CreateMultiPolygon([Square(0, 0, 10, Ring(4, 4, 2)), Square(20, 0, 5)])
    ];

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(5, 5, false)] // in the hole
    [InlineData(22, 2, true)] // in the second part
    [InlineData(15, 2, false)] // between the parts, inside the overall envelope
    [InlineData(100, 100, false)] // outside the envelope
    [InlineData(0, 5, true)] // on the boundary, as Covers counts it
    public void Covers_MatchesThePolygons(double lng, double lat, bool expected)
    {
        GeometryIndex.For(TwoSquares()).Covers(lng, lat).ShouldBe(expected);
    }

    [Fact]
    public void Covers_AgreesWithGeometryCovers()
    {
        var geometries = TwoSquares();
        var index = GeometryIndex.For(geometries);
        var random = new Random(1);
        for (var i = 0; i < 5_000; i++)
        {
            var lng = random.NextDouble() * 30 - 2;
            var lat = random.NextDouble() * 14 - 2;
            var point = GeometryFactory.CreatePoint(new Coordinate(lng, lat));
            index.Covers(lng, lat).ShouldBe(geometries.Any(g => g.Covers(point)), $"({lng}, {lat})");
        }
    }

    [Fact]
    public void For_BuildsOneIndexPerGeometrySet()
    {
        var geometries = TwoSquares();
        GeometryIndex.For(geometries).ShouldBeSameAs(GeometryIndex.For(geometries));
        GeometryIndex.For(TwoSquares()).ShouldNotBeSameAs(GeometryIndex.For(geometries));
    }

    [Fact]
    public void Covers_IsFalseForNoGeometries()
    {
        GeometryIndex.For([]).Covers(0, 0).ShouldBeFalse();
    }

    [Fact]
    public void DeserializeGeometriesFromFile_ParsesEachFileOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vali-geometry-{Guid.NewGuid():N}.geojson");
        File.WriteAllText(path, """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""");
        try
        {
            GeometryReader.DeserializeGeometriesFromFile(path).ShouldBeSameAs(GeometryReader.DeserializeGeometriesFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
