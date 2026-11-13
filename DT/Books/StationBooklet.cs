using DV.Items;
using DV.RenderTextureSystem.BookletRender;
using DV.Utils;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DoubleTrack.Books;

/// <summary>
/// Double Track's switches in the station booklet (item MapSchematic), for the switches inside a
/// yard that has a page. Drawn in booklet-normal.json.
///
/// - Stock pages whose yards Double Track changes get its switches: in the render, the stock
///   track tiles of the changed cells are hidden and the new ones drawn, cloned from the
///   booklet's own; a badge may move (Steel Mill B's track 1, to the stub that now carries its
///   name). In the switch data, the page gets a touch cell for each.
/// - The hump yard gets a page of its own, Steel Mill H, after Steel Mill B: a copy of that page
///   with the hump yard drawn on it. It goes into the render's pages, the switch data's and the
///   Steel Mill chapter, and every later page (and chapter) moves one on, Interchange Yards' among
///   them: these patches run after its own.
///
/// All or none, as the route map: only when every switch the pages show is found and the
/// booklet's pages are the ones drawn on.
/// </summary>
internal static class StationBooklet
{
#pragma warning disable CS0649 // filled from JSON
    internal sealed class Drawing
    {
        public sealed class Tile
        {
            public string sprite = "";
            public float x, y, rot;
            public float sx = 1f, sy = 1f;
        }

        public sealed class Cell
        {
            public string id = "";
            public float[] pos = new float[3];
            public int[] cell = new int[2];
        }

        public sealed class Move
        {
            public float[] from = new float[2], to = new float[2];
        }

        public sealed class Page
        {
            public int index;
            public string name = "";
            public List<int[]> clear = new();
            public List<Tile> tiles = new();
            public List<Move> moves = new();
            public Dictionary<string, Cell> switches = new();
        }

        public sealed class Badge
        {
            public float x, y;
            public string text = "", type = "";
        }

        public sealed class Insert
        {
            public int after, copyOf;
            public string yardId = "", yardType = "";
            public List<Tile> tiles = new();
            public List<Badge> badges = new();
            public Dictionary<string, Cell> switches = new();
        }

        public string layout = "";
        public int doubleTrackTotal;
        public List<Page> pages = new();
        public Insert insert = new();
    }
#pragma warning restore CS0649

    internal sealed class Plan
    {
        public RailTrackRegistryBase Registry = null!;
        public Drawing Spec = null!;
        public bool Applied;
        public string? Why;
        public readonly BookSwitches Found = new();
    }

    private static Drawing? spec;
    private static bool loaded;
    private static Plan? plan;

