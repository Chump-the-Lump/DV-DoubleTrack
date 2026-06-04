using System.Reflection;
using System.Text.RegularExpressions;
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
    

    
public static BezierCurve CreateSmoothCurve(List<Vector3> positions, float percent = 0.33f)
{
    if (positions == null || positions.Count < 2)
    {
        Debug.LogWarning("BezierCurve generation requires at least 2 points.");
        return null;
    }

    GameObject curveObj = new GameObject("Percentage_Track_Curve");
    BezierCurve bezierCurve = curveObj.AddComponent<BezierCurve>();

    List<BezierPoint> spawnedPoints = new List<BezierPoint>();
    foreach (Vector3 pos in positions)
    {
        spawnedPoints.Add(bezierCurve.AddPointAt(pos));
    }

    int count = positions.Count;

    for (int i = 0; i < count; i++)
    {
        BezierPoint currentPoint = spawnedPoints[i];

        // ALWAYS start with Broken so setting one handle doesn't automatically auto-mirror/clobber the other
        currentPoint.handleStyle = BezierPoint.HandleStyle.Broken;

        Vector3 flatForward;

        // --- Endpoints ---
        if (i == 0)
        {
            Vector3 segment = positions[1] - positions[0];
            float distanceToNext = segment.magnitude;
            flatForward = new Vector3(segment.x, 0f, segment.z).normalized;

            // Use global handles to prevent issues with parent/local transform rotations
            currentPoint.globalHandle2 = currentPoint.position + (flatForward * (distanceToNext * percent));
            currentPoint.globalHandle1 = currentPoint.position - (flatForward * (distanceToNext * percent));

            currentPoint.handleStyle = BezierPoint.HandleStyle.Connected;
            continue;
        }
        if (i == count - 1)
        {
            Vector3 segment = positions[count - 1] - positions[count - 2];
            float distanceToPrev = segment.magnitude;
            flatForward = new Vector3(segment.x, 0f, segment.z).normalized;

            currentPoint.globalHandle1 = currentPoint.position - (flatForward * (distanceToPrev * percent));
            currentPoint.globalHandle2 = currentPoint.position + (flatForward * (distanceToPrev * percent));

            currentPoint.handleStyle = BezierPoint.HandleStyle.Connected;
            continue;
        }

        // --- Middle Nodes ---
        Vector3 pPrev = positions[i - 1];
        Vector3 pCurr = positions[i];
        Vector3 pNext = positions[i + 1];

        float distanceToPrevNode = Vector3.Distance(pPrev, pCurr);
        float distanceToNextNode = Vector3.Distance(pCurr, pNext);

        Vector3 unifiedChord = (pNext - pPrev);
        flatForward = new Vector3(unifiedChord.x, 0f, unifiedChord.z).normalized;

        // Calculate positions globally first
        Vector3 targetGlobalH1 = pCurr - flatForward * (distanceToPrevNode * percent);
        Vector3 targetGlobalH2 = pCurr + flatForward * (distanceToNextNode * percent);

        // Proportional Rotation/Smoothing Step using Global Positions
        Vector3 dirH1 = (targetGlobalH1 - pCurr).normalized;
        Vector3 dirH2 = (targetGlobalH2 - pCurr).normalized;
        Vector3 averagedDirection = (dirH2 - dirH1).normalized;
        
        float averageLength = (Vector3.Distance(targetGlobalH1, pCurr) + Vector3.Distance(targetGlobalH2, pCurr)) * 0.5f;

        // Apply cleanly while handle style is still Broken
        currentPoint.globalHandle1 = pCurr - averagedDirection * averageLength;
        currentPoint.globalHandle2 = pCurr + averagedDirection * averageLength;

        // Lock state now that both handles are safely set symmetrically
        currentPoint.handleStyle = BezierPoint.HandleStyle.Connected;
    }

    bezierCurve.SetDirty();
    return bezierCurve;
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
    
    /// <summary>
    /// Parses a standardized string in the format "[0.0,1.0,2.0],[3.0,4.0,5.0]" into a List of Vector3s.
    /// </summary>
    public static List<Vector3> ParseVecList(string input)
    {
        List<Vector3> vectorList = new List<Vector3>();

        if (string.IsNullOrWhiteSpace(input))
        {
            Debug.LogWarning("Input string is empty or null.");
            return vectorList;
        }

        // 1. Strip the very outer leading '[' and trailing ']' brackets
        string trimmed = input.Trim();
        if (trimmed.StartsWith("[")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("]")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        // 2. Split into individual vector segments using the internal configuration "]["
        // We use string arrays to handle the multi-character delimiter cleanly
        string[] vectorSegments = trimmed.Split(new string[] { "][" }, StringSplitOptions.None);

        foreach (string segment in vectorSegments)
        {
            // Split the individual X, Y, Z components by their commas
            string[] components = segment.Split(';');

            if (components.Length == 3)
            {
                // InvariantCulture ensures that dots (.) are always parsed correctly regardless of system region
                if (float.TryParse(components[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(components[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(components[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    vectorList.Add(new Vector3(x, y, z));
                }
                else
                {
                    Debug.LogError($"Failed to parse float components in segment: {segment}");
                }
            }
            else
            {
                Debug.LogError($"Segment did not contain exactly 3 components: {segment}");
            }
        }

        return vectorList;
    }
}
