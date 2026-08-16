using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DoubleTrack;
using HarmonyLib;
using Unity.Linq;
using UnityEngine;

namespace DV.Signs;

public class CustomSignPlacer : MonoBehaviour
{
    #region Configuration & Constants

    // Configurable Sign Placement Distances & Thresholds
    private const float trackEndSpacing = 40f;
    private const float firstTrackSignDistance = 40f;
    private const float signPullBackDistance = 100f;
    private const float junctionWarningDistance = 300f;

    // Speed Limits
    private const float lastSegmentConnectedMaxSpeed = 60f;
    private const float lastSegmentUnconnectedMaxSpeed = 20f;
    private const float yardOrShortTrackMaxSpeed = 60f;

    // Curve & Grade Tolerances
    private const float minTrackLength = 100f;
    private const float flatGradeThreshold = 0.005f;
    private const float speedZeroRemoverMult = 0.1f;
    private const float preventSpeedUpRange = 200f;
    private const float speedAheadArrowTolerance = 20f;

    [Header("Placement Parameters")]
    public float minArcLength = 300f;
    public float error = 1f;
    public float minimizeSpeedDiffThreshold = 30f;
    public float minimizeSpeedSegmentLengthThreshold = 300f;
    public float lastSegmentOptimalLength = 250f;
    public float sideways = 2f;

    [Header("Hierarchy & Target Controls")]
    public Transform parentTo;
    public bool clearParent = true;
    public List<string> noSignsTrackNameMarks = new List<string>();

    [Header("Debug Controls")]
    public bool sceneViewReverse;
    public bool mergeSameSpeedSigns = true;
    public bool minimizeSpeedDifference = true;
    public bool placeTrackSigns = true;
    public bool placeJunctionSigns = true;
    
    #endregion

    public static List<GameObject> signs;

    #region Unity Lifecycle & Core Placement Entry

    private void Start()
    {
        StartCoroutine(PlaceSigns());
    }

    public IEnumerator PlaceSigns()
    {
        yield return new WaitForSeconds(1f);
        Debug.Log("[Double Track] Placing signs");
        
        // Clear existing generated signs if requested
        if (parentTo != null && clearParent)
        {
            foreach (GameObject child in parentTo.gameObject.Children().ToList())
            {
                DestroyImmediate(child);
            }
        }

        List<SignData> signDataList = new List<SignData>();

        if (placeTrackSigns)
            signDataList.AddRange(GetTrackSigns());

        if (placeJunctionSigns)
            signDataList.AddRange(GetJunctionSigns());

        // Filter out marked tracks
        if (noSignsTrackNameMarks != null && noSignsTrackNameMarks.Count > 0)
        {
            signDataList.RemoveAll(signData =>
                noSignsTrackNameMarks.Any(mark => signData.GetTrack().name.StartsWith(mark)));
        }

        signs = signDataList.Select(PlaceSign).ToList();
    }

    private static readonly AccessTools.FieldRef<Sign, List<SignParameters>> GetSignParametersRef = 
        AccessTools.FieldRefAccess<Sign, List<SignParameters>>("signParameters");

    private GameObject PlaceSign(SignData signData)
    {
        GameObject signGo = signData.GetSign().Make();
        signGo.transform.position = signData.GetPosition();
        signGo.transform.rotation = signData.GetRotation();

        // Offset sideways relative to the track direction
        signGo.transform.Translate(-sideways, 0f, 0f, Space.Self);

        // Keep sign upright (zero out pitch/roll)
        Vector3 rot = signGo.transform.localRotation.eulerAngles;
        signGo.transform.localRotation = Quaternion.Euler(0f, rot.y, rot.z);

        // Access the internal struct list via FieldRef
        List<SignParameters> signParamsList = GetSignParametersRef(signData.GetSign());

        string debugText = string.Empty;
        if (signParamsList != null)
        {
            // Struct check: compare signText directly instead of checking the struct against null
            debugText = string.Join("\n",
                signParamsList
                    .Where(sp => !string.IsNullOrWhiteSpace(sp.signText))
                    .Select(sp => sp.signText));
        }

        signGo.AddComponent<SignDebug>().text = debugText;

        Transform targetParent = parentTo != null ? parentTo : OriginShift.OriginShift.parentContainer;
        signGo.transform.SetParent(targetParent);

        return signGo;
    }