    internal static Drawing? Spec
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                spec = Book.LoadDrawing<Drawing>("booklet-normal.json");
            }
            return spec;
        }
    }

    /// <summary>Whether this world's booklet has Double Track's switches, decided once per world (registry).</summary>
    internal static Plan? Decide()
    {
        var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
        if (registry == null || Spec == null)
            return null;
        if (plan != null && plan.Registry == registry)
            return plan;
        plan = new Plan { Registry = registry, Spec = Spec };
        try
        {
            plan.Why = Check(plan);
            plan.Applied = plan.Why == null;
        }
        catch (Exception e)
        {
            plan.Why = $"failed: {e}";
        }
        Book.Log(plan.Applied
            ? $"booklet: {plan.Found.Junctions.Count} Double Track switches drawn in"
            : $"booklet left as it is: {plan.Why}");
        return plan;
    }

    private static string? Check(Plan p)
    {
        var d = p.Spec;
        var stations = SingletonBehaviour<LevelInfo>.Instance?.junctionData?.junctionPageDataStations;
        if (stations == null)
            return "no stock booklet data";
        // The pages drawn on must be where the drawing has them.
        var render = LevelInfo.GetWorldSpecificPrefab(LevelInfo.WorldSpecificPrefabs.SchematicMapRender)?.GetComponent<StaticPagesRender>();
        if (render == null || render.staticPages.Count != stations.Length)
            return "the booklet's render and switch data disagree";
        foreach (var pg in d.pages)
            if (pg.index >= render.staticPages.Count || render.staticPages[pg.index].gameObject.name != pg.name)
                return $"page {pg.index} is not {pg.name}";
        if (d.insert.after >= stations.Length || d.insert.copyOf >= render.staticPages.Count)
            return "no page to copy for the hump yard";
        var wanted = d.pages.SelectMany(pg => pg.switches).Concat(d.insert.switches)
            .Select(kv => (kv.Key, kv.Value.id, kv.Value.pos));
        return p.Found.Find(p.Registry, d.doubleTrackTotal, wanted);
    }

    /// <summary>This world's plan when its booklet has Double Track's switches, else null.</summary>
    private static Plan? Applied()
    {
        var p = Decide();
        return p is { Applied: true } ? p : null;
    }

    // ------------------------------------------------------------ the switch data

    private static JunctionGeneratedDataRuntime.DataCoord Coord(Plan p, string key, Drawing.Cell c) =>
        new() { data = p.Found.DataFor(key), coord = new Vector2Int(c.cell[0], c.cell[1]) };

    private static JunctionGeneratedDataRuntime.JunctionPageData[] WithStations(JunctionGeneratedDataRuntime.JunctionPageData[] pages)
    {
        var p = Applied();
        if (p == null)
            return pages;
        var d = p.Spec;
        var list = pages.ToList();
        foreach (var pg in d.pages)
        {
            if (pg.index >= list.Count)
                continue;
            var page = list[pg.index];
            page.junctionDataCoords = page.junctionDataCoords.Concat(pg.switches.Select(kv => Coord(p, kv.Key, kv.Value))).ToList();
            list[pg.index] = page;
        }
        var ins = d.insert;
        list.Insert(ins.after + 1, new JunctionGeneratedDataRuntime.JunctionPageData
        {
            categoryId = list[ins.after].categoryId,
            junctionDataCoords = ins.switches.Select(kv => Coord(p, kv.Key, kv.Value)).ToList(),
        });
        for (var i = 0; i < list.Count; i++)
        {
            var page = list[i];
            page.pageNumber = i;
            list[i] = page;
        }
        return list.ToArray();
    }

    // These patches run after Interchange Yards' (UMM id Interchanges), so that its pages and
    // chapters, added after the stock ones, move one on with the rest.
    [HarmonyPatch(typeof(JunctionGeneratedDataRuntime), nameof(JunctionGeneratedDataRuntime.GetPageData))]
    [HarmonyAfter("Interchanges")]
    private static class PageData
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(JunctionGeneratedDataRuntime __instance, JunctionMap.JunctionMapType junctionMapType,
                                    ref JunctionGeneratedDataRuntime.JunctionPageData[]? __result)
        {
            if (__result == null)
                return;
            try
            {
                if (junctionMapType == JunctionMap.JunctionMapType.Station)
                    __result = WithStations(__result);
                else if (junctionMapType == JunctionMap.JunctionMapType.All)
                {
                    var world = __instance.junctionPageDataWorld.Length;
                    __result = __result.Take(world).Concat(WithStations(__result.Skip(world).ToArray())).ToArray();
                }
            }
            catch (Exception e)
            {
                Book.Error($"booklet data: {e}");
            }
        }
    }

    // ------------------------------------------------------------ the chapters

    /// <summary>Marks a booklet whose chapters already have the hump yard's page.</summary>
    internal sealed class Chaptered : MonoBehaviour
    {
    }

    /// <summary>The hump yard's page in the Steel Mill chapter; every later chapter a page on.</summary>
    [HarmonyPatch(typeof(ChapterSelector), "Start")]
    [HarmonyAfter("Interchanges")]
    private static class Chapters
    {
        [HarmonyPriority(Priority.Low)]
        private static void Prefix(ChapterSelector __instance, List<ChapterSelector.PageBookChapter>? ___allChapters)
        {
            try
            {
                var map = __instance.GetComponent<JunctionMap>();
                if (map == null || map.junctionMapType != JunctionMap.JunctionMapType.Station || ___allChapters == null ||
                    __instance.GetComponent<Chaptered>() != null)
                    return;
                var p = Applied();
                if (p == null)
                    return;
                var after = p.Spec.insert.after;
                var holder = ___allChapters.FindIndex(c => c.startingPage <= after && after <= c.endingPage);
                if (holder < 0)
                {
                    Book.Warn($"booklet: no chapter holds page {after}");
                    return;
                }
                for (var i = 0; i < ___allChapters.Count; i++)
                {
                    var c = ___allChapters[i];
                    if (i == holder)
                        c.endingPage++;
                    else if (c.startingPage > after)
                    {
                        c.startingPage++;
                        c.endingPage++;
                    }
                    ___allChapters[i] = c;
                }
                __instance.gameObject.AddComponent<Chaptered>();
            }
            catch (Exception e)
            {
                Book.Error($"booklet chapters: {e}");
            }
        }
    }

    // ------------------------------------------------------------ the pages

    /// <summary>Marks a booklet render the drawing is already in.</summary>
    internal sealed class Drawn : MonoBehaviour
    {
    }

    [HarmonyPatch(typeof(StaticPagesRender), "GetStaticTemplatePaperData")]
    [HarmonyAfter("Interchanges")]
    private static class Render
    {
        [HarmonyPriority(Priority.Low)]
        private static void Prefix(StaticPagesRender __instance)
        {
            try
            {
                if (!__instance.gameObject.name.StartsWith("MapSchematicStaticRender", StringComparison.Ordinal) ||
                    __instance.GetComponent<Drawn>() != null)
                    return;
                var p = Applied();
                if (p == null)
                    return;
                Draw(p.Spec, __instance);
                __instance.gameObject.AddComponent<Drawn>();
            }
            catch (Exception e)
            {
                Book.Error($"booklet pages: {e}");
            }
        }
    }

    private static readonly HashSet<string> TrackSprites = new()
    {
        "SM-TracksStraight", "SM-TracksDiagonal", "SM-TracksCrossover", "SM-TracksSwitchDivergingL", "SM-TracksSwitchDivergingR",
        "SM-TracksSwitchSpread", "SM-TracksExit", "SM-TracksEnd", "SM-Switch",
    };

    private static string? SpriteOf(Transform t) => t.GetComponent<Image>()?.sprite?.name;

    private static Vector2Int CellOf(RectTransform t) =>
        new(Mathf.RoundToInt(t.anchoredPosition.x / 50f), Mathf.RoundToInt(t.anchoredPosition.y / 50f));

    private static void Draw(Drawing d, StaticPagesRender render)
    {
        var pages = render.staticPages;
        // Prototypes: a stock tile of each sprite, and a stock track badge.
        var prototypes = new Dictionary<string, GameObject>();
        GameObject? badge = null;
        foreach (var page in pages)
        {
            var smap = page.transform.Find(Book.WrapperPath + "/SMap");
            if (smap == null)
                continue;
            foreach (Transform t in smap)
            {
                var s = SpriteOf(t);
                if (s == null)
                    continue;
                if (TrackSprites.Contains(s) && !prototypes.ContainsKey(s))
                    prototypes[s] = t.gameObject;
                if (s == "SM-ID" && badge == null && t.Find("Type-S") != null)
                    badge = t.gameObject;
            }
        }
        // The booklet has no crossover tile of its own: the route map's.
        var route = LevelInfo.GetWorldSpecificPrefab(LevelInfo.WorldSpecificPrefabs.RouteMapRender)?.GetComponent<StaticPagesRender>();
        if (route != null)
            foreach (var img in route.staticPages.SelectMany(sp => sp.GetComponentsInChildren<Image>(true)))
                if (img.sprite != null && TrackSprites.Contains(img.sprite.name) && !prototypes.ContainsKey(img.sprite.name))
                    prototypes[img.sprite.name] = img.gameObject;
        var made = 0;
        var hidden = 0;
        foreach (var pg in d.pages)
        {
            var smap = pages[pg.index].transform.Find(Book.WrapperPath + "/SMap");
            var clear = new HashSet<Vector2Int>(pg.clear.Select(c => new Vector2Int(c[0], c[1])));
            foreach (Transform t in smap.Cast<Transform>().ToList())
            {
                var s = SpriteOf(t);
                if (s != null && TrackSprites.Contains(s) && t.gameObject.activeSelf && clear.Contains(CellOf((RectTransform)t)))
                {
                    t.gameObject.SetActive(false);
                    hidden++;
                }
            }
            foreach (var m in pg.moves)
            {
                var from = new Vector2(m.from[0], m.from[1]);
                var t = smap.Cast<Transform>().FirstOrDefault(c => SpriteOf(c) == "SM-ID" && (((RectTransform)c).anchoredPosition - from).sqrMagnitude < 1f);
                if (t != null)
                    ((RectTransform)t).anchoredPosition = new Vector2(m.to[0], m.to[1]);
                else
                    Book.Warn($"booklet {pg.name}: no badge at ({from.x}, {from.y}) to move");
            }
            made += Place(smap, pg.tiles, prototypes);
        }
        // The hump yard's page: a copy of the page it follows, renamed, its schematic redrawn.
        var ins = d.insert;
        var source = pages[ins.copyOf];
        var copy = Object.Instantiate(source.gameObject, source.transform.parent, false);
        copy.name = $"[{SourceStation(source)}-{ins.yardId}]";
        var wrapper = copy.transform.Find(Book.WrapperPath);
        SetText(wrapper, "Text-YardID", ins.yardId);
        SetText(wrapper, "Text-YardType", ins.yardType);
        var csmap = wrapper.Find("SMap");
        foreach (Transform t in csmap.Cast<Transform>().ToList())
        {
            var s = SpriteOf(t);
            if (s != "SM-Compass" && s != "SM-CompassOffice")
                Object.DestroyImmediate(t.gameObject);
        }
        made += Place(csmap, ins.tiles, prototypes);
        if (badge != null)
            foreach (var b in ins.badges)
            {
                var go = Object.Instantiate(badge, csmap, false);
                go.name = Book.OwnPrefix + "SM-ID";
                ((RectTransform)go.transform).anchoredPosition = new Vector2(b.x, b.y);
                foreach (Transform child in go.transform)
                {
                    if (child.name.StartsWith("Type-", StringComparison.Ordinal))
                        child.gameObject.SetActive(child.name == "Type-" + b.type);
                    else if (child.name == "text-ID-White")
                    {
                        child.gameObject.SetActive(true);
                        SetTextOn(child, b.text);
                    }
                    else if (child.name.StartsWith("text-", StringComparison.Ordinal))
                        child.gameObject.SetActive(false);
                }
                go.SetActive(true);
            }
        else
            Book.Warn("booklet: no track badge to copy");
        pages.Insert(ins.after + 1, copy.GetComponent<StaticPageTemplatePaper>());
        Book.Log($"booklet pages: {hidden} stock tiles hidden, {made} drawn on {d.pages.Count} pages; page {copy.name} added at {ins.after + 1} of {pages.Count}");
    }

    private static string SourceStation(StaticPageTemplatePaper page)
    {
        var n = page.gameObject.name;
        var dash = n.LastIndexOf('-');
        return n.StartsWith("[", StringComparison.Ordinal) && dash > 1 ? n.Substring(1, dash - 1) : "SM";
    }

    private static int Place(Transform smap, IEnumerable<Drawing.Tile> tiles, Dictionary<string, GameObject> prototypes)
    {
        var made = 0;
        foreach (var tile in tiles)
        {
            if (!prototypes.TryGetValue(tile.sprite, out var proto))
            {
                Book.Warn($"booklet: no tile {tile.sprite} to copy");
                continue;
            }
            var go = Object.Instantiate(proto, smap, false);
            go.name = Book.OwnPrefix + tile.sprite;
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = new Vector2(tile.x, tile.y);
            rt.localRotation = Quaternion.Euler(0f, 0f, tile.rot);
            rt.localScale = new Vector3(tile.sx, tile.sy, 1f);
            // The red markers lie under the track, as on the stock pages.
            if (tile.sprite == "SM-Switch")
                go.transform.SetAsFirstSibling();
            go.SetActive(true);
            made++;
        }
        return made;
    }

    private static void SetText(Transform wrapper, string name, string text)
    {
        var t = wrapper.Find(name);
        if (t != null)
            SetTextOn(t, text);
    }

    /// <summary>Sets a text, taking off the localization that would put the stock one back.</summary>
    private static void SetTextOn(Transform t, string text)
    {
        foreach (var l in t.GetComponents<DV.Localization.Localize>())
            Object.DestroyImmediate(l);
        foreach (var l in t.GetComponents<I2.Loc.Localize>())
            Object.DestroyImmediate(l);
        var tmp = t.GetComponent<TMP_Text>();
        if (tmp != null)
            tmp.text = text;
    }
}
