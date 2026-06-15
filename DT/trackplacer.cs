
using DV.CabControls;
using DV.CabControls.NonVR;
using DV.Interaction;
using DV.Signs;
using UnityEngine;
using HarmonyLib;
using Ludiq;
using Rewired.Utils.Platforms.Windows;
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
        private static Transform railwayParent;
        public static List<RailTrack> AddedTracks = null;
        public static List<Junction> AddedJunctions = null;
        public static Dictionary<string, RailTrack> ComplexJunctionEnds;
        public static int trackCounter;
        private static GameObject railwayGo;
        private static List<string> LoadTargets()
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
        
        

        public static void LoadTracks(Scene arg0, LoadSceneMode loadSceneMode)
        {
            if(railwayGo != null) return;
            if (WorldStreamingInit.Instance == null || WorldStreamingInit.Instance.railwayScenePath == null) return;
            if (arg0.path != WorldStreamingInit.Instance.railwayScenePath) return;
            if (RailTrackRegistry.Instance == null) return;
            
            AddedTracks = new List<RailTrack>();
            AddedJunctions = new List<Junction>();
            ComplexJunctionEnds = new Dictionary<string, RailTrack>();
            RailTrackSceneManager.MovedObjectsRegistry.Clear();
            SignalFixer.AdditionalSignalsToFix.Clear();
            trackCounter = 0;
            
            railwayGo = GameObject.Find("[railway]");
            if (railwayGo == null) return;
            railwayParent = railwayGo.transform;

            List<string> targets = LoadTargets();
            List<RailTrack> allTracks = new List<RailTrack>(Object.FindObjectsOfType<RailTrack>());

            WorldStateFixer.Patch = false;

            foreach (string entry in targets)
            {
                string[] parts = entry.Split(',');

                string mode = parts[0].Trim().ToUpper();
                

                // 1. Handle Skip Mode (#)
                if (mode.IndexOf('#') != -1)continue;
                
                if (mode.IndexOf('S') != -1)
                {
                    // Updated check: Mode, Name, Start, End, Offset, X, Z = 7 parts
                    if (parts.Length < 7) 
                    {
                        Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
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
                    
                    trackCounter++;
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
                    
                    Object.Destroy(tempA[1]);
                    if (mode.IndexOf('M') == -1)
                    {
                        if (mode.IndexOf('I') == -1)
                        {
                            newTrack.name = "[y]_[doubletrack]_[Siding-" + trackCounter + "-D]";
                            newMains[1].name = "[y#]_[doubletrack]_[Main-" + trackCounter + "-T]";
                        }
                        else
                        {
                            newMains[1].name = "[y]_[doubletrack]_[Siding-" + trackCounter + "-T]";
                            newTrack.name = "[y#]_[doubletrack]_[Main-" + trackCounter + "-D]";
                        }
                    }
                    else
                    {
                        newTrack.name = "DT-" + trackCounter + "-D";
                        newMains[1].name = "DT-" + trackCounter + "-T";
                    }
                    
                    // 4. Set up Junctions
                    SetUpPrefabJunction(newMains[0], newMains[1], newTrack, xzOffset, "Split", true);
                    SetUpPrefabJunction(newMains[2], newMains[1], newTrack, xzOffset, "Merge", false);

                    if(mode.IndexOf('M') == -1)SetupComponent(newMains[1], newTrack, xzOffset);
                    
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
                    allTracks.Add(newMains[1]);
                    allTracks.Add(newTrack);

                    newMains[0].name = template.name + "A";
                    newMains[2].name = template.name + "B";
                    
                    if(!allTracks.Contains(newMains[0]))allTracks.Add(newMains[0]);
                    if(!allTracks.Contains(newMains[2]))allTracks.Add(newMains[2]);
                    Object.Destroy(template);
                }
                
                
                
                
                else if (mode.IndexOf('J') != -1)
                {
                    // Updated check: Mode, Name, Start, End, Offset, X, Z = 7 parts
                    if (parts.Length < 10) 
                    {
                        Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
                        continue;
                    }
                    
                    // 2. Validate and Parse the rest of the parameters
                    // Note: Indices are shifted +1 compared to your previous code
                    string targetName = parts[1].Trim();
                    
                    if (!int.TryParse(parts[2], out int node)) continue;
                    if (!bool.TryParse(parts[3], out bool isDiverge)) continue;
                    if (!bool.TryParse(parts[4], out bool flipDirection)) continue;
                    if (!bool.TryParse(parts[5], out bool mirror)) continue;
                    if (!float.TryParse(parts[6], out float searchX)) continue;
                    if (!float.TryParse(parts[7], out float searchZ)) continue;
                    if (!float.TryParse(parts[8], out float tanScale)) continue;
                    string junctionID = parts[9].Trim();
                    
                    
                    Vector3 searchPos = new Vector3(searchX, 0, searchZ);
                    int offset = 1;
                    if (mirror) offset = -1;
                    
                    RailTrack target = allTracks.Where(t => t.name == targetName).OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z))).FirstOrDefault();
                    
                    if (target == null)
                    {
                        Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
                        continue;
                    }
                    target.gameObject.SetActive(false);
                    
                    RailTrack[] splitTrack = SplitTrackForJunction(target, node);
                    if(!flipDirection) (splitTrack[0], splitTrack[1]) = (splitTrack[1], splitTrack[0]);

                    Junction newJunction;
                    
                    if(!isDiverge)newJunction = SetUpPrefabJunction(splitTrack[0], splitTrack[1], null, offset, "Complex", flipDirection, mode.IndexOf('M') == -1, junctionID);
                    else newJunction = SetUpPrefabJunction(splitTrack[0], null, splitTrack[1], offset, "Complex", flipDirection, mode.IndexOf('M') == -1, junctionID);

                    splitTrack[0].gameObject.name = targetName + "A";
                    splitTrack[1].gameObject.name = targetName + "B";
                    
                    foreach (RailTrack track in splitTrack)
                    {
                        track.curve.transform.parent = track.transform;
                        track.transform.SetParent(railwayParent);
                        track.gameObject.SetActive(true);
                    }

                    RailTrack endTrack = newJunction.outBranches[newJunction.outBranches[0].track.outBranch.track == null ? 0 : 1].track;
                    
                    ComplexJunctionEnds.Add(junctionID,endTrack);
                    
                    float mag = 1f/CalcTanSacle(flipDirection, splitTrack[1].curve);
                    splitTrack[1].curve.ScaleEndTangents(!flipDirection, mag*tanScale);

                    allTracks.Remove(target);
                    allTracks.Add(splitTrack[0]);
                    allTracks.Add(splitTrack[1]);
                    if (mode.IndexOf('E') != -1)
                    {
                        AddedTracks.Add(splitTrack[0]);
                        AddedTracks.Add(splitTrack[1]);
                    }

                    if (AddedTracks.Contains(target)) AddedTracks.Remove(target);
                    Object.Destroy(target);
                }
                
                
                
                else if (mode.IndexOf('T') != -1)
                {
                    // Updated check: Mode, Name, Start, End, Offset, X, Z = 7 parts
                    if (parts.Length < 5) 
                    {
                        Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
                        continue;
                    }
                    
                    string inTargetID = parts[1].Trim();
                    string outTargetID = parts[2].Trim();
                    string name = parts[3].Trim();
                    string points = parts[4].Trim();
                    
                    Dictionary<string,float[]> pointsList = BezierOffsetTool.ParseTrackData(points);
                    
                    BezierCurve curve = new GameObject("Curve").AddComponent<BezierCurve>();
                    
                    BezierPoint startPoint = Object.Instantiate(ComplexJunctionEnds[inTargetID].curve.Last(), curve.transform, true);
                    BezierPoint endPoint = Object.Instantiate(ComplexJunctionEnds[outTargetID].curve.Last(), curve.transform, true);

                    startPoint.transform.localRotation = Quaternion.Euler(Vector3.zero);
                    endPoint.transform.localRotation = Quaternion.Euler(Vector3.zero);
                    
                    
                    curve.AddPoint(startPoint);

                    Vector3 searchPos = ComplexJunctionEnds[inTargetID].transform.position;
                    
                    foreach (string key in pointsList.Keys)
                    {
                        float tanLenght = -1;

                        float yOffset = 0;
                        
                        if (pointsList[key].Length > 4) searchPos = new Vector3(pointsList[key][3], 0, pointsList[key][4]);
                        if (pointsList[key].Length > 5) yOffset = pointsList[key][5];
                        if (pointsList[key].Length > 6) tanLenght = -pointsList[key][6];
                        
                        BezierCurve targetCurve = allTracks.Where(t => t.name == key).OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z))).FirstOrDefault().curve;
                    
                        if (targetCurve == null)
                        {
                            Debug.LogWarning($"[DoubleTrack] Could not find '{key}' near {searchPos.x}, {searchPos.z}");
                            continue;
                        }

                        int startIndex = (int)pointsList[key][0];
                        int endIndex = (int)pointsList[key][1];
                        bool invert = false;
                        if (startIndex > endIndex)
                        {
                            (startIndex, endIndex) = (endIndex, startIndex);
                            invert = true;
                        }
                        else if (startIndex == endIndex && tanLenght >0 )
                        {
                            invert = true;
                            tanLenght *= -1;
                        }
                        

                        BezierCurve tempCurve = BezierOffsetTool.CreateParallelCopy(targetCurve, startIndex, endIndex, pointsList[key][2], yOffset);
                        //Finish using the curve combine method to remove the points from the cloned curve for each segment and add them to our new track
                        BezierPoint[] tempPoints = tempCurve.GetAnchorPoints();
                        if(invert) Array.Reverse(tempPoints);
                        foreach (BezierPoint point in tempPoints)
                        {
                            
                            if(invert)(point.handle1,point.handle2) = (point.handle2, point.handle1);
                            
                            if(point.handle1.magnitude <= 1.1)
                            {
                                point.handle1 = point.handle2 * tanLenght;
                            }
                            if(point.handle2.magnitude <= 1.1)point.handle2 = point.handle1 * tanLenght;
                            
                            curve.AddPoint(point);
                            point.transform.parent = curve.transform;
                        }
                        Object.Destroy(tempCurve);
                    }
                    
                    curve.AddPoint(endPoint);
                    curve.resolution = 0.5f;
                    
                    /*
                    float magS = 1/CalcTanSacle(true, curve);
                    float magE = 1/CalcTanSacle(false, curve);
                    curve.ScaleEndTangents(false,magS);
                    curve.ScaleEndTangents(true,magE);
                    */
                    curve.SetDirty();
                    
                    
                    
                    RailTrack template = allTracks[0];
                    template.gameObject.SetActive(false);
                    RailTrack newTrack = Object.Instantiate(template);
                    template.gameObject.SetActive(true);
                    
                    
                    newTrack.generateColliders = true;
                    newTrack.name = name;
                    
                    
                    AccessTools.Field(typeof(RailTrack), "_curve").SetValue(newTrack,curve);
                    
                    newTrack.curve.transform.parent = newTrack.transform;
                    newTrack.inBranch = new Junction.Branch();
                    newTrack.outBranch = new Junction.Branch();
                    newTrack.inJunction = null;
                    newTrack.outJunction = null;
                    
                    
                    SnapTrackToPoint(newTrack, true, ComplexJunctionEnds[inTargetID].curve.Last());
                    SnapTrackToPoint(newTrack, false, ComplexJunctionEnds[outTargetID].curve.Last());


                    
                    ComplexJunctionEnds[inTargetID].outBranch.track = newTrack;
                    ComplexJunctionEnds[inTargetID].outBranch.first = true;
                    
                    ComplexJunctionEnds[outTargetID].outBranch.track = newTrack;
                    ComplexJunctionEnds[outTargetID].outBranch.first = false;

                    
                    newTrack.inBranch.track = ComplexJunctionEnds[inTargetID];
                    newTrack.inBranch.first = false;
                    
                    newTrack.outBranch.track = ComplexJunctionEnds[outTargetID];
                    newTrack.outBranch.first = false;
                    
                    
                    newTrack.transform.SetParent(railwayParent);
                    newTrack.gameObject.SetActive(true);
                    
                    allTracks.Add(newTrack);
                    
                    if (mode.IndexOf('E') != -1)AddedTracks.Add(newTrack);
                }
                
                else if (mode.IndexOf('N') != -1)
                {
                    if (parts.Length < 6) 
                    {
                        Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
                        continue;
                    }
                    
                    string targetName = parts[1].Trim();
                    if (!int.TryParse(parts[2], out int insertAfter)) continue;
                    if (!float.TryParse(parts[3], out float distance)) continue;
                    if (!float.TryParse(parts[4], out float searchX)) continue;
                    if (!float.TryParse(parts[5], out float searchZ)) continue;
                    
                    
                    RailTrack template = allTracks
                        .Where(t => t.name == targetName)
                        .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
                        .FirstOrDefault();

                    if (template == null)
                    {
                        Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
                        continue;
                    }

                    BezierPoint startNode = template.curve.GetAnchorPoints()[insertAfter];
                    BezierPoint newNode;
                    if (mode.IndexOf('I') == -1)newNode = template.curve.InsertPointAtDistance(startNode, distance);
                    else newNode = template.curve.InsertStraightExtension(startNode, distance);

                    if (parts.Length>6&&float.TryParse(parts[6], out float scale))
                    {
                        newNode.handle1 *= scale;
                        newNode.handle2 *= scale;
                    }
                }
                
                else if (mode.IndexOf('O') != -1)
                {
                    if (parts.Length < 8) 
                    {
                        Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
                        continue;
                    }
                    string sceneName = parts[1].Trim();
                    string objectName = parts[2].Trim();
                    if (!float.TryParse(parts[3], out float fromX)) continue;
                    if (!float.TryParse(parts[4], out float fromY)) continue;
                    if (!float.TryParse(parts[5], out float fromZ)) continue;
                    if (!float.TryParse(parts[6], out float toX)) continue;
                    if (!float.TryParse(parts[7], out float toY)) continue;
                    if (!float.TryParse(parts[8], out float toZ)) continue;

                    RailTrackSceneManager.MovedGeometryObject movedGeometryObject = new RailTrackSceneManager.MovedGeometryObject()
                    {
                        Name = objectName, 
                        NewPosition = new Vector3(toX, toY, toZ),
                        OldPosition = new Vector3(fromX, fromY, fromZ),
                    };
                    if (!RailTrackSceneManager.MovedObjectsRegistry.ContainsKey(sceneName)) RailTrackSceneManager.MovedObjectsRegistry.Add(sceneName, new List<RailTrackSceneManager.MovedGeometryObject>());
                    RailTrackSceneManager.MovedObjectsRegistry[sceneName].Add(movedGeometryObject);
                }
                
                else if (mode.IndexOf("M") != -1)
                {
                    for (int i = 1; i < parts.Length; i++)
                    {
                        SignalFixer.AdditionalSignalsToFix.Add(parts[i].Trim());
                        SignalFixer.AdditionalSignalsToFix.Add(parts[i].Trim()+"-D");
                    }
            }
            }
            
            new GameObject("signPlacer", typeof(SignPlacer));
        }
        
        
        

        public static Junction SetUpPrefabJunction(RailTrack anchor, RailTrack mainMid, RailTrack siding, float xzOffset, string name, bool isDiverge, bool manage = true, string id = "")
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

            junction.defaultSelectedBranch = 0;
            junction.selectedBranch = 0;
            
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


        private static float CalcTanSacle(bool isStart, BezierCurve curve)
        {
            float tanScale = 4f;
            float mag;
            if(isStart) mag = Vector3.Distance(curve[0].position,curve[1].position)/tanScale;
            else mag = Vector3.Distance(curve[curve.pointCount-1].position,curve[curve.pointCount-2].position)/tanScale;
            return mag;
        }
        private static void SnapTrackToPoint(RailTrack track, bool isStart, BezierPoint targetPoint)
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

        private static void SetupComponent(RailTrack main, RailTrack side, float offset)
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