    #endregion

    #region Track Signs Logic

    private List<SignData> GetTrackSigns()
    {
        RailTrack singleTarget = GetComponent<RailTrack>();
        IEnumerable<RailTrack> targetTracks = singleTarget != null
            ? new[] { singleTarget }
            : FindObjectsOfType<RailTrack>().Where(rt => ShouldIncludeTrack(rt));

        List<SignData> trackSigns = new List<SignData>();
        foreach (RailTrack track in targetTracks)
        {
            // Evaluates both directions; sceneViewReverse flips which direction is calculated first
            trackSigns.AddRange(GetTrackSigns(track, reverse: sceneViewReverse));
            trackSigns.AddRange(GetTrackSigns(track, reverse: !sceneViewReverse));
        }

        return trackSigns;
    }

    private List<SignData> GetTrackSigns(RailTrack rt, bool reverse)
    {
        BezierCurve curve = rt.curve;
        List<CurveSegmentInfo> segmentInfos = GetSegmentInfos(curve, error, minArcLength, reverse);
        segmentInfos = SplitLastSegmentIfNeeded(segmentInfos, lastSegmentOptimalLength);

        CurveSegmentInfo lastSegment = segmentInfos.Last();

        bool isConnected = reverse ? rt.inIsConnected : rt.outIsConnected;
        bool hasJunction = (reverse ? rt.inJunction : rt.outJunction) != null;
        Junction.Branch branch = reverse ? rt.inBranch : rt.outBranch;

        bool isDivergingDangling = isConnected && !hasJunction && branch.track != null && branch.track.name == "[track diverging]";

        float maxSpeedCap = float.PositiveInfinity;
        if (!isConnected)
        {
            maxSpeedCap = lastSegmentUnconnectedMaxSpeed; // Default: 20
        }
        else if (isDivergingDangling || hasJunction)
        {
            maxSpeedCap = lastSegmentConnectedMaxSpeed; // Default: 60
        }
        else
        {
            CurveSegmentInfo nextInfo = GetSegmentInfoAfterJunction(branch);
            if (nextInfo != null)
                maxSpeedCap = nextInfo.GetSpeed();
        }

        if (lastSegment.GetSpeed() > maxSpeedCap)
            lastSegment.assignedSpeed = Mathf.Clamp(lastSegment.GetSpeed(), 0f, maxSpeedCap);

        if (mergeSameSpeedSigns)
            segmentInfos = MergeSameSpeed(segmentInfos);

        if (minimizeSpeedDifference)
            segmentInfos = MinimizeSpeedDifference(segmentInfos, minimizeSpeedDiffThreshold, minimizeSpeedSegmentLengthThreshold);

        List<SignData> signs = new List<SignData>();

        for (int i = 0; i < segmentInfos.Count; i++)
        {
            CurveSegmentInfo current = segmentInfos[i];
            CurveSegmentInfo prev = segmentInfos[Mathf.Max(0, i - 1)];
            CurveSegmentInfo next = segmentInfos[Mathf.Min(i + 1, segmentInfos.Count - 1)];

            // Smooth out speed dips on short segments
            if (current.GetSpeed() > next.GetSpeed() && current.segmentLength <= preventSpeedUpRange)
                current.assignedSpeed = next.GetSpeed();

            float placementT;
            if (i == 0)
            {
                bool startConnected = reverse ? rt.outIsConnected : rt.inIsConnected;
                if (!startConnected) continue;

                float offsetT = firstTrackSignDistance / curve.length;
                if (reverse) offsetT *= -1f;

                placementT = Mathf.Clamp01(current.bezierStartT + offsetT);
            }
            else
            {
                float offsetT = 0f;
                if (current.GetSpeed() < prev.GetSpeed())
                    offsetT = signPullBackDistance / curve.length;

                if (reverse) offsetT *= -1f;

                placementT = Mathf.Clamp01(current.bezierStartT - offsetT);
            }

            SignData signData = new SignData(reverse, rt.curve, placementT);
            Sign sign = signData.GetSign();

            if (i == segmentInfos.Count - 1)
            {
                current.assignedSpeed = Mathf.Min(prev.GetSpeed(), current.GetSpeed(), lastSegmentConnectedMaxSpeed);
                /*if (hasJunction)
                {
                    sign.UpcomingJunction();
                    sign.UpcomingJunctionDistance(current.segmentLength);
                }
                else*/ if (!isConnected)
                {
                    sign.UpcomingTrackEnd();
                }
            }

            sign.SpeedLimit(current.GetSpeed() * speedZeroRemoverMult);

            if (i < segmentInfos.Count - 2 && next.GetSpeed() >= current.GetSpeed() + speedAheadArrowTolerance)
                sign.UpcomingSpeedUp();

            if (next.GetSpeed() <= current.GetSpeed() - speedAheadArrowTolerance || (i == segmentInfos.Count - 1 && current.GetSpeed() > lastSegmentConnectedMaxSpeed))
                sign.UpcomingSpeedDown();

            if (i == 0 || Mathf.Abs(current.GetGradeAngle() - prev.GetGradeAngle()) >= flatGradeThreshold)
                sign.Grade(current.GetGrade(), current.GetGradeAngle());

            signs.Add(signData);
        }

        return signs;
    }

