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
/// The route map (the switch dispatch page, item RouteMap), re-laid with Double Track's switches
/// on its main lines: the game's own tiles at 80% on a 40 x 40 grid, which makes room for the
/// passing loops and double-track sections. Drawn in routemap-normal.json.
///
/// - The page: as its render prefab is rendered, the stock tiles are hidden and the drawing's
///   placed, cloned from the stock ones; the stations' badges move to their cells. Tiles another
///   mod added (Interchange Yards' tie-ins) move with the stock cells they stand in.
/// - The touch cells (the switch data's world page): every stock switch moves to its cell in the
///   drawing, every cell another mod added moves with its stock cell, and Double Track's switches
///   are added, each naming its junction by its index in the registry's ordered junctions.
/// - The touch grid is the drawing's (40 x 40).
///
/// All of it or none: the map is re-laid only when the game's stock route map is the one the
/// drawing was made from and every Double Track switch the drawing shows is found. Otherwise the
/// stock map stays.
/// </summary>
internal static class RouteMapBook
{
#pragma warning disable CS0649 // filled from JSON
    internal sealed class Drawing
    {
        public sealed class Tile
        {
            public string sprite = "";
            public float x, y, rot;
            public float sx = 1f, sy = 1f;
            public int[]? stock;           // the stock cell an unchanged stock tile came from
        }

        public sealed class Switch
        {
            public string id = "";
            public float[] pos = new float[3];  // in the map frame
        }

        public string layout = "";
        public int grid;
        public List<Tile> tiles = new();
        public Dictionary<string, int[]> badges = new();
        public Dictionary<string, int[]> switches = new();
        public Dictionary<string, Switch> doubleTrack = new();
        public Dictionary<string, string> bookletOnly = new();   // Double Track's switches only the booklet shows
        public Dictionary<string, int[]> stockCells = new();
        public int[] cols = new int[0], rows = new int[0];

        /// <summary>Canvas units per cell.</summary>
        public float Cell => 1600f / grid;
    }
#pragma warning restore CS0649

