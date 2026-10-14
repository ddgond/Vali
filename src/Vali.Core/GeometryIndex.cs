using System.Runtime.CompilerServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;

namespace Vali.Core;

/// <summary>
/// A point-in-area index over a set of geometries, built once per set. Multi-part geometries are
/// split into their parts, each prepared and placed in an STR-tree, so a point is only tested
/// against the few parts whose envelope contains it, not every edge of one large geometry.
/// </summary>
public sealed class GeometryIndex
{
    private static readonly ConditionalWeakTable<Geometry[], GeometryIndex> Indexes = new();

    private readonly STRtree<IPreparedGeometry> _tree = new();
    private readonly Envelope _envelope = new();

    private GeometryIndex(Geometry[] geometries)
    {
        foreach (var geometry in geometries)
        {
            for (var i = 0; i < geometry.NumGeometries; i++)
            {
                var part = geometry.GetGeometryN(i);
                if (part.IsEmpty) continue;
                _tree.Insert(part.EnvelopeInternal, PreparedGeometryFactory.Prepare(part));
                _envelope.ExpandToInclude(part.EnvelopeInternal);
            }
        }

        _tree.Build();
    }

    public static GeometryIndex For(Geometry[] geometries) => Indexes.GetValue(geometries, g => new GeometryIndex(g));

    public bool Covers(double lng, double lat)
    {
        if (!_envelope.Covers(lng, lat)) return false;
        var point = new Point(lng, lat);
        foreach (var candidate in _tree.Query(new Envelope(lng, lng, lat, lat)))
        {
            if (candidate.Covers(point)) return true;
        }

        return false;
    }
}