    #endregion

    #region Junction Signs Logic

    private List<SignData> GetJunctionSigns()
    {
        Junction targetJunction = GetComponent<Junction>();
        IEnumerable<Junction> junctions = targetJunction != null
            ? new[] { targetJunction }
            : FindObjectsOfType<Junction>();

        return junctions.SelectMany(GetJunctionSigns).ToList();
    }

    private List<SignData> GetJunctionSigns(Junction j)
    {
        List<SignData> signs = new List<SignData>();

        if (j.inBranch.track == null)
        {
            Debug.LogWarning("Skipping placing signs for junction: no track connected to inBranch", j);
            return signs;
        }

        List<Junction.Branch> outBranches = new List<Junction.Branch>();
        for (int i = 0; i < j.outBranches.Count; i++)
        {
            Junction.Branch branch = j.outBranches[i];
            Junction.Branch targetBranch = branch.first ? branch.track.outBranch : branch.track.inBranch;

            if (targetBranch.track == null)
            {
                Debug.LogWarning($"Skipping placing signs for junction: no track connected to outBranches[{i}]", j);
                return signs;
            }
            outBranches.Add(targetBranch);
        }

        BezierCurve curve = j.inBranch.track.curve;
        
        bool reverse = !j.inBranch.first;

        if (ShouldIncludeTrack(j.inBranch.track) && curve.length > 8f)
        {
            float placementT = trackEndSpacing / curve.length;
            if (reverse) placementT = 1f - placementT;
            SignData signData = GetJunctionSign();
            
            SignData GetJunctionSign(float distance = 0)
            {
                SignData signData = new SignData(!reverse, curve, placementT);
                bool isLeftTurnout = j.transform.parent.name.Contains("left");

                Sign newSign = signData.GetSign();
                if(distance != 0)
                {
                    newSign.UpcomingJunction();
                    newSign.UpcomingJunctionDistance(distance);
                }
                
                for (int i = 0; i < 2; i++)
                {
                    Junction.Branch branch = outBranches[i];
                    List<CurveSegmentInfo> infos = GetSegmentInfos(branch.track.curve, error, minArcLength, !branch.first);

                    if (minimizeSpeedDifference)
                        infos = MinimizeSpeedDifference(infos, minimizeSpeedDiffThreshold, float.MaxValue);

                    CurveSegmentInfo info = infos[0];

                    if (!ShouldIncludeTrack(branch.track))
                        info.assignedSpeed = Mathf.Min(yardOrShortTrackMaxSpeed, info.GetSpeed());

                    // Apply diverging turnout speed restriction (60 km/h)
                    if (i == 1 ^ isLeftTurnout)
                        info.assignedSpeed = Mathf.Min(lastSegmentConnectedMaxSpeed, info.GetSpeed());
                    newSign.SpeedLimit(info.GetSpeed() * speedZeroRemoverMult).Arrow(i == 0);
                }

                return signData;
            }
            
            signs.Add(signData);

            if (curve.length > junctionWarningDistance+firstTrackSignDistance)
            {
                float distance = junctionWarningDistance;
                placementT = distance / curve.length;
                if (reverse) placementT = 1f - placementT;
                signData = GetJunctionSign(distance);
                signs.Add(signData);
            }
            
        }

        return signs;
    }

