using DV.PointSet;
using DV.TerrainSystem;
using JBooth.MicroSplat;
using TerrainComposer2;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Collections;
using HarmonyLib;
using DoubleTrack;

public static class PersistentTerrainManager
{
    public const int BRUSH_SIZE = 30;
    public const int SMOOTH_RADIUS = 2;
    public const float VERTICAL_OFFSET = -0.75f;
    
    public struct HeightDelta
    {
        public ushort x;
        public ushort z;
        public float height;

        public HeightDelta(int x, int z, float height)
        {
            this.x = (ushort)x;
            this.z = (ushort)z;
            this.height = height;
        }
    }

    private static Dictionary<Vector2Int, List<HeightDelta>> CompressedHeightCache = new Dictionary<Vector2Int, List<HeightDelta>>();

    public static void Initialize()
    {
        TerrainGrid.Initialized += ()=>TerrainGrid.Instance.StartCoroutine(PreLoadTerrain());
        
        // Listen to the static delegate provided by the game's TerrainGrid
        TerrainGrid.TerrainDataLoaded += (UnityEngine.TerrainData data, Vector2Int coord) =>
        {
            FindCell(coord);
        };
    }
    
    private static IEnumerator PreLoadTerrain()
    {
        Debug.Log("[DoubleTrack] Preloading Terrain");
        CompressedHeightCache = TerrainCacheCompressor.LoadCompressedDeltas(TrackPlacerEntry.CACHE_PATH,TrackPlacerEntry.TARGET_PATH);
        if(CompressedHeightCache.Count > 0) yield break;
        Debug.LogWarning("[DoubleTrack] Failed to find cache data, recalculating terrain height matrix (This may take a while)");
        
        
        yield return new WaitUntil(()=>AllTracksPatch.trackCounter != 0);
        
        var gridField = AccessTools.Field(typeof(TerrainGrid), "grid").GetValue(TerrainGrid.Instance) as TerrainGrid.GridCell[];

        int celsToCache = 0;
        foreach(TerrainGrid.GridCell cell in gridField)
        {
            List<Vector3[]> closeTiles = IsTileNearTrack(cell.coord);
            if(closeTiles.Count > 0)
            {
                cell.Load();
                cell.wrapper.LoadingFinished += PreLoad;
                celsToCache++;
                
                void PreLoad(TerrainInfo info)
                {
                    cell.wrapper.LoadingFinished -= PreLoad;
                    ProcessTileSync(cell,closeTiles);
                    cell.Unload();
                }
            }
        }
        
        yield return new WaitUntil(()=>CompressedHeightCache.Count==celsToCache);
        TerrainCacheCompressor.SaveCompressedDeltas(TrackPlacerEntry.CACHE_PATH,TrackPlacerEntry.TARGET_PATH,CompressedHeightCache);
        Debug.Log("[DoubleTrack] Cached "+CompressedHeightCache.Count+" tiles");
        
    }
    

    private static void FindCell(Vector2Int coord)
    {
        // Find the active cell that just finished loading
        var gridField = AccessTools.Field(typeof(TerrainGrid), "grid").GetValue(TerrainGrid.Instance) as TerrainGrid.GridCell[];
        if (gridField == null) return;
            
        // Find the matching coordinate row
        foreach(var cell in gridField)
        {
            if(cell.coord == coord)
            {
                LoadFromCache(cell);
                return;
            }
        }
    }

