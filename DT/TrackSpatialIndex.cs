using System.Collections.Generic;
using DV.PointSet;
using UnityEngine;

public class TrackSpatialIndex
{
    private readonly float cellSize;
    private readonly Dictionary<Vector2Int, List<RailTrack>> grid = new Dictionary<Vector2Int, List<RailTrack>>();
    private readonly Dictionary<RailTrack, Bounds> trackBoundsCache = new Dictionary<RailTrack, Bounds>();

    public TrackSpatialIndex(float cellSize = 100f)
    {
        this.cellSize = cellSize;
    }

    public void BuildIndex(IEnumerable<RailTrack> tracks)
    {
        grid.Clear();
        trackBoundsCache.Clear();

        foreach (var track in tracks)
        {
            EquiPointSet pointSet = track.GetKinkedPointSet();
            if (pointSet == null || pointSet.points.Length == 0) continue;

            // Compute track local AABB
            Bounds bounds = new Bounds((Vector3)pointSet.points[0].position, Vector3.zero);
            for (int i = 1; i < pointSet.points.Length; i++)
            {
                bounds.Encapsulate((Vector3)pointSet.points[i].position);
            }

            // Expand bounds slightly to handle floating tolerances
            bounds.Expand(5f); 
            trackBoundsCache[track] = bounds;

            // Map track bounds into grid cells
            Vector2Int minCell = GetCellCoords(bounds.min);
            Vector2Int maxCell = GetCellCoords(bounds.max);

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    Vector2Int cell = new Vector2Int(x, y);
                    if (!grid.TryGetValue(cell, out var list))
                    {
                        list = new List<RailTrack>();
                        grid[cell] = list;
                    }
                    list.Add(track);
                }
            }
        }
    }

    private Vector2Int GetCellCoords(Vector3 position)
    {
        return new Vector2Int(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.z / cellSize) // Tracks are horizontally distributed
        );
    }

    public RailTrack GetTrackClosestToFast(Vector3 position, float minDistFromTrackEnd, out int closestNodeIndex)
    {
        closestNodeIndex = -1;
        Vector2Int centerCell = GetCellCoords(position);

        List<RailTrack> candidates = GetCandidates(centerCell);
        
        // Fallback to all tracks if grid misses (rare boundary cases)
        if (candidates.Count == 0)
        {
            return RailTrack.GetClosest(position, minDistFromTrackEnd).track;
        }

        RailTrack bestTrack = null;
        EquiPointSet.Point? bestPoint = null;
        float bestSqrDist = float.PositiveInfinity;

        // Broad-phase distance filter on candidate bounds before checking individual points
        for (int i = 0; i < candidates.Count; i++)
        {
            RailTrack track = candidates[i];
            if (trackBoundsCache.TryGetValue(track, out Bounds bounds))
            {
                // Quick bounding box check: if point is further from bounding box than best distance, skip!
                if (bounds.SqrDistance(position) >= bestSqrDist)
                    continue;
            }

            // Narrow phase check
            var (closestPoint, distanceToPoint) = RailTrack.GetClosestPoint(track, position, minDistFromTrackEnd);
            if (distanceToPoint < bestSqrDist && closestPoint.HasValue)
            {
                bestSqrDist = distanceToPoint;
                bestTrack = track;
                bestPoint = closestPoint;
            }
        }

        if (bestPoint.HasValue)
        {
            closestNodeIndex = bestPoint.Value.index;
        }

        return bestTrack;
    }

    private List<RailTrack> GetCandidates(Vector2Int centerCell)
    {
        List<RailTrack> results = new List<RailTrack>();
        
        // Search 3x3 grid around cell
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                Vector2Int cell = new Vector2Int(centerCell.x + x, centerCell.y + y);
                if (grid.TryGetValue(cell, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!results.Contains(list[i]))
                        {
                            results.Add(list[i]);
                        }
                    }
                }
            }
        }
        return results;
    }
}