    private CurveSegmentInfo GetSegmentInfoAfterJunction(Junction.Branch branch)
    {
        if (branch.track == null)
        {
            Debug.LogError("Branch's track is null");
            return null;
        }

        Junction junction = branch.first ? branch.track.outJunction : branch.track.inJunction;
        if (junction == null)
        {
            //Debug.LogError("Expected junction on the other end of the branch");
            return null;
        }

        Junction.Branch inBranch = junction.inBranch;
        RailTrack track = inBranch.track;

        if (track == null)
        {
            Debug.LogWarning("Junction's inBranch is not connected");
            return null;
        }

        bool reverse = !inBranch.first;
        CurveSegmentInfo info = GetSegmentInfos(track.curve, error, minArcLength, reverse)[0];

        if (!ShouldIncludeTrack(track))
            info.assignedSpeed = Mathf.Min(yardOrShortTrackMaxSpeed, info.GetSpeed());

        return info;
    }

    #endregion

    #region Geometry Processing & Utilities

    private static List<CurveSegmentInfo> GetSegmentInfos(BezierCurve curve, float error, float minArcLength, bool reverse)
    {
        List<BezierArcApproximation.Arc> arcs = new List<BezierArcApproximation.Arc>();
        BezierArcApproximation.CalculateArcs(curve, error, arcs);

        if (reverse)
        {
            arcs.Reverse();
            for (int i = 0; i < arcs.Count; i++)
            {
                BezierArcApproximation.Arc arc = arcs[i];
                arcs[i] = arc with
                {
                    s = arc.e,
                    e = arc.s,
                    bezierStartT = arc.bezierEndT,
                    bezierEndT = arc.bezierStartT
                };
            }
        }

        List<List<float>> arcLengthsChunked = SignPlacerUtils.ChunkifyNumbers(
            arcs.Select(a => a.Length).ToList(),
            minArcLength
        );

        List<List<BezierArcApproximation.Arc>> chunkedArcs = new List<List<BezierArcApproximation.Arc>>();
        int arcIndex = 0;

        foreach (List<float> chunk in arcLengthsChunked)
        {
            List<BezierArcApproximation.Arc> currentChunk = new List<BezierArcApproximation.Arc>();
            for (int i = 0; i < chunk.Count; i++)
            {
                currentChunk.Add(arcs[arcIndex]);
                arcIndex++;
            }
            chunkedArcs.Add(currentChunk);
        }

        List<CurveSegmentInfo> segmentInfos = new List<CurveSegmentInfo>();

        for (int i = 0; i < chunkedArcs.Count; i++)
        {
            List<BezierArcApproximation.Arc> arcGroup = chunkedArcs[i];
            CurveSegmentInfo info = new CurveSegmentInfo
            {
                curve = curve,
                bezierStartT = arcGroup.First().bezierStartT,
                bezierEndT = arcGroup.Last().bezierEndT,
                minRadius = float.PositiveInfinity
            };

            foreach (BezierArcApproximation.Arc arc in arcGroup)
            {
                info.segmentLength += arc.Length;
                if (arc.r < info.minRadius)
                    info.minRadius = arc.r;
            }

            // Lookahead blending for incoming tighter curves
            if (i + 1 < chunkedArcs.Count)
            {
                float firstXMeters = info.GetSpeed() * 2f;
                float upcomingRadius = GetMinRadiusInFirstXMeters(chunkedArcs[i + 1], firstXMeters);

                if (upcomingRadius < info.minRadius)
                {
                    float blendedRadius = (Mathf.Clamp(info.minRadius, upcomingRadius, 1200f) + upcomingRadius) / 2f;
                    info.minRadius = blendedRadius;
                }
            }

            segmentInfos.Add(info);
        }

        return segmentInfos;
    }

