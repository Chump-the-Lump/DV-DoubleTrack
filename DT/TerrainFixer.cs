using DV.PointSet;
using DV.TerrainSystem;
using JBooth.MicroSplat;
using TerrainComposer2;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using HarmonyLib;
using DoubleTrack;

public static class PersistentTerrainManager
{
    public const int BRUSH_SIZE = 30;
    public const int SMOOTH_RADIUS = 2;
    public const float VERTICAL_OFFSET = -1f;

    private static readonly HashSet<Vector2Int> ProcessedTiles = new HashSet<Vector2Int>();
    private static readonly HashSet<Vector2Int> BusyTiles = new HashSet<Vector2Int>();

    public static void Initialize()
    {
        var harmony = new Harmony("com.persistent.terrain");
        
        // Target: Private method inside the nested GridCell class
        var target = AccessTools.Method(AccessTools.Inner(typeof(TerrainGrid), "GridCell"), "OnLoadingFinished");
        var prefix = new HarmonyMethod(typeof(PersistentTerrainManager), nameof(OnLoadingFinished_Prefix));
        
        harmony.Patch(target, prefix);

        UnityEngine.SceneManagement.SceneManager.sceneUnloaded += (s) => {
            ProcessedTiles.Clear();
            BusyTiles.Clear();
        };
    }

    // This Prefix intercepts the TerrainInfo BEFORE the GridCell sets its internal status to 'Loaded'
    private static bool _isResuming;
    private static bool OnLoadingFinished_Prefix(object __instance, TerrainInfo info)
    {
        // If we are currently resuming the method manually, skip the prefix logic
        if (_isResuming) return true;

        Vector2Int coord = (Vector2Int)AccessTools.Field(__instance.GetType(), "coord").GetValue(__instance);

        if (ProcessedTiles.Contains(coord) || BusyTiles.Contains(coord)) return true;

        ProcessTileAsync(__instance, info, coord);
        return false; // Stop the original call so we can finish our thread
    }