    private static void LoadFromCache(TerrainGrid.GridCell gridCell)
    {
        TerrainInfo info = gridCell.terrainInfo;
        if (info.terrainData == null || TerrainGrid.Instance == null) return;

        Vector2Int coord = gridCell.coord;
        TerrainData data = info.terrainData;
        int res = data.heightmapResolution;

        try
        {
            // --- FAST PATH: CACHED LAYOUT LOOKUP ---
            if (CompressedHeightCache.TryGetValue(coord, out List<HeightDelta> cachedDeltas))
            {
                if (cachedDeltas.Count > 0)
                {
                    float[,] heights = data.GetHeights(0, 0, res, res);
                    foreach (var delta in cachedDeltas)
                    {
                        heights[delta.z, delta.x] = delta.height;
                    }

                    data.SetHeights(0, 0, heights);

                    Terrain gridCellTerrain = gridCell.terrain;
                    if (gridCellTerrain != null)
                    {
                        FinalizeTile(coord, gridCellTerrain);
                        var collider = gridCellTerrain.GetComponent<TerrainCollider>();
                        if (collider != null) collider.terrainData = data;
                    }

                    data.SyncHeightmap();
                }
                return;
            }
        }catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[DoubleTrack] CacheLoad failed for {coord}");
            UnityEngine.Debug.LogException(e);
        }
        /*
        RelevantTracks = IsTileNearTrack(coord);
        if(RelevantTracks.Count == 0) return;
        Debug.LogWarning($"[DoubleTrack] Failed to find cached tile at {coord}");
        ProcessTileSync(gridCell);
        */
    }


    private static void ProcessTileSync(TerrainGrid.GridCell gridCell,List<Vector3[]> relevantTracks)
    {
        TerrainInfo info = gridCell.terrainInfo;
        if (info.terrainData == null || TerrainGrid.Instance == null) return;

        Vector2Int coord = gridCell.coord;
        TerrainData data = info.terrainData;
        int res = data.heightmapResolution;
        
        try{
            // --- SLOW PATH: FIRST-TIME COMPUTATION ---
            float[,] currentHeights = data.GetHeights(0, 0, res, res);
            float[,] originalHeights = (float[,])currentHeights.Clone();
            Vector3 tileSize = data.size;
            
            
            //relevantTracks = IsTileNearTrack(coord);
            
            bool modified = false;
            if (relevantTracks.Count > 0)
            {
                modified = CalculateFlattening(currentHeights, originalHeights, relevantTracks, tileSize, res);
            }

            List<HeightDelta> deltasToCache = new List<HeightDelta>();

            if (modified)
            {
                // Populate compression cache collection by comparing differences
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        if (Mathf.Abs(currentHeights[z, x] - originalHeights[z, x]) > 0.00001f)
                        {
                            deltasToCache.Add(new HeightDelta(x, z, currentHeights[z, x]));
                        }
                    }
                }

                data.SetHeights(0, 0, currentHeights);

                Terrain gridCellTerrain = gridCell.terrain;
                if (gridCellTerrain != null)
                {
                    FinalizeTile(coord, gridCellTerrain);
                    var collider = gridCellTerrain.GetComponent<TerrainCollider>();
                    if (collider != null) collider.terrainData = data;
                }
                
                data.SyncHeightmap();
                Debug.Log($"[DoubleTrack] Fixed tile {coord}. Cached {deltasToCache.Count} structural coordinates.");
            }

            CompressedHeightCache[coord] = deltasToCache;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[DoubleTrack] Synchronous flattening failed for {coord}");
            UnityEngine.Debug.LogException(e);
        }
    }


    public static List<Vector3[]> IsTileNearTrack(Vector2Int coord)
    {
        List<Vector3[]> relevantTracks = new List<Vector3[]>();
        
        Vector3 tileSize = Vector3.one * TerrainGrid.Instance.TerrainSizeInWorld;
        Vector3 capturedTileWorldPos = TerrainGrid.Instance.ToWorldPosition(coord);
        
        Bounds tileBounds = new Bounds(capturedTileWorldPos + tileSize * 0.5f, tileSize);
        tileBounds.Expand(BRUSH_SIZE * 2f);
        
        Vector3 worldShift = WorldMover.currentMove;
        
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
                    
                if (tileBounds.Intersects(trackBounds)) relevantTracks.Add(relativePoints);
            }
        }
        return relevantTracks;
    }

    private static bool CalculateFlattening(float[,] heights, float[,] originalHeights, List<Vector3[]> tracks, Vector3 tileSize, int res)
    {
        bool modified = false;
        float searchRadius = BRUSH_SIZE / 2f;
        float borderBuffer = BRUSH_SIZE;
        float[,] localWeights = new float[res, res];
        
        foreach (var points in tracks)
        {
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 pA = points[i];
                Vector3 pB = points[i + 1];
                
                // --- SPATIAL PRUNING ---
                float minX = Mathf.Min(pA.x, pB.x) - borderBuffer;
                float maxX = Mathf.Max(pA.x, pB.x) + borderBuffer;
                float minZ = Mathf.Min(pA.z, pB.z) - borderBuffer;
                float maxZ = Mathf.Max(pA.z, pB.z) + borderBuffer;
                
                if (maxX < 0f || minX > tileSize.x || maxZ < 0f || minZ > tileSize.z) continue;

                // --- GRID CLAMPING ---
                int xStart = Mathf.Max(0, Mathf.FloorToInt((minX / tileSize.x) * (res - 1)));
                int xEnd = Mathf.Min(res - 1, Mathf.CeilToInt((maxX / tileSize.x) * (res - 1)));
                int zStart = Mathf.Max(0, Mathf.FloorToInt((minZ / tileSize.z) * (res - 1)));
                int zEnd = Mathf.Min(res - 1, Mathf.CeilToInt((maxZ / tileSize.z) * (res - 1)));
                
                for (int z = zStart; z <= zEnd; z++)
                {
                    for (int x = xStart; x <= xEnd; x++)
                    {
                        float stepSize = tileSize.x / (res - 1);
                        Vector3 pixelPos = new Vector3(x * stepSize, 0, z * stepSize);
                        
                        float dist = GetDistanceToSegment(pixelPos, pA, pB);
                        if (dist > searchRadius) continue;
                        
                        float weight = (dist <= SMOOTH_RADIUS) ? 1.0f : Mathf.SmoothStep(1, 0, (dist - SMOOTH_RADIUS) / (searchRadius - SMOOTH_RADIUS));
                        float targetH = (GetLerpedY(pixelPos, pA, pB) + VERTICAL_OFFSET) / tileSize.y;

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

    private static float GetDistanceToSegment(Vector3 p, Vector3 a, Vector3 b) {
        Vector2 p2 = new Vector2(p.x, p.z), a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z);
        Vector2 ab = b2 - a2;
        float t = Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / Vector2.Dot(ab, ab));
        
        float dx = p2.x - (a2.x + t * ab.x);
        float dy = p2.y - (a2.y + t * ab.y);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static float GetLerpedY(Vector3 p, Vector3 a, Vector3 b) {
        Vector2 p2 = new Vector2(p.x, p.z), a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z);
        Vector2 ab = b2 - a2;
        float t = Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / Vector2.Dot(ab, ab));
        return Mathf.Lerp(a.y, b.y, t);
    }

    private static void WeldSeams(Terrain current, Terrain neighbor, bool horizontal)
    {
        if (current == null || neighbor == null) return;
        int res = current.terrainData.heightmapResolution;
    
        if (horizontal)
        {
            float[,] theirEdge = neighbor.terrainData.GetHeights(res - 1, 0, 1, res);
            float[,] myHeights = current.terrainData.GetHeights(0, 0, 1, res);
    
            for (int i = 0; i < res; i++) myHeights[i, 0] = theirEdge[i, 0];
            current.terrainData.SetHeights(0, 0, myHeights);
        }
        else
        {
            float[,] theirEdge = neighbor.terrainData.GetHeights(0, res - 1, res, 1);
            float[,] myHeights = current.terrainData.GetHeights(0, 0, res, 1);

            for (int i = 0; i < res; i++) myHeights[0, i] = theirEdge[0, i];
            current.terrainData.SetHeights(0, 0, myHeights);
        }
    }

    private static void FinalizeTile(Vector2Int coord, Terrain activeTerrain)
    {
        Terrain left = TerrainGrid.Instance.GetLoadedTerrainAt(coord + Vector2Int.left);
        Terrain bottom = TerrainGrid.Instance.GetLoadedTerrainAt(coord + Vector2Int.down);

        if (left != null) WeldSeams(activeTerrain, left, true);
        if (bottom != null) WeldSeams(activeTerrain, bottom, false);
        
        Terrain right = TerrainGrid.Instance.GetLoadedTerrainAt(coord + Vector2Int.right);
        Terrain top = TerrainGrid.Instance.GetLoadedTerrainAt(coord + Vector2Int.up);

        if (right != null) WeldSeams(right, activeTerrain, true);
        if (top != null) WeldSeams(top, activeTerrain, false);
    }
}