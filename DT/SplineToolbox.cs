using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

public static class BezierOffsetTool
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

    public static Dictionary<string, float[]> ParseTrackData(string input)
        {
            Dictionary<string, float[]> result = new Dictionary<string, float[]>();

            if (string.IsNullOrEmpty(input))
            {
                return result;
            }

            // Regex matches everything inside curly braces: {[#] name(p1;p2;...)}
            // \{      : matches literal '{'
            // ([^\}]+): captures everything that isn't a closing curly brace
            // \}      : matches literal '}'
            MatchCollection blocks = Regex.Matches(input, @"\{([^\}]+)\}");

            foreach (Match block in blocks)
            {
                string content = block.Groups[1].Value; // Content inside -> "[#] Road 33(2;0;-10)"

                // Find the opening and closing parentheses
                int openParen = content.IndexOf('(');
                int closeParen = content.LastIndexOf(')');

                if (openParen == -1 || closeParen == -1 || closeParen < openParen)
                {
                    continue; // Skip if parentheses are missing or malformed
                }

                // Extract the key name (e.g., "[#] Road 33")
                string key = content.Substring(0, openParen).Trim();

                // Extract the raw parameters string inside the parentheses (e.g., "2;0;-10")
                string rawParams = content.Substring(openParen + 1, closeParen - openParen - 1);

                if (string.IsNullOrWhiteSpace(rawParams))
                {
                    result[key] = new float[0];
                    continue;
                }

                // Split by semicolon
                string[] tokens = rawParams.Split(';');
                List<float> parsedNumbers = new List<float>();

                foreach (string token in tokens)
                {
                    if (float.TryParse(token.Trim(), out float val))
                    {
                        parsedNumbers.Add(val);
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning($"Failed to parse value '{token}' into an int for key '{key}'.");
                    }
                }

                // Map it to the dictionary
                result[key] = parsedNumbers.ToArray();
            }

            return result;
        }
    
    public static BezierPoint InsertPointAtDistance(this BezierCurve curve, BezierPoint startPoint, float distance)
{
    // If distance is exactly 0, there's nothing to insert
    if (curve == null || startPoint == null || Mathf.Approximately(distance, 0f)) return null;

    // 1. Find the index of the starting point
    int startIndex = curve.GetPointIndex(startPoint);
    if (startIndex == -1)
    {
        Debug.LogError("Start point is not part of the provided BezierCurve.");
        return null;
    }

    BezierPoint segmentStart;
    BezierPoint segmentEnd;
    int insertIndex;
    float targetForwardDistance;

    // 2. Resolve segment direction and convert negative distance to a forward target
    if (distance > 0f)
    {
        int nextIndex = startIndex + 1;
        if (nextIndex >= curve.pointCount)
        {
            if (curve.close) nextIndex = 0;
            else return null; // Can't go past the end of an open curve
        }

        segmentStart = startPoint;
        segmentEnd = curve[nextIndex];
        insertIndex = startIndex + 1;
        targetForwardDistance = distance;
    }
    else
    {
        // Distance is negative: target the preceding segment
        int prevIndex = startIndex - 1;
        if (prevIndex < 0)
        {
            if (curve.close) prevIndex = curve.pointCount - 1;
            else return null; // Can't go behind the start of an open curve
        }

        segmentStart = curve[prevIndex];
        segmentEnd = startPoint;
        insertIndex = startIndex; // Inserting here pushes startPoint down the array index
        
        // Convert negative offset into the equivalent positive forward distance from segmentStart
        float segmentLength = BezierCurve.ApproximateLength(segmentStart, segmentEnd, curve.resolution);
        targetForwardDistance = segmentLength + distance; // e.g., Length 50 + (-10) = 40 forward
    }

    // 3. Validate target distance against total segment boundary
    float totalSegmentLength = BezierCurve.ApproximateLength(segmentStart, segmentEnd, curve.resolution);
    if (targetForwardDistance >= totalSegmentLength || targetForwardDistance <= 0f)
    {
        Debug.LogWarning("Requested distance falls outside the boundary of the target curve segment. Node not inserted.");
        return null;
    }

    // 4. Convert absolute forward distance to local time parameter 't' (0 to 1)
    float t = FindLocalTAtDistance(segmentStart, segmentEnd, targetForwardDistance, totalSegmentLength, curve.resolution);

    // 5. De Casteljau's algorithm using the resolved forward endpoints
    Vector3 p0 = segmentStart.position;
    Vector3 p1 = segmentStart.globalHandle2;
    Vector3 p2 = segmentEnd.globalHandle1;
    Vector3 p3 = segmentEnd.position;

    Vector3 q0 = Vector3.Lerp(p0, p1, t);
    Vector3 q1 = Vector3.Lerp(p1, p2, t);
    Vector3 q2 = Vector3.Lerp(p2, p3, t);

    Vector3 r0 = Vector3.Lerp(q0, q1, t);
    Vector3 r1 = Vector3.Lerp(q1, q2, t);

    Vector3 splitPosition = Vector3.Lerp(r0, r1, t);

    // 6. Insert the new node at the resolved array index position
    BezierPoint newPoint = curve.InsertPointAt(insertIndex, splitPosition);

    // 7. Update handles using the unified forward segment pieces
    segmentStart.globalHandle2 = q0;
    
    newPoint.handleStyle = BezierPoint.HandleStyle.Connected;
    newPoint.globalHandle1 = r0;
    newPoint.globalHandle2 = r1;

    segmentEnd.globalHandle1 = q2;

    curve.SetDirty();

    return newPoint;
}
    
