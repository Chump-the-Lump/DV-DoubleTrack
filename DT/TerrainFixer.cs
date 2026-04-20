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

        // 1. Primary Hook (Background Thread) - Start early
        var loadTarget = AccessTools.Method(AccessTools.Inner(typeof(TerrainGrid), "GridCell"), "OnLoadingFinished");
        harmony.Patch(loadTarget, new HarmonyMethod(typeof(PersistentTerrainManager), nameof(OnLoadingFinished_Prefix)));

        // 2. Gatekeeper Hook (Main Thread) - Ensure visual perfection
        var displayTarget = AccessTools.Method(AccessTools.Inner(typeof(TerrainGrid), "GridCell"), "DisplayTerrain");
        harmony.Patch(displayTarget, new HarmonyMethod(typeof(PersistentTerrainManager), nameof(DisplayTerrain_Prefix)));

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
    
    private static bool DisplayTerrain_Prefix(object __instance, Vector3 worldPosition)
    {
        Vector2Int coord = (Vector2Int)AccessTools.Field(__instance.GetType(), "coord").GetValue(__instance);
    
        // If we already finished this tile, let the game display it
        if (ProcessedTiles.Contains(coord)) return true;

        // If it's currently being calculated in the background, we have to block 
        // the main thread for a moment to prevent the "Mountain Flash"
        if (BusyTiles.Contains(coord))
        {
            // This is rare due to our optimization, but it prevents the visual bug
            return false; 
        }

        // Capture the info struct and run the patch
        var infoField = AccessTools.Field(__instance.GetType(), "terrainInfo");
        TerrainInfo info = (TerrainInfo)infoField.GetValue(__instance);

        if (info.terrainData != null)
        {
            // We call this synchronously here to ENSURE the first frame is flat
            ProcessTileSync(__instance, info, coord);
        }

        return true;
    }

    private static void ProcessTileSync(object gridCell, TerrainInfo info, Vector2Int coord)
{
    // 1. Initial Validation
    if (info.terrainData == null || TerrainGrid.Instance == null) return;

    try 
    {
        TerrainData data = info.terrainData;
        
        // --- STEP 1: CAPTURE DATA (Main Thread) ---
        int res = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, res, res);
        float[,] originalHeights = (float[,])heights.Clone();
        Vector3 tileSize = data.size;
        
        // Capture positions using Tile-Relative math for World Shift immunity
        Vector3 capturedTileWorldPos = TerrainGrid.Instance.ToWorldPosition(coord);
        Vector3 worldShift = WorldMover.currentMove;

        Bounds tileBounds = new Bounds(capturedTileWorldPos + tileSize * 0.5f, tileSize);
        tileBounds.Expand(BRUSH_SIZE * 2f);

        var relevantTracks = new List<Vector3[]>();
        
        if (AllTracksPatch.AddedTracks != null)
        {
            foreach (var track in AllTracksPatch.AddedTracks)
            {
                if (track == null) continue;

                EquiPointSet pointSet = track.GetKinkedPointSet();
                if (pointSet?.points == null) continue;

                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                
                // Convert track points to Tile-Relative space immediately
                Vector3[] relativePoints = new Vector3[pointSet.points.Length];
                for (int i = 0; i < pointSet.points.Length; i++) 
                {
                    Vector3 worldPt = (Vector3)pointSet.points[i].position + worldShift;
                    relativePoints[i] = worldPt - capturedTileWorldPos;

                    min = Vector3.Min(min, worldPt);
                    max = Vector3.Max(max, worldPt);
                }

                Bounds trackBounds = new Bounds();
                trackBounds.SetMinMax(min, max);
                trackBounds.Expand(BRUSH_SIZE);

                if (tileBounds.Intersects(trackBounds)) 
                    relevantTracks.Add(relativePoints);
            }
        }
        
        // --- STEP 2: CALCULATE (Main Thread - Synchronous) ---
        // Because of Grid Clamping, this now takes milliseconds instead of minutes.
        bool modified = false;
        if (relevantTracks.Count > 0)
        {
            // We pass Vector3.zero because we are using Tile-Relative points.
            modified = CalculateFlattening(heights, originalHeights, relevantTracks, Vector3.zero, tileSize, res);
        }

        // --- STEP 3: APPLY ---
        if (data != null && data)
        {
            if (modified)
            {
                data.SetHeights(0, 0, heights);
                data.SyncHeightmap();
            }
            // Mark as processed so the DisplayTerrain_Prefix doesn't run this again.
            ProcessedTiles.Add(coord);
        }
    }
    catch (Exception e)
    {
        UnityEngine.Debug.LogError($"[DoubleTrack] Synchronous flattening failed for {coord}");
        UnityEngine.Debug.LogException(e);
    }
}
    
    private static async void ProcessTileAsync(object gridCell, TerrainInfo info, Vector2Int coord)
    {
        // 1. Fundamental Validation
        if (info.terrainData == null || TerrainGrid.Instance == null) 
        {
            return;
        }

        bool success = false;
        try 
        {
            BusyTiles.Add(coord);
            TerrainData data = info.terrainData;
            
            // --- STEP 1: CAPTURE DATA (Main Thread) ---
            int res = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, res, res);
            float[,] originalHeights = (float[,])heights.Clone();
            Vector3 tileSize = data.size;
            
            // Capture the tile's world position and current world shift immediately.
            // By calculating relative offsets now, we become immune to world shifts during the Task.
            Vector3 capturedTileWorldPos = TerrainGrid.Instance.ToWorldPosition(coord);
            Vector3 worldShift = WorldMover.currentMove;

            Bounds tileBounds = new Bounds(capturedTileWorldPos + tileSize * 0.5f, tileSize);
            tileBounds.Expand(BRUSH_SIZE * 2f);

            var relevantTracks = new List<Vector3[]>();
            
            if (AllTracksPatch.AddedTracks != null)
            {
                foreach (var track in AllTracksPatch.AddedTracks)
                {
                    if (track == null) continue;

                    EquiPointSet pointSet = track.GetKinkedPointSet();
                    if (pointSet?.points == null) continue;

                    Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    
                    // Convert track points to Tile-Relative space immediately.
                    // The formula (Point + CurrentShift) - TilePos is origin-independent.
                    Vector3[] relativePoints = new Vector3[pointSet.points.Length];

                    for (int i = 0; i < pointSet.points.Length; i++) 
                    {
                        Vector3 worldPt = (Vector3)pointSet.points[i].position + worldShift;
                        relativePoints[i] = worldPt - capturedTileWorldPos;

                        // Track bounds for intersection check
                        min = Vector3.Min(min, worldPt);
                        max = Vector3.Max(max, worldPt);
                    }

                    Bounds trackBounds = new Bounds();
                    trackBounds.SetMinMax(min, max);
                    trackBounds.Expand(BRUSH_SIZE);

                    if (tileBounds.Intersects(trackBounds)) 
                    {
                        relevantTracks.Add(relativePoints);
                    }
                }
            }
            
            // --- STEP 2: CALCULATE (Background Thread) ---
            bool modified = false;
            if (relevantTracks.Count > 0)
            {
                // We pass Vector3.zero as the tilePos because relevantTracks are already relative to the tile origin.
                modified = await Task.Run(() => CalculateFlattening(heights, originalHeights, relevantTracks, Vector3.zero, tileSize, res));
            }

            // --- STEP 3: APPLY (Main Thread) ---
            if (data != null && data)
            {
                if (modified)
                {
                    data.SetHeights(0, 0, heights);
                    data.SyncHeightmap();
                }
                success = true;
            }
        }
        catch (Exception e)
        {
            // This will now catch the NullRef and log exactly where it happened
            UnityEngine.Debug.LogError($"[DoubleTrack] Flattening failed for {coord}. Instance status: TerrainGrid={TerrainGrid.Instance != null}, Tracks={AllTracksPatch.AddedTracks != null}");
            UnityEngine.Debug.LogException(e);
        }
        finally
        {
            // RESUME ORIGINAL LOGIC
            try 
            {
                // 1. Verify the GridCell still exists and has its wrapper
                // If the wrapper is null, the game has already called Unload() on this cell
                var wrapperField = AccessTools.Field(gridCell.GetType(), "wrapper");
                var currentWrapper = wrapperField?.GetValue(gridCell);

                if (gridCell != null && currentWrapper != null)
                {
                    _isResuming = true;
                    var resumeMethod = AccessTools.Method(gridCell.GetType(), "OnLoadingFinished");
                    if (resumeMethod != null)
                    {
                        // Resume the original game logic
                        resumeMethod.Invoke(gridCell, new object[] { info });
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[DoubleTrack] Aborting resumption for {coord}: GridCell was already unloaded.");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[DoubleTrack] Critical failure resuming GridCell for {coord}");
                UnityEngine.Debug.LogException(ex);
            }
            finally
            {
                _isResuming = false;
                BusyTiles.Remove(coord);
                if (success) ProcessedTiles.Add(coord);
            }

            // 2. Final visual refresh - only if everything is still valid
            if (success && TerrainGrid.Instance != null)
            {
                Terrain activeTile = TerrainGrid.Instance.GetLoadedTerrainAt(coord);
                if (activeTile != null && activeTile.terrainData != null) 
                    ForceRendererRefresh(activeTile, info.terrainData, info.terrainData.heightmapResolution);
            }
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