    private static float GetMinRadiusInFirstXMeters(List<BezierArcApproximation.Arc> arcs, float firstXMeters)
    {
        if (arcs == null || arcs.Count == 0)
        {
            Debug.LogError("Must have at least one arc");
            return 0f;
        }
        if (firstXMeters <= 0.0)
        {
            Debug.LogError("firstXMeters must be positive");
            return 0f;
        }

        float minRadius = float.PositiveInfinity;
        float accumulatedLength = 0f;

        for (int i = 0; i < arcs.Count && accumulatedLength < firstXMeters; i++)
        {
            BezierArcApproximation.Arc arc = arcs[i];
            accumulatedLength += arc.Length;
            if (arc.r < minRadius)
                minRadius = arc.r;
        }

        return minRadius;
    }

    private static List<CurveSegmentInfo> SplitLastSegmentIfNeeded(List<CurveSegmentInfo> infos, float optimalLength)
    {
        List<CurveSegmentInfo> result = new List<CurveSegmentInfo>(infos);
        CurveSegmentInfo last = result.Last();
        bool isReversedT = last.bezierStartT > last.bezierEndT;

        if (last.segmentLength > 2.0f * optimalLength)
        {
            CurveSegmentInfo extraSegment = last.ShallowCopy();
            last.segmentLength -= optimalLength;
            extraSegment.segmentLength = optimalLength;

            float tOffset = optimalLength / last.curve.length;
            if (isReversedT)
                last.bezierEndT += tOffset;
            else
                last.bezierEndT -= tOffset;

            extraSegment.bezierStartT = last.bezierEndT;
            result.Add(extraSegment);
        }
        else if (last.segmentLength > 1.5f * optimalLength)
        {
            CurveSegmentInfo extraSegment = last.ShallowCopy();
            last.segmentLength *= 0.5f;
            extraSegment.segmentLength = last.segmentLength;

            float midT = (last.bezierStartT + last.bezierEndT) * 0.5f;
            last.bezierEndT = midT;
            extraSegment.bezierStartT = midT;

            result.Add(extraSegment);
        }

        return result;
    }

    private static List<CurveSegmentInfo> MergeSameSpeed(List<CurveSegmentInfo> infos)
    {
        List<CurveSegmentInfo> result = new List<CurveSegmentInfo>(infos);

        for (int i = result.Count - 1; i > 0; i--)
        {
            CurveSegmentInfo prev = result[i - 1];
            CurveSegmentInfo current = result[i];

            bool sameGrade = Mathf.Abs(current.GetGradeAngle() - prev.GetGradeAngle()) < 0.005f;
            bool sameSpeed = Mathf.Approximately(prev.GetSpeed(), current.GetSpeed());

            if (sameGrade && sameSpeed)
            {
                prev.bezierEndT = current.bezierEndT;
                prev.segmentLength += current.segmentLength;
                prev.minRadius = Mathf.Min(prev.minRadius, current.minRadius);
                result.RemoveAt(i);
            }
        }

        return result;
    }