    private static async void ProcessTileAsync(object gridCell, TerrainInfo info, Vector2Int coord)
    {
        try 
        {
            BusyTiles.Add(coord);
            TerrainData data = info.terrainData;
            if (data == null) { BusyTiles.Remove(coord); return; }

            // --- STEP 1: CAPTURE DATA (Main Thread) ---
            int res = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, res, res);
            float[,] originalHeights = (float[,])heights.Clone();
            Vector3 tileSize = data.size;
            Vector3 tileWorldPos = TerrainGrid.Instance.ToWorldPosition(coord);
            Vector3 worldShift = WorldMover.currentMove;

            // Thread-safe copy of track segments
            var trackSegments = new List<Vector3[]>();
            foreach (var track in AllTracksPatch.AddedTracks)
            {
                EquiPointSet pointSet = track.GetKinkedPointSet();
                if (pointSet?.points == null) continue;
                Vector3[] points = new Vector3[pointSet.points.Length];
                for (int i = 0; i < pointSet.points.Length; i++) 
                    points[i] = (Vector3)pointSet.points[i].position + worldShift;
                trackSegments.Add(points);
            }
            
            bool modified = await Task.Run(() => CalculateFlattening(heights, originalHeights, trackSegments, tileWorldPos, tileSize, res));


            if (modified)
            {
                data.SetHeights(0, 0, heights);
                data.SyncHeightmap(); // Push CPU changes to the GPU texture
            }

            ProcessedTiles.Add(coord);
            BusyTiles.Remove(coord);
            
            AccessTools.Method(gridCell.GetType(), "OnLoadingFinished").Invoke(gridCell, new object[] { info });

            // If the tile was already active (Live update), force a mesh refresh
            Terrain activeTile = TerrainGrid.Instance.GetLoadedTerrainAt(coord);
            if (activeTile != null) ForceRendererRefresh(activeTile, data, res);
        }
        catch (Exception e)
        {
            Debug.LogError($"[DoubleTrack] Flattening failed for {coord}: {e}");
        }
        finally
        {
            _isResuming = true;
            // Resume original call without triggering the prefix again
            AccessTools.Method(gridCell.GetType(), "OnLoadingFinished").Invoke(gridCell, new object[] { info });
            _isResuming = false;
        
            BusyTiles.Remove(coord);
            ProcessedTiles.Add(coord);
        }
    }

    private static bool CalculateFlattening(float[,] heights, float[,] originalHeights, List<Vector3[]> tracks, Vector3 tilePos, Vector3 tileSize, int res)
{
    bool modified = false;
    float searchRadius = BRUSH_SIZE / 2f;
    float borderBuffer = searchRadius + 2f;
    float[,] localWeights = new float[res, res];

    foreach (var points in tracks)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector3 pA = points[i]; 
            Vector3 pB = points[i + 1];

            // --- SPATIAL PRUNING ---
            // Define the world-space box that this segment affects
            float minX = Mathf.Min(pA.x, pB.x) - borderBuffer;
            float maxX = Mathf.Max(pA.x, pB.x) + borderBuffer;
            float minZ = Mathf.Min(pA.z, pB.z) - borderBuffer;
            float maxZ = Mathf.Max(pA.z, pB.z) + borderBuffer;

            // If the segment's impact box doesn't touch this tile at all, skip it immediately
            if (maxX < tilePos.x || minX > tilePos.x + tileSize.x || 
                maxZ < tilePos.z || minZ > tilePos.z + tileSize.z) continue;

            // --- GRID CLAMPING ---
            // Convert world bounds to heightmap array indices (0 to res-1)
            int xStart = Mathf.Max(0, Mathf.FloorToInt(((minX - tilePos.x) / tileSize.x) * (res - 1)));
            int xEnd = Mathf.Min(res - 1, Mathf.CeilToInt(((maxX - tilePos.x) / tileSize.x) * (res - 1)));
            int zStart = Mathf.Max(0, Mathf.FloorToInt(((minZ - tilePos.z) / tileSize.z) * (res - 1)));
            int zEnd = Mathf.Min(res - 1, Mathf.CeilToInt(((maxZ - tilePos.z) / tileSize.z) * (res - 1)));

            // ONLY loop through the pixels inside the segment's bounding box
            for (int z = zStart; z <= zEnd; z++)
            {
                for (int x = xStart; x <= xEnd; x++)
                {
                    Vector3 pixelPos = new Vector3(
                        tilePos.x + (x / (float)(res - 1)) * tileSize.x, 
                        0, 
                        tilePos.z + (z / (float)(res - 1)) * tileSize.z
                    );

                    float dist = GetDistanceToSegment(pixelPos, pA, pB);
                    if (dist > searchRadius) continue;

                    float weight = (dist <= SMOOTH_RADIUS) ? 1.0f : 
                                   Mathf.SmoothStep(1, 0, (dist - SMOOTH_RADIUS) / (searchRadius - SMOOTH_RADIUS));
                    
                    float targetH = (GetLerpedY(pixelPos, pA, pB) + VERTICAL_OFFSET - tilePos.y) / tileSize.y;

                    if (weight > localWeights[z, x])
                    {
                        localWeights[z, x] = weight;
                        heights[z, x] = Mathf.Lerp(originalHeights[z, x], targetH, weight);
                        modified = true;
                    }
                }
            }
        }
    }
    return modified;
}

    private static void ForceRendererRefresh(Terrain tile, TerrainData data, int res)
    {
        // Use the correct property names from the decompile
        float prevError = tile.heightmapPixelError;
        tile.heightmapPixelError = 1.0f; 
        
        data.DirtyHeightmapRegion(new RectInt(0, 0, res, res), TerrainHeightmapSyncControl.HeightAndLod);
        
        // Force Renderer Reconstruction
        tile.drawHeightmap = false;

        // TC2 Sync
        if (TC_Area2D.current != null && TC_Compute.instance != null)
        {
            TCUnityTerrain tc = TC_Area2D.current.currentTCUnityTerrain;
            if (tc != null && tc.terrain == tile)
            {
                TC_Compute.instance.RunTerrainTexFromTerrainData(data, ref tc.rtHeight);
                TC_Generate.instance.GenerateHeight(tc, true, new Rect(0, 0, 1, 1), false);
            }
        }

        tile.drawHeightmap = true;
        tile.heightmapPixelError = prevError;
        tile.Flush();
    }

    // Thread-safe math helpers
    private static float GetDistanceToSegment(Vector3 p, Vector3 a, Vector3 b) {
        Vector2 p2 = new Vector2(p.x, p.z), a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z);
        Vector2 ab = b2 - a2;
        float t = Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / Vector2.Dot(ab, ab));
        
        float dx = p2.x - (a2.x + t * ab.x);
        float dy = p2.y - (a2.y + t * ab.y);
        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        
        return dist;
    }
    private static float GetLerpedY(Vector3 p, Vector3 a, Vector3 b) {
        Vector2 p2 = new Vector2(p.x, p.z), a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z);
        Vector2 ab = b2 - a2;
        float t = Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / Vector2.Dot(ab, ab));
        return Mathf.Lerp(a.y, b.y, t);
    }
}