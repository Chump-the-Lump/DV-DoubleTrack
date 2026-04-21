
using System.Reflection;
using Cysharp.Threading.Tasks.Triggers;
using DV.CabControls;
using DV.CabControls.NonVR;
using DV.Interaction;
using DV.PointSet;
using DV.Signs;
using DV.TerrainSystem;
using DV.TerrainTools;
using DV.Utils;
using UnityEngine;
using HarmonyLib;
using JBooth.MicroSplat;
using Ludiq;
using TerrainComposer2;
using UnityModManagerNet;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DoubleTrack;

    public static class TrackPlacerEntry
    {
        public static UnityModManager.ModEntry ModEntry;
        public static bool Load(UnityModManager.ModEntry entry)
        {
            ModEntry = entry;
            var harmony = new Harmony(entry.Info.Id);
            harmony.PatchAll();
            SceneManager.sceneLoaded += AllTracksPatch.LoadTracks;
            PersistentTerrainManager.Initialize();
            return true;
        }
    }
    
    public class AllTracksPatch
    {
        private static Transform railwayParent;
        public static List<RailTrack> AddedTracks;
        public static int junctionIDCounter;
        private static GameObject railwayGo;
        private static List<string> LoadTargets()
        {
            string path = Path.Combine(TrackPlacerEntry.ModEntry.Path, "target.txt");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[DoubleTrack] target.txt not found at {path}");
                return new List<string>();
            }

            string content = File.ReadAllText(path);
            return content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();
        }

        public static void LoadTracks(Scene arg0, LoadSceneMode loadSceneMode)
        {
            if(railwayGo != null) return;
            if (WorldStreamingInit.Instance == null || WorldStreamingInit.Instance.railwayScenePath == null) return;
            if (arg0.path != WorldStreamingInit.Instance.railwayScenePath) return;
            if (RailTrackRegistry.Instance == null) return;
            
            AddedTracks = new List<RailTrack>();
            junctionIDCounter = 0;
            
            railwayGo = GameObject.Find("[railway]");
            if (railwayGo == null) return;
            railwayParent = railwayGo.transform;

            List<string> targets = LoadTargets();
            List<RailTrack> allTracks = new List<RailTrack>(Object.FindObjectsOfType<RailTrack>());

            foreach (string entry in targets)
            {
                string[] parts = entry.Split(',');
                
                // Updated check: Mode, Name, Start, End, Offset, X, Z = 7 parts
                if (parts.Length < 7) 
                {
                    Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
                    continue;
                }

                string mode = parts[0].Trim().ToUpper();

                // 1. Handle Skip Mode (#)
                if (mode == "#")
                {
                    Debug.Log($"[DoubleTrack] Skipping entry per '#' marker: {entry}");
                    continue;
                }

                // 2. Validate and Parse the rest of the parameters
                // Note: Indices are shifted +1 compared to your previous code
                string targetName = parts[1].Trim();
                
                string rawStart = parts[2].Trim();
                string rawEnd = parts[3].Trim();

                bool deadEndStart = rawStart.StartsWith("X", StringComparison.OrdinalIgnoreCase);
                bool deadEndEnd = rawEnd.StartsWith("X", StringComparison.OrdinalIgnoreCase);

        // Strip 'X' if present before parsing to int
                if (!int.TryParse(deadEndStart ? rawStart.Substring(1) : rawStart, out int startIdx)) continue;
                if (!int.TryParse(deadEndEnd ? rawEnd.Substring(1) : rawEnd, out int endIdx)) continue;
                
                if (!float.TryParse(parts[4], out float xzOffset)) continue;
                if (!float.TryParse(parts[5], out float searchX)) continue;
                if (!float.TryParse(parts[6], out float searchZ)) continue;

                // 3. Handle Siding/Split Mode (S)
                if (mode == "S")
                {
                    Vector3 searchPos = new Vector3(searchX, 0, searchZ);

                    RailTrack template = allTracks
                        .Where(t => t.name == targetName)
                        .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z)))
                        .FirstOrDefault();

                    if (template == null)
                    {
                        Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
                        continue;
                    }

                    // 1. Create Siding
                    // Deactivate original temporarily to prevent junction logic firing during setup
                    template.gameObject.SetActive(false);

                    RailTrack newTrack = Object.Instantiate(template);
                    newTrack.generateColliders = true;
                    newTrack.name = targetName + "_Siding";

                    // Create the parallel geometry
                    AccessTools.Field(typeof(RailTrack), "_curve").SetValue(newTrack,BezierOffsetTool.CreateParallelCopy(template.curve, startIdx, endIdx, xzOffset, 0));

                    newTrack.curve.transform.parent = newTrack.transform;
                    newTrack.inBranch = new Junction.Branch();
                    newTrack.outBranch = new Junction.Branch();
                    newTrack.inJunction = null;
                    newTrack.outJunction = null;

                    // 2. Split Main (Logical split of the template rail)
                    
                    
                    RailTrack[] tempA = SplitTrackForJunction(template, startIdx);
                    RailTrack[] tempB = SplitTrackForJunction(tempA[1], endIdx - startIdx);
                    RailTrack[] newMains = new[] { tempA[0], tempB[0], tempB[1] };
                    
                    // 4. Set up Junctions
                    SetUpPrefabJunction(newMains[0], newMains[1], newTrack, xzOffset, "Split", true);
                    SetUpPrefabJunction(newMains[2], newMains[1], newTrack, xzOffset, "Merge", false);
                    
                    AddedTracks.Add(newTrack);
                    AddedTracks.Add(newMains[1]);
                    
                    newTrack.transform.SetParent(railwayParent);
                    newTrack.gameObject.SetActive(true);


                    foreach (RailTrack track in newMains)
                    {
                        track.curve.transform.parent = track.transform;
                        track.transform.SetParent(railwayParent);
                        track.gameObject.SetActive(true);
                    }

                    allTracks.Remove(template);
                    if(!allTracks.Contains(newMains[0]))allTracks.Add(newMains[0]);
                    if(!allTracks.Contains(newMains[2]))allTracks.Add(newMains[2]);
                    Object.Destroy(template);
                }
            }
               
            /*
            FieldInfo field = typeof(RailTrackRegistryBase).GetField("_allTracks", BindingFlags.NonPublic | BindingFlags.Instance);
    
            if (field != null)
            {
                field.SetValue(RailTrackRegistryBase.Instance, null);
            }

            RailTrack[]? _ = RailTrackRegistryBase.Instance.AllTracks;
            _ = null;
            */
            
            new GameObject("signPlacer", typeof(SignPlacer));
        }


        public static Junction SetUpPrefabJunction(RailTrack anchor, RailTrack mainMid, RailTrack siding, float xzOffset, string id, bool isDiverge)
        {
            string side = (xzOffset > 0) ^ !isDiverge ? "left" : "right";

            GameObject template = GameObject.FindObjectsOfType<GameObject>()
                .FirstOrDefault(go => go.name.StartsWith("junc-" + side) && go.transform.parent.name == "[railway]");

            template.SetActive(false);
            GameObject juncGroup = Object.Instantiate(template, railwayParent);
            template.SetActive(true);

            //LogAllComponents(juncGroup, id);

            juncGroup.name = $"junc-{side}_{id}";

            RailTrack prefabThrough = juncGroup.transform.Find("[track through]").GetComponent<RailTrack>();
            RailTrack prefabDiverge = juncGroup.transform.Find("[track diverging]").GetComponent<RailTrack>();
            Junction junction = juncGroup.GetComponentInChildren<Junction>();

            VisualSwitch visualSwitch = juncGroup.GetComponentInChildren<VisualSwitch>();
            visualSwitch.invertDirection = true;
            
            AccessTools.Field(typeof(VisualSwitch), "switchButtonGO").SetValue(visualSwitch,
                juncGroup.GetComponentInChildren<ButtonBase>().gameObject);
            visualSwitch.junction = junction;
            visualSwitch.animator = juncGroup.GetComponent<Animator>();

            Transform lever = juncGroup.GetComponentsInChildren<ButtonNonVR>(true).FirstOrDefault()?.transform;
            if (lever != null)
            {
                // Remove ALL instances to ensure a clean slate
                var existingHandlers = lever.GetComponents<GrabHandlerButton>();
                foreach (var h in existingHandlers) Object.DestroyImmediate(h);

                var existingButtons = lever.GetComponents<ButtonNonVR>();
                foreach (var b in existingButtons) Object.DestroyImmediate(b);
            }

            // Break template references by ensuring new Branch instances exist
            junction.inBranch = new Junction.Branch(anchor, !isDiverge);

            // Replace the shared list with unique Branch objects
            junction.outBranches = new List<Junction.Branch>()
            {
                new Junction.Branch(prefabThrough, true),
                new Junction.Branch(prefabDiverge, true)
            };
            
            Junction.JunctionData junctionData = new Junction.JunctionData();
            junctionData.junctionId = junctionIDCounter;
            junctionData.position = junction.transform.position;
            junctionData.junctionIdLong = "EXC-" + junctionIDCounter.ToString("D4")+"-MAIN";
            junction.junctionData = junctionData;
            
            junctionIDCounter++;

            // Positioning
            Vector3 anchorPos = isDiverge ? anchor.curve.Last().position : anchor.curve[0].position;
            Vector3 forwardDir = isDiverge
                ? (anchor.curve.Last().position - anchor.curve.Last().globalHandle1).normalized
                : (anchor.curve[0].globalHandle1 - anchor.curve[0].position).normalized;
            juncGroup.transform.rotation = Quaternion.LookRotation(forwardDir);

            Vector3 localInPoint = prefabThrough.curve[0].localPosition;
            juncGroup.transform.position = anchorPos - (juncGroup.transform.rotation * localInPoint);

            // Vertex Snapping
            if (isDiverge)
            {
                SnapTrackToPoint(mainMid, true, prefabThrough.curve.Last());
                SnapTrackToPoint(siding, true, prefabDiverge.curve.Last());
            }
            else
            {
                SnapTrackToPoint(mainMid, false, prefabThrough.curve.Last());
                SnapTrackToPoint(siding, false, prefabDiverge.curve.Last());
            }

            if (isDiverge)
            {
                anchor.outJunction = junction;
                anchor.outBranch = junction.outBranches[0];

                prefabThrough.inBranch = junction.inBranch;
                prefabThrough.outBranch = new Junction.Branch(mainMid, true);
                mainMid.inBranch = new Junction.Branch(prefabThrough, false);

                prefabDiverge.inBranch = junction.inBranch;
                prefabDiverge.outBranch = new Junction.Branch(siding, true);
                siding.inBranch = new Junction.Branch(prefabDiverge, false);
            }
            else
            {
                anchor.inJunction = junction;
                anchor.inBranch = junction.outBranches[0];

                prefabThrough.inBranch = junction.inBranch;
                prefabThrough.outBranch = new Junction.Branch(mainMid, false);
                mainMid.outBranch = new Junction.Branch(prefabThrough, false);

                prefabDiverge.inBranch = junction.inBranch;
                prefabDiverge.outBranch = new Junction.Branch(siding, false);
                siding.outBranch = new Junction.Branch(prefabDiverge, false);
            }

            junction.defaultSelectedBranch = 0;
            junction.selectedBranch = 0;
            
            prefabThrough.generateColliders = true;
            prefabDiverge.generateColliders = true;

            AccessTools.Field(typeof(RailTrack), "initialized").SetValue(prefabThrough, false);
            AccessTools.Field(typeof(RailTrack), "initialized").SetValue(prefabDiverge, false);

            //SingletonBehaviour<JunctionSwitcherManager>.Instance.AllowSwitchingForJunction(junction);

            junction.gameObject.SetActive(true);
            juncGroup.SetActive(true);
            

            return junction;
        }


        private static void SnapTrackToPoint(RailTrack track, bool isStart, BezierPoint targetPoint)
        {
            BezierCurve curve = track.curve;
            // Use the prefab's actual handle length (~14m) instead of 40m to prevent 'looping'
            float tanScale = 4f;
            float mag;
            if(isStart) mag = Vector3.Distance(curve[0].position,curve[1].position)/tanScale;
            else mag = Vector3.Distance(curve[curve.pointCount-1].position,curve[curve.pointCount-2].position)/tanScale;
            if (mag < 10f) mag = 30f;
            //mag = 40;

            if (isStart)
            {
                curve[0].position = targetPoint.position;
                // Point handles AWAY from the junction's internal handle
                Vector3 dir = (targetPoint.position - targetPoint.globalHandle1).normalized;
                curve[0].globalHandle1 = targetPoint.position - (dir * mag);
                curve[0].globalHandle2 = targetPoint.position + (dir * mag);
            }
            else
            {
                curve[curve.pointCount - 1].position = targetPoint.position;
                // FIX: For the END of a track, we must mirror globalHandle2
                Vector3 dir = (targetPoint.position - targetPoint.globalHandle2).normalized;
                curve[curve.pointCount - 1].globalHandle1 = targetPoint.position - (dir * mag);
                curve[curve.pointCount - 1].globalHandle2 = targetPoint.position + (dir * mag);
            }

            curve.dirty = true;
        }


        private static RailTrack[] SplitTrackForJunction(RailTrack track, int Index)
        {
            Junction.Branch OldInTrack = track.inBranch;
            Junction.Branch OldOutTrack = track.outBranch;

            Junction OldInJunction = track.inJunction;
            Junction OldOutJunction = track.outJunction;

            RailTrack[] newTracks = new RailTrack[2];
            for (int i = 0; i < newTracks.Length; i++)
            {
                newTracks[i] = Object.Instantiate(track);
                AccessTools.Field(typeof(RailTrack), "initialized").SetValue(newTracks[i], false);
                newTracks[i].name += $"{i + Index}";
            }

            AccessTools.Field(typeof(RailTrack), "_curve").SetValue(newTracks[0],
                BezierOffsetTool.CreateParallelCopy(track.curve, 0, Index, 0, 0));
            newTracks[0].outJunction = null;

            AccessTools.Field(typeof(RailTrack), "_curve").SetValue(newTracks[1],
                BezierOffsetTool.CreateParallelCopy(track.curve, Index, track.curve.pointCount - 1, 0, 0));
            newTracks[1].inJunction = null;


            newTracks[0].outBranch.track = newTracks[1];
            newTracks[0].outBranch.first = true;

            newTracks[1].inBranch.track = newTracks[0];


            if (OldInTrack?.track?.inBranch?.track == track) OldInTrack.track.inBranch.track = newTracks[0];
            else if (OldInTrack?.track?.outBranch?.track == track) OldInTrack.track.outBranch.track = newTracks[0];



            if (OldOutTrack?.track?.inBranch?.track == track) OldOutTrack.track.inBranch.track = newTracks[1];
            else if (OldOutTrack?.track?.outBranch?.track == track) OldOutTrack.track.outBranch.track = newTracks[1];


            if (track.inJunction != null)
            {
                if (track.inJunction.inBranch.track == track) newTracks[0].inJunction.inBranch.track = newTracks[0];
                if (track.inJunction.outBranches[0].track == track)
                    newTracks[0].inJunction.outBranches[0].track = newTracks[0];
                if (track.inJunction.outBranches[1].track == track)
                    newTracks[0].inJunction.outBranches[1].track = newTracks[0];
            }

            if (track.outJunction != null)
            {
                if (track.outJunction.inBranch.track == track) newTracks[1].outJunction.inBranch.track = newTracks[1];
                if (track.outJunction.outBranches[0].track == track)
                    newTracks[1].outJunction.outBranches[0].track = newTracks[1];
                if (track.outJunction.outBranches[1].track == track)
                    newTracks[1].outJunction.outBranches[1].track = newTracks[1];
            }
            

            for (int i = 0; i < newTracks.Length; i++)
            {
                if (RailTrack.pointSets.ContainsKey(newTracks[i])) RailTrack.pointSets.Remove(newTracks[i]);
            }
            
            newTracks[1].generateColliders = true;

            return newTracks;
        }

        private static void LogAllComponents(GameObject root, string junctionId)
        {
            Debug.Log($"[DoubleTrack] --- Component Map for {junctionId} ---");

            // Get all components in the root and all children
            Component[] allComponents = root.GetComponentsInChildren<Component>(true);

            foreach (var comp in allComponents)
            {
                if (comp == null) continue; // Skip missing scripts

                // Build the child path for clarity
                string path = comp.gameObject.name;
                Transform parent = comp.transform.parent;
                while (parent != null && parent != root.transform.parent)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }

                Debug.Log($"[DoubleTrack] Object: [{path}] | Component: {comp.GetType().Name}");
            }

            Debug.Log($"[DoubleTrack] --- End Map for {junctionId} ---");
        }
    }