    private static List<CurveSegmentInfo> MinimizeSpeedDifference(List<CurveSegmentInfo> infos, float speedDiffThreshold, float segmentLengthThreshold)
    {
        List<CurveSegmentInfo> result = new List<CurveSegmentInfo>(infos);
        var speedLengthTuples = result.Select(i => (i.GetSpeed(), i.segmentLength)).ToList();

        var operations = SignPlacerUtils.MinimizeSpeedDifference(speedLengthTuples, speedDiffThreshold, segmentLengthThreshold);

        foreach (var (op, index, value) in operations)
        {
            switch (op)
            {
                case SignPlacerUtils.Operation.Insert:
                    CurveSegmentInfo prev = result[index - 1];
                    CurveSegmentInfo next = result[index];

                    CurveSegmentInfo inserted = new CurveSegmentInfo
                    {
                        assignedSpeed = value,
                        bezierStartT = (prev.bezierStartT + prev.bezierEndT) / 2f,
                        bezierEndT = next.bezierStartT,
                        curve = prev.curve,
                        minRadius = prev.minRadius
                    };

                    prev.bezierEndT = inserted.bezierStartT;
                    prev.segmentLength /= 2f;
                    inserted.segmentLength = prev.segmentLength;

                    result.Insert(index, inserted);
                    break;

                case SignPlacerUtils.Operation.Update:
                    result[index].assignedSpeed = value;
                    break;

                default:
                    throw new NotImplementedException(op.ToString());
            }
        }

        return result;
    }

    protected static bool ShouldIncludeTrack(RailTrack rt)
    {
        return rt.curve.length >= minTrackLength && !rt.name.StartsWith("[Y]") && !rt.name.StartsWith("[#]");
    }

    #endregion

    #region Nested Support Classes

    protected class CurveSegmentInfo
    {
        public BezierCurve curve;
        public float segmentLength;
        public float minRadius;
        public float bezierStartT;
        public float bezierEndT;
        public float assignedSpeed;

        public float GetSpeed()
        {
            return assignedSpeed > 0f ? assignedSpeed : GetMaxSpeedForRadius();
        }

        public float GetMaxSpeedForRadius()
        {
            if (minRadius < 50f) return 10f;
            if (minRadius < 70f) return 20f;
            if (minRadius < 95f) return 30f;
            if (minRadius < 130f) return 40f;
            if (minRadius < 170f) return 50f;
            if (minRadius < 230f) return 60f;
            if (minRadius < 360f) return 70f;
            if (minRadius < 700f) return 80f;
            if (minRadius < 900f) return 90f;
            return minRadius < 1200f ? 100f : 120f;
        }

        public float GetGradeAngle()
        {
            Vector3 start = curve.GetPointAt(bezierStartT);
            Vector3 end = curve.GetPointAt(bezierEndT);
            return Mathf.Abs(start.y - end.y) / Vector3.Distance(start, end);
        }

        public Grade GetGrade()
        {
            Vector3 start = curve.GetPointAt(bezierStartT);
            Vector3 end = curve.GetPointAt(bezierEndT);

            if (GetGradeAngle() < 0.005f)
                return Grade.Flat;

            return end.y <= start.y ? Grade.Decline : Grade.Incline;
        }

        public CurveSegmentInfo ShallowCopy()
        {
            return (CurveSegmentInfo)MemberwiseClone();
        }
    }

    protected class SignData
    {
        private readonly bool flip;
        private readonly BezierCurve placementCurve;
        private readonly float placementT;
        private Sign sign;

        public SignData(bool flip, BezierCurve placementCurve, float placementT)
        {
            this.flip = flip;
            this.placementCurve = placementCurve ?? throw new ArgumentNullException(nameof(placementCurve));
            this.placementT = placementT;
        }

        public Vector3 GetPosition() => placementCurve.GetPointAt(placementT);

        public Quaternion GetRotation()
        {
            Vector3 forward = placementCurve.GetTangentAt(placementT);
            if (!flip) forward = -forward;
            return Quaternion.LookRotation(forward);
        }

        public Sign GetSign()
        {
            if (sign == null)
            {
                // Hash placement t to seed deterministic sign varieties
                bool altStyle = (placementT * 100f) % 10f < 3f;
                sign = new Sign(altStyle);
            }
            return sign;
        }

        public RailTrack GetTrack() => placementCurve.GetComponent<RailTrack>();
    }

    #endregion
}