    internal sealed class Plan
    {
        public RailTrackRegistryBase Registry = null!;
        public Drawing Spec = null!;
        public bool Relaid;
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
                spec = Book.LoadDrawing<Drawing>("routemap-normal.json");
            }
            return spec;
        }
    }

    /// <summary>Whether this world's route map is re-laid, decided once per world (registry).</summary>
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
            plan.Relaid = plan.Why == null;
        }
        catch (Exception e)
        {
            plan.Why = $"failed: {e}";
        }
        Book.Log(plan.Relaid
            ? $"route map re-laid: {plan.Found.Junctions.Count} Double Track switches drawn in"
            : $"route map left as it is: {plan.Why}");
        return plan;
    }

    private static string? Check(Plan p)
    {
        var d = p.Spec;
        var data = SingletonBehaviour<LevelInfo>.Instance?.junctionData;
        if (data == null || data.junctionPageDataWorld == null || data.junctionPageDataWorld.Length != 1)
            return "no stock route map data";
        // The stock data must be the drawing's.
        var cells = data.junctionPageDataWorld[0].junctionDataCoords;
        if (cells.Count != d.stockCells.Count)
            return $"the stock route map has {cells.Count} switches, the drawing {d.stockCells.Count}";
        foreach (var c in cells)
            if (!d.stockCells.TryGetValue(c.data.junctionIdLong ?? "", out var at) || at[0] != c.coord.x || at[1] != c.coord.y)
                return $"stock switch {c.data.junctionIdLong} at ({c.coord.x}, {c.coord.y}) is not where the drawing has it";
        // Double Track's switches: all of them, and each the map shows found by id and place.
        var expected = d.doubleTrack.Count + d.bookletOnly.Count;
        return p.Found.Find(p.Registry, expected, d.doubleTrack.Select(kv => (kv.Key, kv.Value.id, kv.Value.pos)));
    }

    /// <summary>This world's plan when its route map is re-laid, else null.</summary>
    private static Plan? Relaid()
    {
        var p = Decide();
        return p is { Relaid: true } ? p : null;
    }

    // ------------------------------------------------------------ the touch cells

    private static Vector2Int Remap(Drawing d, Vector2Int stock) =>
        stock.x >= 0 && stock.x < d.cols.Length && stock.y >= 0 && stock.y < d.rows.Length
            ? new Vector2Int(d.cols[stock.x], d.rows[stock.y])
            : stock;

    private static JunctionGeneratedDataRuntime.JunctionPageData[] WithWorld(JunctionGeneratedDataRuntime.JunctionPageData[] pages)
    {
        var p = Relaid();
        if (p == null || pages.Length == 0)
            return pages;
        var d = p.Spec;
        var page = pages[0];
        var coords = new List<JunctionGeneratedDataRuntime.DataCoord>();
        foreach (var c in page.junctionDataCoords)
        {
            var id = c.data.junctionIdLong ?? "";
            var moved = c;
            if (d.stockCells.TryGetValue(id, out var was) && was[0] == c.coord.x && was[1] == c.coord.y && d.switches.TryGetValue(id, out var now))
                moved.coord = new Vector2Int(now[0], now[1]);
            else
                moved.coord = Remap(d, c.coord);    // a cell another mod added: with its stock cell
            coords.Add(moved);
        }
        foreach (var key in p.Found.Junctions.Keys)
        {
            var cell = d.switches[key];
            coords.Add(new JunctionGeneratedDataRuntime.DataCoord { data = p.Found.DataFor(key), coord = new Vector2Int(cell[0], cell[1]) });
        }
        page.junctionDataCoords = coords;
        pages[0] = page;
        return pages;
    }

    // These patches run after Interchange Yards' (UMM id Interchanges), so that the cells and
    // tiles it adds move with the stock ones.
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
                if (junctionMapType == JunctionMap.JunctionMapType.World)
                    __result = WithWorld(__result);
                else if (junctionMapType == JunctionMap.JunctionMapType.All)
                {
                    var world = __instance.junctionPageDataWorld.Length;
                    __result = WithWorld(__result.Take(world).ToArray()).Concat(__result.Skip(world)).ToArray();
                }
            }
            catch (Exception e)
            {
                Book.Error($"route map data: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(JunctionGeneratedDataRuntime), nameof(JunctionGeneratedDataRuntime.GetMapGridSize))]
    private static class GridSize
    {
        private static void Postfix(JunctionMap.JunctionMapType junctionMapType, ref Vector2Int __result)
        {
            var p = junctionMapType == JunctionMap.JunctionMapType.World ? Relaid() : null;
            if (p != null)
                __result = new Vector2Int(p.Spec.grid, p.Spec.grid);
        }
    }

    // ------------------------------------------------------------ the page

    /// <summary>Marks a route map render the drawing is already in.</summary>
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
                if (!__instance.gameObject.name.StartsWith("RouteMapStaticRender", StringComparison.Ordinal) || __instance.GetComponent<Drawn>() != null)
                    return;
                var p = Relaid();
                if (p == null)
                    return;
                Relay(p.Spec, __instance);
                __instance.gameObject.AddComponent<Drawn>();
            }
            catch (Exception e)
            {
                Book.Error($"route map page: {e}");
            }
        }
    }

    private static Vector2Int StockCell(RectTransform t)
    {
        var p = t.anchoredPosition;
        return new Vector2Int(Mathf.FloorToInt((p.x + 800f) / 50f), Mathf.FloorToInt((p.y + 800f) / 50f));
    }

    private static string? SpriteOf(Transform t) => t.GetComponent<Image>()?.sprite?.name;

    private static string BadgeText(Transform badge)
    {
        var text = badge.Find("text-ID-Black")?.GetComponent<TMP_Text>()?.text ?? "";
        return text.Replace("\\n", "").Replace("\n", "").Trim();
    }

    private static void Relay(Drawing d, StaticPagesRender render)
    {
        var page = render.staticPages.FirstOrDefault();
        var stockPage = LevelInfo.GetWorldSpecificPrefab(LevelInfo.WorldSpecificPrefabs.RouteMapRender)?.GetComponent<StaticPagesRender>()?.staticPages.FirstOrDefault();
        if (page == null || stockPage == null)
        {
            Book.Warn("route map page: no page to draw on");
            return;
        }
        var wrapper = page.transform.Find(Book.WrapperPath);
        var stockWrapper = stockPage.transform.Find(Book.WrapperPath);
        var names = new[] { "Tracks", "Switches", "Stations" };
        var containers = names.ToDictionary(n => n, n => wrapper.Find(n));
        var stockNames = new HashSet<string>(names.SelectMany(n => stockWrapper.Find(n).Cast<Transform>().Select(t => $"{n}/{t.name}")));
        var k = d.Cell / 50f;
        Vector2 Center(int x, int y) => new(-800f + d.Cell * (x + 0.5f), -800f + d.Cell * (y + 0.5f));

        // Prototypes of each tile, from the stock page (and the booklet's, for the end tile).
        var prototypes = new Dictionary<string, GameObject>();
        foreach (var n in new[] { "Tracks", "Switches" })
            foreach (Transform t in containers[n])
            {
                var s = SpriteOf(t);
                if (s != null && !prototypes.ContainsKey(s) && stockNames.Contains($"{n}/{t.name}"))
                    prototypes[s] = t.gameObject;
            }
        var booklet = LevelInfo.GetWorldSpecificPrefab(LevelInfo.WorldSpecificPrefabs.SchematicMapRender)?.GetComponent<StaticPagesRender>();
        if (booklet != null)
            foreach (var img in booklet.staticPages.SelectMany(sp => sp.GetComponentsInChildren<Image>(true)))
                if (img.sprite != null && !prototypes.ContainsKey(img.sprite.name) && img.sprite.name.StartsWith("SM-Tracks", StringComparison.Ordinal))
                    prototypes[img.sprite.name] = img.gameObject;

        // The stock tiles hidden; stock cells another mod had already emptied are left empty.
        var emptied = new HashSet<Vector2Int>();
        var hidden = 0;
        var moved = 0;
        foreach (var n in new[] { "Tracks", "Switches" })
            foreach (Transform t in containers[n].Cast<Transform>().ToList())
            {
                if (stockNames.Contains($"{n}/{t.name}"))
                {
                    if (!t.gameObject.activeSelf)
                        emptied.Add(StockCell((RectTransform)t));
                    else
                        hidden++;
                    t.gameObject.SetActive(false);
                }
                else
                    moved += Move(d, (RectTransform)t, k) ? 1 : 0;
            }
        // The badges to their cells; another mod's badges with their stock cells.
        var badges = 0;
        foreach (Transform t in containers["Stations"].Cast<Transform>().ToList())
        {
            var rt = (RectTransform)t;
            if (stockNames.Contains($"Stations/{t.name}") && d.badges.TryGetValue(BadgeText(t), out var cell))
            {
                rt.anchoredPosition = Center(cell[0], cell[1]);
                rt.localScale = new Vector3(rt.localScale.x * k, rt.localScale.y * k, 1f);
                badges++;
            }
            else
                moved += Move(d, rt, k) ? 1 : 0;
        }
        // The drawing.
        var made = 0;
        var skipped = 0;
        foreach (var tile in d.tiles)
        {
            if (tile.stock != null && emptied.Contains(new Vector2Int(tile.stock[0], tile.stock[1])))
            {
                skipped++;
                continue;
            }
            if (!prototypes.TryGetValue(tile.sprite, out var proto))
            {
                Book.Warn($"route map page: no tile {tile.sprite} to copy");
                continue;
            }
            var into = tile.sprite == "SM-Switch" ? containers["Switches"] : containers["Tracks"];
            var go = Object.Instantiate(proto, into, false);
            go.name = Book.OwnPrefix + tile.sprite;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(50f, 50f);
            rt.anchoredPosition = new Vector2(tile.x, tile.y);
            rt.localRotation = Quaternion.Euler(0f, 0f, tile.rot);
            rt.localScale = new Vector3(tile.sx * k, tile.sy * k, 1f);
            go.SetActive(true);
            made++;
        }
        Book.Log($"route map page: {hidden} stock tiles hidden, {made} drawn, {badges} badges placed, {moved} other tiles moved, " +
                 $"{skipped} left out where another mod emptied the stock cell");
    }

    /// <summary>Another mod's tile, moved with the stock cell it stands in (its place within the cell scaled with it).</summary>
    private static bool Move(Drawing d, RectTransform t, float k)
    {
        var cell = StockCell(t);
        var to = Remap(d, cell);
        if (to == cell && (cell.x < 0 || cell.x >= d.cols.Length || cell.y < 0 || cell.y >= d.rows.Length))
            return false;
        var within = t.anchoredPosition - new Vector2(-800f + 50f * (cell.x + 0.5f), -800f + 50f * (cell.y + 0.5f));
        t.anchoredPosition = new Vector2(-800f + d.Cell * (to.x + 0.5f), -800f + d.Cell * (to.y + 0.5f)) + within * k;
        t.localScale = new Vector3(t.localScale.x * k, t.localScale.y * k, 1f);
        return true;
    }
}
