using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class BezierOffsetTool : MonoBehaviour
{
    /// <summary>
    /// Creates a parallel segment of an existing BezierCurve with independent horizontal and vertical offsets.
    /// </summary>
    /// <param name="originalCurve">The source curve.</param>
    /// <param name="startIndex">Index of the first point to copy.</param>
    /// <param name="endIndex">Index of the last point to copy.</param>
    /// <param name="xzOffset">Distance to the side (relative to curve direction).</param>
    /// <param name="yOffset">Distance to move up or down (world Y-axis).</param>
    /// <returns>A new BezierCurve segment.</returns>
    public static BezierCurve CreateParallelCopy(BezierCurve originalCurve, int startIndex, int endIndex, float xzOffset, float yOffset)
    {
        // 1. Setup the new Curve Container
        GameObject offsetGo = new GameObject($"{originalCurve.name}_Offset_XZ{xzOffset}_Y{yOffset}");
        BezierCurve newCurve = offsetGo.AddComponent<BezierCurve>();
        
        newCurve.resolution = originalCurve.resolution;
        newCurve.drawColor = Color.green;

        startIndex = Mathf.Clamp(startIndex, 0, originalCurve.pointCount - 1);
        endIndex = Mathf.Clamp(endIndex, startIndex, originalCurve.pointCount - 1);

        for (int i = startIndex; i <= endIndex; i++)
        {
            BezierPoint originalPoint = originalCurve[i];
            
            // 2. Calculate the Forward Tangent
            // We need this to know which way is "side"
            Vector3 tangent;
            if (originalPoint.handleStyle != BezierPoint.HandleStyle.None && originalPoint.handle2 != Vector3.zero)
            {
                tangent = originalPoint.globalHandle2 - originalPoint.position;
            }
            else if (i < originalCurve.pointCount - 1)
            {
                tangent = originalCurve[i + 1].position - originalPoint.position;
            }
            else if (i > 0)
            {
                tangent = originalPoint.position - originalCurve[i - 1].position;
            }
            else
            {
                tangent = Vector3.forward; // Absolute fallback for single-point curves
            }

            // 3. Calculate the side vector (XZ Plane)
            // We ignore any verticality in the tangent for the cross product to keep the offset "flat"
            Vector3 flatteningTangent = new Vector3(tangent.x, 0, tangent.z).normalized;
            if (flatteningTangent.sqrMagnitude < 0.001f) flatteningTangent = Vector3.forward;

            Vector3 sideVector = Vector3.Cross(flatteningTangent, Vector3.up).normalized;
            
            // 4. Combine offsets
            // Horizontal shift + Vertical shift
            Vector3 totalOffset = (sideVector * xzOffset) + (Vector3.up * yOffset);
            Vector3 finalPosition = originalPoint.position + totalOffset;

            // 5. Apply to new point
            BezierPoint newPoint = newCurve.AddPointAt(finalPosition);
            newPoint.handleStyle = originalPoint.handleStyle;
            
            // Mirror handles exactly so the curve shape matches the original
            newPoint.handle1 = originalPoint.handle1;
            newPoint.handle2 = originalPoint.handle2;
        }

        return newCurve;
    }
    
    public static void TruncateCurve(BezierCurve curve, float distanceToShrink, bool useFirst)
{
    if (curve == null || curve.pointCount < 2) return;

    // 1. Identify the segment to modify
    int p0Idx = useFirst ? 0 : curve.pointCount - 2;
    int p1Idx = useFirst ? 1 : curve.pointCount - 1;

    Vector3 p0 = curve[p0Idx].position;
    Vector3 p1 = curve[p0Idx].globalHandle2;
    Vector3 p2 = curve[p1Idx].globalHandle1;
    Vector3 p3 = curve[p1Idx].position;

    // 2. Calculate T
    // If useFirst, we want to find the point 'distanceToShrink' from the START.
    // If !useFirst, we want the point 'distanceToShrink' from the END.
    float t;
    if (useFirst)
    {
        t = GetTForDistance(distanceToShrink, p0, p1, p2, p3);
    }
    else
    {
        // Reuse the logic to find T from the end
        t = GetTForDistanceBackwards(distanceToShrink, p0, p1, p2, p3);
    }

    // 3. De Casteljau Math
    Vector3 q0 = Vector3.Lerp(p0, p1, t);
    Vector3 q1 = Vector3.Lerp(p1, p2, t);
    Vector3 q2 = Vector3.Lerp(p2, p3, t);
    Vector3 r0 = Vector3.Lerp(q0, q1, t);
    Vector3 r1 = Vector3.Lerp(q1, q2, t);
    Vector3 b  = Vector3.Lerp(r0, r1, t);

    // 4. Apply Changes
    if (useFirst)
    {
        // Shrinking the START:
        // Point 0 moves to B. 
        // Point 0's Out-Handle moves to r1.
        // Point 1's In-Handle moves to q2.
        curve[p0Idx].position = b;
        curve[p0Idx].handleStyle = BezierPoint.HandleStyle.Broken;
        curve[p0Idx].globalHandle1 = b + (b - r1); // Maintain a straight projection back
        curve[p0Idx].globalHandle2 = r1;

        curve[p1Idx].globalHandle1 = q2;
        
        Debug.Log($"[TrackMod] Truncated START of {curve.name}. New Start: {b}");
    }
    else
    {
        // Shrinking the END:
        // Point 0's Out-Handle moves to q0.
        // Point 1 moves to B.
        // Point 1's In-Handle moves to r0.
        curve[p0Idx].globalHandle2 = q0;

        curve[p1Idx].position = b;
        curve[p1Idx].handleStyle = BezierPoint.HandleStyle.Broken;
        curve[p1Idx].globalHandle1 = r0;
        curve[p1Idx].globalHandle2 = b + (b - r0);
        
        Debug.Log($"[TrackMod] Truncated END of {curve.name}. New End: {b}");
    }

    curve.dirty = true;
}

    private static float GetTForDistance(float targetDistance, params Vector3[] points)
    {
        float totalLength = 0;
        int samples = 100;
        float[] dists = new float[samples + 1];
        Vector3 prev = points[0];

        for (int i = 1; i <= samples; i++)
        {
            Vector3 curr = BezierCurve.GetPoint(i / (float)samples, points);
            totalLength += Vector3.Distance(prev, curr);
            dists[i] = totalLength;
            prev = curr;
        }

        // Clamp target to ensure we don't go out of bounds
        float actualTarget = Mathf.Clamp(targetDistance, 0.001f, totalLength - 0.001f);

        for (int i = 1; i <= samples; i++)
        {
            if (dists[i] >= actualTarget)
            {
                float segmentT = (actualTarget - dists[i - 1]) / (dists[i] - dists[i - 1]);
                return ((i - 1) + segmentT) / samples;
            }
        }
        return 0.999f;
    }

    /// <summary>
    /// Finds the T value (0 to 1) at a specific distance from the END of the segment.
    /// </summary>
    private static float GetTForDistanceBackwards(float distFromEnd, params Vector3[] points)
    {
        float totalLen = 0;
        int samples = 100;
        float[] dists = new float[samples + 1];
        Vector3 prev = points[0];

        for (int i = 1; i <= samples; i++)
        {
            Vector3 curr = BezierCurve.GetPoint(i / (float)samples, points);
            totalLen += Vector3.Distance(prev, curr);
            dists[i] = totalLen;
            prev = curr;
        }

        // The target T is at (Total Length - Distance from end)
        float target = Mathf.Max(0.001f, totalLen - distFromEnd);
    
        for (int i = 1; i <= samples; i++)
        {
            if (dists[i] >= target)
            {
                float ratio = (target - dists[i - 1]) / (dists[i] - dists[i - 1]);
                return ((i - 1) + ratio) / samples;
            }
        }
        return 0.001f;
    }
}