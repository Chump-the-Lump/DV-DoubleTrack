using System;
using System.Collections.Generic;
using System.Linq;
using DV.Signs;
using HarmonyLib;
using Ludiq;
using Rewired.Utils.Platforms.Windows;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityModManagerNet;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace DoubleTrack;

public static class TrackPlacer
{
    static readonly Material s_newMat = DV.Globals.G.GetRailType(000).railType.railMaterial;
    static readonly Material s_medMat = DV.Globals.G.GetRailType(050).railType.railMaterial;
    static readonly Material s_oldMat = DV.Globals.G.GetRailType(100).railType.railMaterial;
    public static void LoadTracks(Scene arg0, LoadSceneMode loadSceneMode)
    {
        if (AllTracksPatch.railwayGo != null) return;
        if (WorldStreamingInit.Instance == null || WorldStreamingInit.Instance.railwayScenePath == null) return;
        if (arg0.path != WorldStreamingInit.Instance.railwayScenePath) return;
        if (RailTrackRegistry.Instance == null) return;

        AllTracksPatch.AddedTracks = new List<RailTrack>();
        AllTracksPatch.AddedJunctions = new List<Junction>();
        AllTracksPatch.ComplexJunctionEnds = new Dictionary<string, RailTrack>();
        RailTrackSceneManager.MovedObjectsRegistry.Clear();
        AllTracksPatch.trackCounter = 0;

        AllTracksPatch.railwayGo = GameObject.Find("[railway]");
        if (AllTracksPatch.railwayGo == null) return;
        AllTracksPatch.railwayParent = AllTracksPatch.railwayGo.transform;

        List<string> targets = AllTracksPatch.LoadTargets();
        List<RailTrack> allTracks = new List<RailTrack>(Object.FindObjectsOfType<RailTrack>());

        WorldStateFixer.Patch = false;

        if(MultiplayerShim.IsHost) Settings.LoadedTrackMode = Settings.StaticTrackMode;
        
        bool hardMode = Settings.LoadedTrackMode == Settings.TrackMode.Hard;

        foreach (string entry in targets)
        {
            try
            {
                string[] parts = entry.Split(',');
                string mode = parts[0].Trim();

                // Handle Skip Mode (#)
                if (mode.IndexOf('#') != -1) continue;
                if (hardMode && mode.IndexOf('!') != -1) continue;
                if (!hardMode && mode.IndexOf('i') != -1) continue;
                

                if (mode.IndexOf('S') != -1) HandleSidingMode(parts, mode, entry, allTracks);
                else if (mode.IndexOf('J') != -1) HandleJunctionMode(parts, mode, entry, allTracks);
                else if (mode.IndexOf('T') != -1) HandleTrackSegmentMode(parts, mode, entry, allTracks);
                else if (mode.IndexOf('N') != -1) HandleNodeInsertMode(parts, mode, entry, allTracks);
                else if (mode.IndexOf('X') != -1) HandleRemoveMode(parts, entry, allTracks);
                else if (mode.IndexOf('A') != -1) HandleTrackAbandonMode(parts,mode, entry, allTracks);
                else if (mode.IndexOf('R') != -1) HandleRenameMode(parts, entry, allTracks);
                else if (mode.IndexOf('t') != -1) HandleTransformNodeMode(parts, entry, allTracks);
                else if (mode.IndexOf('O') != -1) HandleObjectOffsetMode(parts, entry);
                else if (mode.IndexOf('C') != -1) HandleRemoveSceneMode(parts, entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to execute track placement action '{entry}' due to exception:");
                Debug.LogError(e);
            }
        }

        foreach (RailTrack track in  allTracks)
        {
            if (!track.name.Any(char.IsDigit)) track.name += " "+((int)track.transform.position.magnitude % 100);
            AccessTools.Field(typeof(RailTrack), "pointSet").SetValue(track, null);
        }
        RailTrack.pointSets.Clear();
        new GameObject("signPlacer", typeof(CustomSignPlacer));
    }

    #region Mode Handlers

    // Mode 'S' - Siding Creation
    private static void HandleSidingMode(string[] parts, string mode, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 7)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        string rawStart = parts[2].Trim();
        string rawEnd = parts[3].Trim();

        bool deadEndStart = rawStart.StartsWith("X", StringComparison.OrdinalIgnoreCase);
        bool deadEndEnd = rawEnd.StartsWith("X", StringComparison.OrdinalIgnoreCase);

        if (!int.TryParse(deadEndStart ? rawStart.Substring(1) : rawStart, out int startIdx)) return;
        if (!int.TryParse(deadEndEnd ? rawEnd.Substring(1) : rawEnd, out int endIdx)) return;
        if (!float.TryParse(parts[4], out float xzOffset)) return;
        if (!float.TryParse(parts[5], out float searchX)) return;
        if (!float.TryParse(parts[6], out float searchZ)) return;

        string name = AllTracksPatch.trackCounter+"";
        string id = "";
        if (parts.Length > 7)
        {
            name = parts[7].Trim();
            id = name;
        }

        AllTracksPatch.trackCounter++;
        Vector3 searchPos = new Vector3(searchX, 0, searchZ);

        RailTrack template = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z)))
            .FirstOrDefault();

        if (template == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        template.gameObject.SetActive(false);

        RailTrack newTrack = Object.Instantiate(template);
        newTrack.generateColliders = true;
        newTrack.name = targetName + "_Siding";

        BezierOffsetTool.CreateParallelCopy(template.curve, startIdx, endIdx, xzOffset, 0, newTrack.gameObject);

        newTrack.curve.transform.parent = newTrack.transform;
        newTrack.inBranch = new Junction.Branch();
        newTrack.outBranch = new Junction.Branch();
        newTrack.inJunction = null;
        newTrack.outJunction = null;

        RailTrack[] tempA = AllTracksPatch.SplitTrackForJunction(template, startIdx);
        RailTrack[] tempB = AllTracksPatch.SplitTrackForJunction(tempA[1], endIdx - startIdx);
        RailTrack[] newMains = new[] { tempA[0], tempB[0], tempB[1] };

        Object.Destroy(tempA[1].gameObject);

        if (mode.IndexOf('M') == -1)
        {
            if (mode.IndexOf('I') == -1)
            {
                newTrack.name = "[y]_[doubletrack]_[Siding-" + name + "-D]";
                newMains[1].name = "[y#]_[doubletrack]_[Main-" + name + "-T]";
            }
            else
            {
                newMains[1].name = "[y]_[doubletrack]_[Siding-" + name + "-T]";
                newTrack.name = "[y#]_[doubletrack]_[Main-" + name + "-D]";
            }
        }
        else
        {
            newTrack.name = "DT-" + name + "-D";
            newMains[1].name = "DT-" + name + "-T";
        }

        AllTracksPatch.SetUpPrefabJunction(newMains[0], newMains[1], newTrack, xzOffset, "Split", true, defaultLeft: (mode.IndexOf('I') == -1 ^ xzOffset > 0), id: id);
        AllTracksPatch.SetUpPrefabJunction(newMains[2], newMains[1], newTrack, xzOffset, "Merge", false, defaultLeft: (mode.IndexOf('I') != -1 ^ xzOffset > 0), id: id);

        //if (mode.IndexOf('M') == -1) AllTracksPatch.SetupComponent(newMains[1], newTrack, xzOffset);

        AllTracksPatch.AddedTracks.Add(newTrack);
        AllTracksPatch.AddedTracks.Add(newMains[1]);

        newTrack.transform.SetParent(AllTracksPatch.railwayParent);
        newTrack.gameObject.SetActive(true);

        foreach (RailTrack track in newMains)
        {
            track.curve.transform.parent = track.transform;
            track.transform.SetParent(AllTracksPatch.railwayParent);
            track.gameObject.SetActive(true);
        }

        allTracks.Remove(template);
        allTracks.Add(newMains[1]);
        allTracks.Add(newTrack);

        newMains[0].name = template.name + "A";
        newMains[2].name = template.name + "B";

        if (!allTracks.Contains(newMains[0])) allTracks.Add(newMains[0]);
        if (!allTracks.Contains(newMains[2])) allTracks.Add(newMains[2]);

        Object.Destroy(template.gameObject);
    }

    // Mode 'J' - Complex Junction Setup
    private static void HandleJunctionMode(string[] parts, string mode, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 10)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        if (!int.TryParse(parts[2], out int node)) return;
        if (!bool.TryParse(parts[3], out bool isDiverge)) return;
        if (!bool.TryParse(parts[4], out bool flipDirection)) return;
        if (!bool.TryParse(parts[5], out bool mirror)) return;
        if (!float.TryParse(parts[6], out float searchX)) return;
        if (!float.TryParse(parts[7], out float searchZ)) return;
        if (!float.TryParse(parts[8], out float tanScale)) return;
        string junctionID = parts[9].Trim();

        Vector3 searchPos = new Vector3(searchX, 0, searchZ);
        int offset = mirror ? -1 : 1;

        RailTrack target = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z)))
            .FirstOrDefault();

        if (target == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        target.gameObject.SetActive(false);

        RailTrack[] splitTrack = AllTracksPatch.SplitTrackForJunction(target, node);
        if (!flipDirection) (splitTrack[0], splitTrack[1]) = (splitTrack[1], splitTrack[0]);

        Junction newJunction;
        if (!isDiverge)
            newJunction = AllTracksPatch.SetUpPrefabJunction(splitTrack[0], splitTrack[1], null, offset, "Complex", flipDirection, mode.IndexOf('M') == -1, junctionID, defaultLeft: mode.IndexOf('I') != -1);
        else
            newJunction = AllTracksPatch.SetUpPrefabJunction(splitTrack[0], null, splitTrack[1], offset, "Complex", flipDirection, mode.IndexOf('M') == -1, junctionID, defaultLeft: mode.IndexOf('I') != -1);

        splitTrack[0].gameObject.name = targetName + "A";
        splitTrack[1].gameObject.name = targetName + "B";
        
        
        foreach (RailTrack track in splitTrack)
        {
            track.curve.SetDirty();
            track.curve.transform.parent = track.transform;
            track.transform.SetParent(AllTracksPatch.railwayParent);
            track.gameObject.SetActive(true);
        }

        RailTrack endTrack = newJunction.outBranches[newJunction.outBranches[0].track.outBranch.track == null ? 0 : 1].track;
        AllTracksPatch.ComplexJunctionEnds.Add(junctionID, endTrack);

        float mag = 1f / AllTracksPatch.CalcTanSacle(flipDirection, splitTrack[1].curve);
        splitTrack[1].curve.ScaleEndTangents(!flipDirection, mag * tanScale);

        allTracks.Remove(target);
        allTracks.Add(splitTrack[0]);
        allTracks.Add(splitTrack[1]);

        if (mode.IndexOf('E') != -1)
        {
            AllTracksPatch.AddedTracks.Add(splitTrack[0]);
            AllTracksPatch.AddedTracks.Add(splitTrack[1]);
            AllTracksPatch.AddedTracks.Add(newJunction.outBranches[0].track);
            AllTracksPatch.AddedTracks.Add(newJunction.outBranches[1].track);
        }

        if (AllTracksPatch.AddedTracks.Contains(target)) AllTracksPatch.AddedTracks.Remove(target);
        Object.Destroy(target.gameObject);
    }

    // Mode 'T' - Custom Track Segment Generation
    private static void HandleTrackSegmentMode(string[] parts, string mode, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 5)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string inTargetID = parts[1].Trim();
        string outTargetID = parts[2].Trim();
        string name = parts[3].Trim();
        string points = parts[4].Trim();

        RailTrack template = allTracks[0];
        template.gameObject.SetActive(false);
        RailTrack newTrack = Object.Instantiate(template);
        template.gameObject.SetActive(true);
        
        List<BezierOffsetTool.TrackSegmentData> pointsList = BezierOffsetTool.ParseTrackData(points);
        BezierCurve curve = newTrack.GetComponent<BezierCurve>();
        BezierPoint[] oldPoints = curve.GetAnchorPoints();
        BezierPoint[] pointsStrandedGo = newTrack.transform.GetComponentsInChildren<BezierPoint>();
        
        for (int i = 0; i < oldPoints.Length; i++)
        {
            curve.RemovePoint(oldPoints[i]);
            Object.Destroy(pointsStrandedGo[i].gameObject);
        }

        BezierPoint startPoint = Object.Instantiate(AllTracksPatch.ComplexJunctionEnds[inTargetID].curve.Last(), curve.transform, true);
        startPoint.handleStyle = BezierPoint.HandleStyle.Broken;
        BezierPoint endPoint = null;

        if (outTargetID != "")
        {
            endPoint = Object.Instantiate(AllTracksPatch.ComplexJunctionEnds[outTargetID].curve.Last(), curve.transform, true);
            endPoint.handleStyle = BezierPoint.HandleStyle.Broken;
        }

        startPoint.transform.localRotation = Quaternion.Euler(Vector3.zero);
        if (outTargetID != "") endPoint.transform.localRotation = Quaternion.Euler(Vector3.zero);

        curve.AddPoint(startPoint);
        startPoint._curve = curve;
        
        Vector3 searchPos = AllTracksPatch.ComplexJunctionEnds[inTargetID].transform.position;

        foreach (BezierOffsetTool.TrackSegmentData segment in pointsList)
        {
            string key = segment.TrackName;
            float[] paramsList = segment.Parameters;

            float tan1Lenght = 1;
            float tan2Lenght = 1;
            float yOffset = 0;

            if (paramsList.Length > 4) searchPos = new Vector3(paramsList[3], 0, paramsList[4]);
            if (paramsList.Length > 5) yOffset = paramsList[5];
            if (paramsList.Length > 6) tan1Lenght = paramsList[6];
            tan2Lenght = paramsList.Length > 7 ? paramsList[7] : tan1Lenght;

            BezierCurve targetCurve = allTracks
                .Where(t => t.name == key)
                .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchPos.x, searchPos.z)))
                .FirstOrDefault()?.curve;

            if (targetCurve == null)
            {
                Debug.LogWarning($"[DoubleTrack] Could not find '{key}' near {searchPos.x}, {searchPos.z}");
                continue;
            }

            int startIndex = (int)paramsList[0];
            int endIndex = (int)paramsList[1];
            float xzOffset = paramsList[2];

            bool invert = false;
            if (startIndex > endIndex)
            {
                (startIndex, endIndex) = (endIndex, startIndex);
                invert = true;
            }
            else if (startIndex == endIndex && tan1Lenght < 0 && tan2Lenght < 0)
            {
                invert = true;
                tan1Lenght *= -1;
                tan2Lenght *= -1;
            }

            GameObject tempGO = new GameObject("temp");
            BezierCurve tempCurve = BezierOffsetTool.CreateParallelCopy(targetCurve, startIndex, endIndex, xzOffset, yOffset, tempGO);
            BezierPoint[] tempPoints = tempCurve.GetAnchorPoints();

            if (invert) Array.Reverse(tempPoints);

            for (int i = 0; i < tempPoints.Length; i++)
            {
                BezierPoint point = tempPoints[i];

                if (invert) (point.handle1, point.handle2) = (point.handle2, point.handle1);
                if (tan1Lenght != tan2Lenght) point.handleStyle = BezierPoint.HandleStyle.Broken;

                if (i == 0) point.handle1 *= tan1Lenght;
                if (i == tempPoints.Length - 1) point.handle2 *= tan2Lenght;

                curve.AddPoint(point);
                point._curve = curve;
                point.transform.parent = curve.transform;
            }

            Object.Destroy(tempGO);
        }

        if (outTargetID != "")
        {
            curve.AddPoint(endPoint);
            endPoint._curve = curve;
        }

        curve.resolution = 0.5f;
        curve.SetDirty();

        newTrack.generateColliders = true;
        newTrack.name = name;
        

        newTrack.curve.transform.parent = newTrack.transform;
        newTrack.inBranch = new Junction.Branch();
        newTrack.outBranch = new Junction.Branch();
        newTrack.inJunction = null;
        newTrack.outJunction = null;

        AllTracksPatch.SnapTrackToPoint(newTrack, true, AllTracksPatch.ComplexJunctionEnds[inTargetID].curve.Last());
        if (outTargetID != "") AllTracksPatch.SnapTrackToPoint(newTrack, false, AllTracksPatch.ComplexJunctionEnds[outTargetID].curve.Last());

        AllTracksPatch.ComplexJunctionEnds[inTargetID].outBranch.track = newTrack;
        AllTracksPatch.ComplexJunctionEnds[inTargetID].outBranch.first = true;

        newTrack.inBranch.track = AllTracksPatch.ComplexJunctionEnds[inTargetID];
        newTrack.inBranch.first = false;

        if (outTargetID != "")
        {
            AllTracksPatch.ComplexJunctionEnds[outTargetID].outBranch.track = newTrack;
            AllTracksPatch.ComplexJunctionEnds[outTargetID].outBranch.first = false;

            newTrack.outBranch.track = AllTracksPatch.ComplexJunctionEnds[outTargetID];
            newTrack.outBranch.first = false;
        }
        else
        {
            AllTracksPatch.CreateBufferStop(newTrack);
        }

        newTrack.transform.SetParent(AllTracksPatch.railwayParent);
        newTrack.gameObject.SetActive(true);

        allTracks.Add(newTrack);

        if (mode.IndexOf('E') != -1) AllTracksPatch.AddedTracks.Add(newTrack);
    }

    // Mode 'N' - Insert Curve Node
    private static void HandleNodeInsertMode(string[] parts, string mode, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 6)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        if (!int.TryParse(parts[2], out int insertAfter)) return;
        if (!float.TryParse(parts[3], out float distance)) return;
        if (!float.TryParse(parts[4], out float searchX)) return;
        if (!float.TryParse(parts[5], out float searchZ)) return;

        RailTrack template = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
            .FirstOrDefault();

        if (template == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        BezierPoint startNode = template.curve.GetAnchorPoints()[insertAfter];
        BezierPoint newNode = mode.IndexOf('I') == -1 ? template.curve.InsertPointAtDistance(startNode, distance) : template.curve.InsertStraightExtension(startNode, distance);

        
        if (parts.Length > 6 && float.TryParse(parts[6], out float scale))
        {
            newNode.handle1 *= scale;
            newNode.handle2 *= scale;
        }
        
        template.curve.SetDirty();
        
    }

    // Mode 'X' - Remove Point or Track
    private static void HandleRemoveMode(string[] parts, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 5)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        if (!int.TryParse(parts[2], out int removeAt)) return;
        if (!float.TryParse(parts[3], out float searchX)) return;
        if (!float.TryParse(parts[4], out float searchZ)) return;

        RailTrack template = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
            .FirstOrDefault();

        if (template == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        if (removeAt == -1) Object.Destroy(template.gameObject);
        else template.curve.RemovePoint(removeAt);
    }

    // Mode 'A' - Abandon Track
    private static void HandleTrackAbandonMode(string[] parts, string mode, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 6)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetID = parts[1].Trim();
        if (!float.TryParse(parts[2], out float searchX)) return;
        if (!float.TryParse(parts[3], out float searchZ)) return;
        if (!int.TryParse(parts[4], out int startNode)) return;
        if (!int.TryParse(parts[5], out int endNode)) return;

        RailTrack target = allTracks
                .Where(t => t.name == targetID)
                .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
                .FirstOrDefault();
        target.gameObject.SetActive(false);
        
        if (target == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{target}' near {searchX}, {searchZ}");
            return;
        }
        
        if (endNode < 0) endNode += target.curve.pointCount;
        if (startNode < 0) startNode += target.curve.pointCount;
        
        if(startNode < 0 || startNode >= target.curve.pointCount || endNode < 0 || endNode >= target.curve.pointCount)
        {
            Debug.LogError("Invalid Point Setup");
            return;
        }

        Debug.Log(startNode+" "+endNode);
        RailTrack[] tempA;
        if(startNode > 0)
        {
            tempA = AllTracksPatch.SplitTrackForJunction(target, startNode);
            
        }
        else
        {
            RailTrack newTrack = Object.Instantiate(target);
            
            newTrack.curve.transform.parent = newTrack.transform;
            newTrack.inBranch = new Junction.Branch();
            newTrack.outBranch = new Junction.Branch();
            newTrack.inJunction = null;
            newTrack.outJunction = null;
            
            BezierOffsetTool.CreateParallelCopy(target.curve, 0, target.curve.pointCount-1, 0, 0,newTrack.gameObject);
            
            if (target.inJunction != null)
            {
                newTrack.inJunction = target.inJunction;
                if (target.inJunction.inBranch.track == target) newTrack.inJunction.inBranch.track = newTrack;
                if (target.inJunction.outBranches[0].track == target)
                    newTrack.inJunction.outBranches[0].track = newTrack;
                if (target.inJunction.outBranches[1].track == target)
                    newTrack.inJunction.outBranches[1].track = newTrack;
            }

            if (target.outJunction != null)
            {
                newTrack.outJunction = target.outJunction;
                if (target.outJunction.inBranch.track == target) newTrack.outJunction.inBranch.track = newTrack;
                if (target.outJunction.outBranches[0].track == target)
                    newTrack.outJunction.outBranches[0].track = newTrack;
                if (target.outJunction.outBranches[1].track == target)
                    newTrack.outJunction.outBranches[1].track = newTrack;
            }
            
            if (target.inBranch?.track != null)
            {
                newTrack.inBranch.track = target.inBranch.track;
                if (target.inBranch.track.outBranch.track == target) target.inBranch.track.outBranch.track = newTrack;
                else target.inBranch.track.inBranch.track = newTrack;
            }
            if (target.outBranch?.track != null)
            {
                newTrack.outBranch.track = target.outBranch.track;
                if (target.outBranch.track.outBranch.track == target) target.outBranch.track.outBranch.track = newTrack;
                else target.outBranch.track.inBranch.track = newTrack;
            }
            
            AccessTools.Field(typeof(RailTrack), "initialized").SetValue(target, false);
            tempA = new[] { null, newTrack };
            
        }
        RailTrack[] tempB = {tempA[1],null};
        if(endNode - startNode < tempA[1].curve.pointCount-1) tempB = AllTracksPatch.SplitTrackForJunction(tempA[1], endNode - startNode);
        
        RailTrack[] newTracks = new[] { tempA[0], tempB[0], tempB[1] };
        
        newTracks[1].railType = ScriptableObject.CreateInstance<RailType>();
        newTracks[1].railType.railShape = target.railType.railShape;
        newTracks[1].railType.rotationKinkScale *= 12;
        newTracks[1].railType.verticalKinkScale *= 4;
        newTracks[1].railType.kinkFrequency *= 4;
        newTracks[1].railType.railMaterial = s_oldMat;
        
        newTracks[1].name = "[Y]_[doubletrack]_[Abandoned-0-Track]";
        
        //signal fix
        /*
        UnityModManager.ModEntry SignalsMod = UnityModManager.FindMod("DVSignals");
        if (SignalsMod != null && SignalsMod.Active)
        {
            if (newTracks[0]!=null)
            {
                AllTracksPatch.DisconnectTracks(newTracks[1], newTracks[0]);
            }
            if (newTracks[2]!=null)
            {
                AllTracksPatch.DisconnectTracks(newTracks[1], newTracks[2]);
            }
        }*/

        if(newTracks[0]!=null)AllTracksPatch.CreateBufferStop(newTracks[0]);
        if(newTracks[2]!=null)AllTracksPatch.CreateBufferStop(newTracks[2], true);
        Object.Destroy(target.gameObject);
        
        foreach (RailTrack track in newTracks)
        {
            if(track==null)continue;
            track.curve.transform.parent = track.transform;
            track.transform.SetParent(AllTracksPatch.railwayParent);
            track.gameObject.SetActive(true);
        }
    }
    
    // Mode 'R' - Rename Track
    private static void HandleRenameMode(string[] parts, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 5)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        string newName = parts[2].Trim();
        if (!float.TryParse(parts[3], out float searchX)) return;
        if (!float.TryParse(parts[4], out float searchZ)) return;

        RailTrack template = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
            .FirstOrDefault();

        if (template == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        template.name = newName;
    }

    // Mode 't' - Local Transformations on a Node
    private static void HandleTransformNodeMode(string[] parts, string entry, List<RailTrack> allTracks)
    {
        if (parts.Length < 6)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string targetName = parts[1].Trim();
        if (!int.TryParse(parts[2], out int nodeNum)) return;
        if (!float.TryParse(parts[3], out float searchX)) return;
        if (!float.TryParse(parts[4], out float searchZ)) return;
        string transformMode = parts[5].Trim();

        RailTrack template = allTracks
            .Where(t => t.name == targetName)
            .OrderBy(t => Vector2.Distance(new Vector2(t.transform.position.x, t.transform.position.z), new Vector2(searchX, searchZ)))
            .FirstOrDefault();

        if (template == null)
        {
            Debug.LogWarning($"[DoubleTrack] Could not find '{targetName}' near {searchX}, {searchZ}");
            return;
        }

        BezierPoint node = template.curve.GetAnchorPoints()[nodeNum];
        if(node == null)
        {
            Debug.Log("Node Is Null!");
            return;
        }

        switch (transformMode)
        {
            case "r":
                if (!float.TryParse(parts[6], out float rx)) return;
                if (!float.TryParse(parts[7], out float ry)) return;
                if (!float.TryParse(parts[8], out float rz)) return;
                BezierOffsetTool.RotatePointHandles(node, new Vector3(rx, ry, rz));
                break;

            case "m":
                if (!float.TryParse(parts[6], out float mx)) return;
                if (!float.TryParse(parts[7], out float my)) return;
                if (!float.TryParse(parts[8], out float mz)) return;
                BezierOffsetTool.MovePointNode(node, new Vector3(mx, my, mz), true);
                break;

            case "s":
                if (!float.TryParse(parts[6], out float sx)) return;
                if (!float.TryParse(parts[7], out float sy)) return;
                node.handle1 *= sx;
                node.handle2 *= sy;
                break;
        }

        template.curve.dirty = true;
    }

    // Mode 'O' - Offset Geometry Objects across Scenes
    private static void HandleObjectOffsetMode(string[] parts, string entry)
    {
        if (parts.Length < 8)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string sceneName = parts[1].Trim();
        string objectName = parts[2].Trim();

        if (!float.TryParse(parts[3], out float fromX)) return;
        if (!float.TryParse(parts[4], out float fromY)) return;
        if (!float.TryParse(parts[5], out float fromZ)) return;
        if (!float.TryParse(parts[6], out float toX)) return;
        if (!float.TryParse(parts[7], out float toY)) return;
        if (!float.TryParse(parts[8], out float toZ)) return;

        RailTrackSceneManager.MovedGeometryObject movedGeometryObject = new RailTrackSceneManager.MovedGeometryObject()
        {
            Name = objectName,
            NewPosition = new Vector3(toX, toY, toZ),
            OldPosition = new Vector3(fromX, fromY, fromZ),
        };

        if (!RailTrackSceneManager.MovedObjectsRegistry.ContainsKey(sceneName))
            RailTrackSceneManager.MovedObjectsRegistry.Add(sceneName, new List<RailTrackSceneManager.MovedGeometryObject>());

        RailTrackSceneManager.MovedObjectsRegistry[sceneName].Add(movedGeometryObject);
    }

    // Mode 'C' - Remove Scenes
    private static void HandleRemoveSceneMode(string[] parts, string entry)
    {
        if (parts.Length < 2)
        {
            Debug.LogWarning($"[DoubleTrack] Skipping invalid line (insufficient params): {entry}");
            return;
        }

        string sceneName = parts[1].Trim();
        if (!RailTrackSceneManager.RemovedScenes.Contains(sceneName))
            RailTrackSceneManager.RemovedScenes.Add(sceneName);
    }

    #endregion
}