public static BezierPoint InsertStraightExtension(this BezierCurve curve, BezierPoint startPoint, float distance)
    {
        if (curve == null || startPoint == null || Mathf.Approximately(distance, 0f)) return null;

        int startIndex = curve.GetPointIndex(startPoint);
        if (startIndex == -1)
        {
            Debug.LogError("Start point is not part of the provided BezierCurve.");
            return null;
        }

        BezierPoint segmentStart;
        BezierPoint segmentEnd;
        int insertIndex;
        Vector3 tangentDirection;
        bool isForward = distance > 0f;

        // 1. Resolve direction and isolate the exact vector ray
        if (isForward)
        {
            int nextIndex = startIndex + 1;
            if (nextIndex >= curve.pointCount)
            {
                if (curve.close) nextIndex = 0;
                else return null; 
            }

            segmentStart = startPoint;
            segmentEnd = curve[nextIndex];
            insertIndex = startIndex + 1;

            tangentDirection = (segmentStart.globalHandle2 - segmentStart.position).normalized;
            if (tangentDirection == Vector3.zero) tangentDirection = segmentStart.transform.forward;
        }
        else
        {
            int prevIndex = startIndex - 1;
            if (prevIndex < 0)
            {
                if (curve.close) prevIndex = curve.pointCount - 1;
                else return null;
            }

            segmentStart = curve[prevIndex];
            segmentEnd = startPoint;
            insertIndex = startIndex;

            tangentDirection = (segmentEnd.position - segmentEnd.globalHandle1).normalized;
            if (tangentDirection == Vector3.zero) tangentDirection = segmentEnd.transform.forward;
            
            distance = Mathf.Abs(distance); 
        }

        // 2. Position the new node along the true straight projection ray
        Vector3 p0 = segmentStart.position;
        Vector3 p3 = segmentEnd.position;
        Vector3 newPointPos = isForward ? (p0 + tangentDirection * distance) : (p3 - tangentDirection * distance);

        // Save original destination handles before inserting the new point shifts hierarchy data
        Vector3 originalEndHandle1 = segmentEnd.globalHandle1;
        Vector3 originalStartHandle2 = segmentStart.globalHandle2;

        // 3. Insert the node
        BezierPoint newPoint = curve.InsertPointAt(insertIndex, newPointPos);
        newPoint.handleStyle = BezierPoint.HandleStyle.Connected;

        // 4. Calculate the remaining gap distance to properly scale the blending handles
        float remainingDistance = Vector3.Distance(newPointPos, isForward ? p3 : p0);
        float blendHandleLength = remainingDistance * 0.33f;

        if (isForward)
        {
            // Segment 1 (Straight): segmentStart to newPoint
            // Keep segmentStart.globalHandle2 exactly as it was.
            newPoint.globalHandle1 = newPointPos - (tangentDirection * (distance * 0.33f));

            // Segment 2 (The Blend): newPoint to segmentEnd
            // Force the outgoing handle to stay perfectly parallel with the straight path
            newPoint.globalHandle2 = newPointPos + (tangentDirection * blendHandleLength);

            // Keep the destination node's incoming handle angle unchanged, but scale its magnitude 
            // relative to the shortened gap distance to prevent excessive ballooning or looping.
            Vector3 endTangentDir = (originalEndHandle1 - p3).normalized;
            if (endTangentDir == Vector3.zero) endTangentDir = -segmentEnd.transform.forward;
            segmentEnd.globalHandle1 = p3 + (endTangentDir * blendHandleLength);
        }
        else
        {
            // Segment 1 (The Blend): segmentStart to newPoint
            Vector3 startTangentDir = (originalStartHandle2 - p0).normalized;
            if (startTangentDir == Vector3.zero) startTangentDir = segmentStart.transform.forward;
            segmentStart.globalHandle2 = p0 + (startTangentDir * blendHandleLength);

            newPoint.globalHandle1 = newPointPos - (tangentDirection * blendHandleLength);

            // Segment 2 (Straight): newPoint to segmentEnd
            newPoint.globalHandle2 = newPointPos + (tangentDirection * (distance * 0.33f));
            // segmentEnd.globalHandle1 stays exactly as it was
        }

        curve.SetDirty();
        return newPoint;
    }

    /// <summary>
    /// Helper method using binary/linear search approximation to map a world distance to a local t parameter.
    /// </summary>
    private static float FindLocalTAtDistance(BezierPoint p1, BezierPoint p2, float targetDistance, float totalLength, float resolution)
    {
        float low = 0f;
        float high = 1f;
        float t = targetDistance / totalLength; // Initial linear guess

        // 5 iterations of binary refinement are usually more than enough for precise tracking paths
        for (int i = 0; i < 5; i++)
        {
            float currentDist = BezierCurve.ApproximateLength(p1.position, p1.globalHandle2, p2.position, p2.globalHandle1, Mathf.RoundToInt(resolution)) * t; // rough lower clamp
            
            // Re-evaluating actual segment sub-slice length
            float evaluatedDist = ApproximateSubSegmentLength(p1, p2, t, resolution);

            if (Mathf.Abs(evaluatedDist - targetDistance) < 0.01f)
                break;

            if (evaluatedDist < targetDistance)
            {
                low = t;
                t = (t + high) / 2f;
            }
            else
            {
                high = t;
                t = (low + t) / 2f;
            }
        }
        return t;
    }

    private static float ApproximateSubSegmentLength(BezierPoint p1, BezierPoint p2, float t, float resolution)
    {
        int steps = Mathf.Max(3, Mathf.RoundToInt(resolution));
        float length = 0f;
        Vector3 lastPos = p1.position;

        for (int i = 1; i <= steps; i++)
        {
            float subT = (i / (float)steps) * t;
            Vector3 currentPos = BezierCurve.GetPoint(p1, p2, subT);
            length += (currentPos - lastPos).magnitude;
            lastPos = currentPos;
        }
        return length;
    }
    
    public static void ScaleEndTangents(this BezierCurve curve, bool targetEndNode, float scaleFactor)
    {
        if (curve == null || curve.pointCount < 2)
        {
            Debug.LogWarning("Curve must have at least two points to scale end tangents.");
            return;
        }
        
        
        if (targetEndNode)
        {
            int lastIndex = curve.pointCount - 1;
            int neighborIndex = lastIndex - 1;

            curve[lastIndex].handle1 *= scaleFactor;
            curve[neighborIndex].handle2 *= scaleFactor;
        }
        else
        {
            curve[0].handle2 *= scaleFactor;
            curve[1].handle1 *= scaleFactor;
        }

        // Mark the curve as dirty so lengths and visuals update
        curve.SetDirty();
    }
    
}
