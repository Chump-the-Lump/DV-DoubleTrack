
using DV;
using DV.CabControls;
using DV.CabControls.NonVR;
using DV.Interaction;
using DV.Signs;
using DV.TerrainTools;
using UnityEngine;
using HarmonyLib;
using Ludiq;
using Rewired.Utils.Platforms.Windows;
using Unity.Jobs;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DoubleTrack;
/*
 * S - Standerd
 * J - Junction
 * T - Track
 * E - Edit terrain (Allways true for S)
 * M - Don't manage (J only)
 * I - Invert (S only)
 * O - Object
 */

    
    public class AllTracksPatch
    {
        public static Transform railwayParent;
        public static List<RailTrack> AddedTracks = null;
        public static List<Junction> AddedJunctions = null;
        public static Dictionary<string, RailTrack> ComplexJunctionEnds;
        public static int trackCounter;
        public static GameObject railwayGo;

        public static List<string> LoadTargets()
        {
            string path = TrackPlacerEntry.TARGET_PATH;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"target.txt not found at {path}");
                return new List<string>();
            }

            string content = File.ReadAllText(path);
            return content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();
        }

        public static void CreateBufferStop(RailTrack track, bool start = false)
        {
            GameObject template = GameObject.FindObjectsOfType<GameObject>().FirstOrDefault(go =>
                go.name.StartsWith("term-buffer") && go.transform.parent.name == "[railway]");
            
            template.SetActive(false);
            GameObject newBufferGO = Object.Instantiate(template, railwayParent);
            template.SetActive(true);

            if (!start)
            {
                newBufferGO.transform.position = track.curve.GetAnchorPoints()[track.curve.pointCount - 1].position;
                newBufferGO.transform.forward = -track.curve.GetTangentAt(0.9999f).normalized;
            }
            else
            {
                newBufferGO.transform.position = track.curve.GetAnchorPoints()[0].position;
                newBufferGO.transform.forward = track.curve.GetTangentAt(0.0001f).normalized;
            }


            ConnectablePrefab connectablePrefab = newBufferGO.gameObject.GetComponent<ConnectablePrefab>();
            
            GameObject anchor = new GameObject("in");
            anchor.transform.parent = newBufferGO.transform.Find("[connectable]");
            anchor.transform.position = Vector3.zero;
            anchor.transform.localEulerAngles = Vector3.up;
            connectablePrefab.connectors = new Transform[]{anchor.transform};
            
            newBufferGO.transform.GetChild(0).gameObject.SetActive(true);
            newBufferGO.SetActive(true);
        }


        public static Junction SetUpPrefabJunction(RailTrack anchor, RailTrack mainMid, RailTrack siding, float xzOffset, string name, bool isDiverge, bool manage = true, string id = "", bool defaultLeft = true)
        {
            string side = (xzOffset > 0) ^ !isDiverge ? "left" : "right";

            GameObject template = GameObject.FindObjectsOfType<GameObject>().FirstOrDefault(go => go.name.StartsWith("junc-" + side) && go.transform.parent.name == "[railway]");
            
            
            template.SetActive(false);
            GameObject juncGroup = Object.Instantiate(template, railwayParent);
            
            template.SetActive(true);

            //LogAllComponents(juncGroup, id);

            juncGroup.name = $"junc-{side}_{name}";

            RailTrack prefabThrough = juncGroup.transform.Find("[track through]").GetComponent<RailTrack>();
            RailTrack prefabDiverge = juncGroup.transform.Find("[track diverging]").GetComponent<RailTrack>();
            Junction junction = juncGroup.GetComponentInChildren<Junction>();

            if (AddedJunctions.Contains(junction))
            {
                Debug.LogError("Junction alrady exists");
                return junction;
            }

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

            if (side != "left"){
                junction.outBranches = new List<Junction.Branch>()
                {
                    new Junction.Branch(prefabThrough, true),
                    new Junction.Branch(prefabDiverge, true)
                };
                visualSwitch.invertDirection = true;
            }
            else{
                junction.outBranches = new List<Junction.Branch>()
                {
                    new Junction.Branch(prefabDiverge, true),

                    new Junction.Branch(prefabThrough, true)
                };
                visualSwitch.invertDirection = false;
            }
            
            Junction.JunctionData junctionData = new Junction.JunctionData();

            int junctionCounter = trackCounter * 2;
            if(isDiverge)junctionCounter++;
            junctionData.junctionIndex = junctionCounter + 1000;
            junctionData.junctionId = junctionCounter;
            junctionData.position = junction.transform.position;

            if (id == "")
            {
                if (isDiverge) junctionData.junctionIdLong = "DT-" + trackCounter.ToString("D4") + "-A";
                else junctionData.junctionIdLong = "DT-" + trackCounter.ToString("D4") + "-B";
            }
            else
            {
                junctionData.junctionIdLong = id;
            }

            junction.junctionData = junctionData;

            // Positioning
            Vector3 anchorPos = isDiverge ? anchor.curve.Last().position : anchor.curve[0].position;
            Vector3 forwardDir = isDiverge
                ? (anchor.curve.Last().position - anchor.curve.Last().globalHandle1).normalized
                : (anchor.curve[0].globalHandle1 - anchor.curve[0].position).normalized;
            juncGroup.transform.rotation = Quaternion.LookRotation(forwardDir);

            Vector3 localInPoint = prefabThrough.curve[0].localPosition;
            juncGroup.transform.position = anchorPos - (juncGroup.transform.rotation * localInPoint);



            if(mainMid!=null)SnapTrackToPoint(mainMid, isDiverge, prefabThrough.curve.Last());
            if(siding!=null)SnapTrackToPoint(siding, isDiverge, prefabDiverge.curve.Last());


            if (isDiverge)
            {
                anchor.outJunction = junction;
                anchor.outBranch = junction.outBranches[0];

                prefabThrough.inBranch = junction.inBranch;
                if(mainMid!=null)prefabThrough.outBranch = new Junction.Branch(mainMid, true);
                if(mainMid!=null)mainMid.inBranch = new Junction.Branch(prefabThrough, false);
                else prefabThrough.outBranch = new Junction.Branch();
                

                prefabDiverge.inBranch = junction.inBranch;
                if(siding!=null)prefabDiverge.outBranch = new Junction.Branch(siding, true);
                if(siding!=null)siding.inBranch = new Junction.Branch(prefabDiverge, false);
                else prefabDiverge.outBranch = new Junction.Branch();
            }
            else
            {
                anchor.inJunction = junction;
                anchor.inBranch = junction.outBranches[0];

                prefabThrough.inBranch = junction.inBranch;
                if(mainMid!=null)prefabThrough.outBranch = new Junction.Branch(mainMid, false);
                if(mainMid!=null)mainMid.outBranch = new Junction.Branch(prefabThrough, false);
                else prefabThrough.outBranch = new Junction.Branch();

                prefabDiverge.inBranch = junction.inBranch;
                if(siding!=null)prefabDiverge.outBranch = new Junction.Branch(siding, false);
                if(siding!=null)siding.outBranch = new Junction.Branch(prefabDiverge, false);
                else prefabDiverge.outBranch = new Junction.Branch();
                
            }

            byte selectBranch = (byte)(defaultLeft ? 0 : 1);
            junction.defaultSelectedBranch = selectBranch;
            junction.selectedBranch = selectBranch;
            
            prefabThrough.generateColliders = true;
            prefabDiverge.generateColliders = true;

            AccessTools.Field(typeof(RailTrack), "initialized").SetValue(prefabThrough, false);
            AccessTools.Field(typeof(RailTrack), "initialized").SetValue(prefabDiverge, false);
            
            
            juncGroup.SetActive(true);
            
            SwitchManager manager = junction.gameObject.GetOrAddComponent<SwitchManager>();
            manager.enabled = manage;
            
            AddedJunctions.Add(junction);

            return junction;
        }


        public static float CalcTanSacle(bool isStart, BezierCurve curve)
        {
            float tanScale = 4f;
            float mag;
            if(isStart) mag = Vector3.Distance(curve[0].position,curve[1].position)/tanScale;
            else mag = Vector3.Distance(curve[curve.pointCount-1].position,curve[curve.pointCount-2].position)/tanScale;
            return mag;
        }

        public static void SnapTrackToPoint(RailTrack track, bool isStart, BezierPoint targetPoint)
        {
            BezierCurve curve = track.curve;
            // Use the prefab's actual handle length (~14m) instead of 40m to prevent 'looping'
            
            float mag = CalcTanSacle(isStart, curve);
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
                //For the END of a track, we must mirror globalHandle2
                Vector3 dir = (targetPoint.position - targetPoint.globalHandle2).normalized;
                curve[curve.pointCount - 1].globalHandle1 = targetPoint.position - (dir * mag);
                curve[curve.pointCount - 1].globalHandle2 = targetPoint.position + (dir * mag);
            }

            curve.dirty = true;
        }


        public static RailTrack[] SplitTrackForJunction(RailTrack track, int Index)
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
            newTracks[1].inBranch.first = Vector3.Distance(newTracks[0].curve.GetAnchorPoints()[0].position, newTracks[1].curve.GetAnchorPoints()[0].position) < 0.001f;
            

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

        /*
        public static void SetupComponent(RailTrack main, RailTrack side, float offset)
        {
            DoubleRailTrack mainTrack = main.AddComponent<DoubleRailTrack>();
            DoubleRailTrack sideTrack = side.AddComponent<DoubleRailTrack>();
            
            mainTrack.IsSiding = false;
            sideTrack.IsSiding = true;
            
            mainTrack.Offset = offset;
            sideTrack.Offset = offset;

            mainTrack.ThisRailTrack = main;
            sideTrack.ThisRailTrack = side;
            
            mainTrack.OtherRailTrack = side;
            sideTrack.OtherRailTrack = main;
        }*/

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
        
        public static void DisconnectTracks(RailTrack trackA, RailTrack trackB)
        {
            if (trackA == null || trackB == null) return;
                    

            // Check trackA's IN branch
            if (trackA.inBranch != null && trackA.inBranch.track == trackB)
            {
                trackA.inBranch = null;
            }

            // Check trackA's OUT branch
            if (trackA.outBranch != null && trackA.outBranch.track == trackB)
            {
                trackA.outBranch = null;
            }

            // Check trackB's IN branch
            if (trackB.inBranch != null && trackB.inBranch.track == trackA)
            {
                trackB.inBranch = null;
            }

            // Check trackB's OUT branch
            if (trackB.outBranch != null && trackB.outBranch.track == trackA)
            {
                trackB.outBranch = null;
            }
                
        }

        [HarmonyPatch(typeof(RailwayMeshGenerator), "ScheduleGenerateBaseAndRail")]
        public static class ScheduleGenerateBaseAndRail_Patch
        {
            static bool Prefix(RailwayMeshGenerator __instance, TrackChunk chunk)
            {
                if (chunk?.track?.railType?.railShape == null || chunk?.track?.baseType?.baseShape == null) return true; // Fallback to original if invalid

                Vector3 position = chunk.track.transform.position;
                
                // Take pool objects for ballast, left rail, and right rail
                TrackChunkPoolObject ballastPoolObj = TrackChunkPoolObject.TakeFromPool(__instance.parent, position);
                TrackChunkPoolObject leftRailPoolObj = TrackChunkPoolObject.TakeFromPool(__instance.parent, position);
                TrackChunkPoolObject rightRailPoolObj = TrackChunkPoolObject.TakeFromPool(__instance.parent, position);
                
                // Fetch custom rail material or fall back to default __instance.railMat
                Material customRailMat = chunk.track.railType?.railMaterial != null 
                    ? chunk.track.railType.railMaterial 
                    : __instance.railMat;

                Material customBaseMat = chunk.track?.baseType.baseMaterial != null 
                    ? chunk.track.baseType.baseMaterial
                    : __instance.baseMat;
                
                // Assign materials directly to THIS chunk's pool objects
                ballastPoolObj.SetMaterial(customBaseMat);
                leftRailPoolObj.SetMaterial(customRailMat);
                rightRailPoolObj.SetMaterial(customRailMat);

                chunk.AssignPoolObjects(ballastPoolObj, leftRailPoolObj, rightRailPoolObj);


                // Calculate dynamic rail shape offset vectors
                Vector2[] points2D = chunk.track.railType.railShape.GetPoints2D(0.1f);
                Vector2 offset = Vector2.right * (chunk.track.railType.gauge * 0.5f + chunk.track.railType.railEdgeOffset);

                Vector2[] leftShape = OffsetPoints(points2D, -offset);
                Vector2[] rightShape = OffsetPoints(points2D, offset);
                Vector2[] gravelShape = chunk.track.baseType.baseShape.GetPoints2D();

                Vector3 globalOffset = -chunk.track.transform.position;


                // Schedule Mesh Sweeper Jobs
                MeshSweeperJob gravelJob = new MeshSweeperJob(
                    chunk.pointSet, chunk.minIndex, chunk.maxIndex, globalOffset, 
                    gravelShape, chunk.track.baseType.basePathUV, chunk.track.baseType.basePathUVScale, 
                    chunk.track.baseType.baseShapeUV, chunk.track.baseType.baseShapeUVScale
                );
                
                MeshSweeperJob leftRailJob = new MeshSweeperJob(
                    chunk.pointSet, chunk.minIndex, chunk.maxIndex, globalOffset, 
                    leftShape, capEnd: true
                );
                
                MeshSweeperJob rightRailJob = new MeshSweeperJob(
                    chunk.pointSet, chunk.minIndex, chunk.maxIndex, globalOffset, 
                    rightShape, capEnd: true
                );
                
                // Access the private activeJobs field via Reflection/Harmony AccessTools
                var activeJobs = AccessTools.Field(typeof(RailwayMeshGenerator), "activeJobs")
                    .GetValue(__instance) as List<(MeshSweeperJob, JobHandle, Mesh)>;

                activeJobs.Add((gravelJob, gravelJob.ScheduleSelf(), ballastPoolObj.mesh));
                activeJobs.Add((leftRailJob, leftRailJob.ScheduleSelf(), leftRailPoolObj.mesh));
                activeJobs.Add((rightRailJob, rightRailJob.ScheduleSelf(), rightRailPoolObj.mesh));

                return false; // Skip original method execution
            }

            private static Vector2[] OffsetPoints(Vector2[] sourcePoints, Vector2 offset)
            {
                Vector2[] result = new Vector2[sourcePoints.Length];
                for (int i = 0; i < sourcePoints.Length; i++)
                    result[i] = sourcePoints[i] + offset;
                return result;
            }
        }
    }