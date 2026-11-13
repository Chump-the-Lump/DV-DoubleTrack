using UnityEngine;

namespace DoubleTrack.Books;

/// <summary>
/// Double Track's switches a drawing names, each found in this world by its id and its place
/// (in the map frame, within 3 m), with its index in the registry's ordered junctions: the index
/// a book's touch cell names. Double Track's junctions follow the stock ones in that order, so
/// the stock cells keep their switches.
/// </summary>
internal sealed class BookSwitches
{
    public readonly Dictionary<string, Junction> Junctions = new();
    public readonly Dictionary<string, int> Indices = new();
    private RailTrackRegistryBase? registry;

    private static Vector3 MapOf(RailTrackRegistryBase registry, Vector3 world)
    {
        var root = registry.TrackRootParent;
        return root.parent != null ? root.parent.InverseTransformPoint(world) : world;
    }

    /// <summary>Finds every wanted switch: null when all are found, else why not.</summary>
    public string? Find(RailTrackRegistryBase registry, int expectedTotal, IEnumerable<(string Key, string Id, float[] Pos)> wanted)
    {
        this.registry = registry;
        if (Settings.LoadedTrackMode != Settings.TrackMode.Normal)
            return $"drawn for the Normal layout, this world has the {Settings.LoadedTrackMode} one";
        var added = AllTracksPatch.AddedJunctions?.Where(j => j != null).ToList();
        if (added == null)
            return "Double Track built no switches";
        if (added.Count != expectedTotal)
            return $"Double Track built {added.Count} switches, the drawing knows {expectedTotal} (an edited target.csv?)";
        var ordered = registry.OrderedJunctions;
        var index = new Dictionary<Junction, int>();
        for (var i = 0; i < ordered.Length; i++)
            index[ordered[i]] = i;
        foreach (var (key, id, pos) in wanted)
        {
            var want = new Vector3(pos[0], pos[1], pos[2]);
            Junction? best = null;
            var bestGap = float.MaxValue;
            foreach (var j in added)
            {
                if (j.junctionData.junctionIdLong != id)
                    continue;
                var gap = Vector3.Distance(MapOf(registry, j.transform.position), want);
                if (gap < bestGap)
                {
                    best = j;
                    bestGap = gap;
                }
            }
            if (best == null || bestGap > 3f)
                return $"switch {key} not found{(best != null ? $" (nearest {bestGap:F1} m away)" : "")}";
            if (!index.TryGetValue(best, out var at))
                return $"switch {key} is not among the ordered junctions";
            if (Junctions.TryGetValue(key, out var had))
            {
                if (had != best)
                    return $"switch {key} found twice";
                continue;
            }
            if (Junctions.ContainsValue(best))
                return $"switch {key} is another key's too";
            Junctions[key] = best;
            Indices[key] = at;
        }
        return null;
    }

    /// <summary>A found switch's data for a touch cell: its index, its place, shown on the map.</summary>
    public Junction.JunctionData DataFor(string key)
    {
        var j = Junctions[key];
        var data = j.junctionData;
        data.junctionIndex = Indices[key];
        data.position = MapOf(registry!, j.transform.position);
        data.excludeFromJunctionMap = false;
        data.isValid = true;
        data.linkedJunctions = new List<int>();
        return data;
    }
}
