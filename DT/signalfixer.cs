using System.Collections;
using UnityEngine;
using UnityModManagerNet;

namespace DoubleTrack;

public static class SignalFixer
{
    static bool active   = false;
    public static IEnumerator SignalFinder()
    {
        if (active) yield break;
        active = true;
        UnityModManager.ModEntry SignalsMod = UnityModManager.FindMod("DVSignals");
        if (SignalsMod != null && SignalsMod.Active)
        {
            yield return new WaitUntil(() => GameObject.Find("[SignalManager]") != null);
            FixSignals();
        }
        active = false;
    }

    private static void FixSignals()
    {
        foreach (RailTrack railTrack in AllTracksPatch.AddedTracks)
        {
            DoubleRailTrack doubleInfo = railTrack.GetComponent<DoubleRailTrack>();
            if (doubleInfo == null) continue;
            

            List<GameObject> signals = new List<GameObject>();
            foreach (Transform child in railTrack.transform)
            {
                if (!child.name.Contains("Signal")) continue;
                signals.Add(child.gameObject);
            }

            if (signals.Count < 2) continue;

            if (doubleInfo.IsSiding ^ doubleInfo.Offset > 0)
            {
                signals[0].transform.localScale = signals[0].transform.localScale with
                {
                    x = signals[0].transform.localScale.x * -1
                };

                if (signals.Count == 4)
                    signals[2].transform.localScale = signals[2].transform.localScale with
                    {
                        x = signals[2].transform.localScale.x * -1
                    };
            }
            else
            {
                signals[1].transform.localScale = signals[1].transform.localScale with
                {
                    x = signals[1].transform.localScale.x * -1
                };

                if (signals.Count == 4)
                    signals[3].transform.localScale = signals[3].transform.localScale with
                    {
                        x = signals[3].transform.localScale.x * -1
                    };
            }

            if (signals.Count != 4) continue;
            
            if(Vector3.Distance(signals[2].transform.position, signals[3].transform.position) > 40f)continue;
            if(Vector3.Dot(signals[2].transform.forward,signals[3].transform.position-signals[2].transform.position) > 0) SwapSignalPositions(signals[2], signals[3]);
        }

    }
        
    
    private static void SwapSignalPositions(GameObject a, GameObject b)
    {
        (a.transform.position, b.transform.position) = (b.transform.position, a.transform.position);
    }

    private static float FindNearestT(BezierCurve curve, Vector3 worldPos, float searchResolution)
    {
        float bestT = 0f;
        float minDistance = float.MaxValue;

        // Use the decompile's Interpolate to get world points
        // We iterate through every segment of the curve
        for (int i = 0; i < curve.pointCount - 1; i++)
        {
            BezierPoint p1 = curve[i];
            BezierPoint p2 = curve[i + 1];

            // Generate points for this segment
            Vector3[] points = BezierCurve.Interpolate(p1.position, p1.globalHandle2, p2.position, p2.globalHandle1, searchResolution);

            for (int j = 0; j < points.Length; j++)
            {
                float dist = Vector3.Distance(worldPos, points[j]);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    // Calculate local T for this segment, then map it to global T
                    float localT = j / (float)(points.Length - 1);
                    bestT = (i + localT) / (curve.pointCount - 1);
                }
            }
        }
        return bestT;
    }

    public static void MirrorObjectAcrossCurve(GameObject obj, BezierCurve curve, float t)
    {
        // 1. Get the reference point and orientation on the curve
        Vector3 centerPoint = curve.GetPointAt(t);
        Vector3 tangent = curve.GetTangentAt(t).normalized;
        
        // 2. Calculate the 'Right' vector (the normal to our mirror plane)
        // In Derail Valley, Vector3.up is the consistent world up
        Vector3 trackRight = Vector3.Cross(Vector3.up, tangent).normalized;

        // 3. Get the object's current position relative to the center point
        Vector3 relativePos = obj.transform.position - centerPoint;

        // 4. Mirror the position across the 'Right' vector
        // We project the relative position onto the right vector to see how far 'out' it is,
        // then subtract twice that projection to move it to the exact opposite side.
        float distanceAcross = Vector3.Dot(relativePos, trackRight);
        Vector3 mirroredPos = obj.transform.position - (trackRight * (distanceAcross * 2f));

        // 5. Apply the new position
        obj.transform.position = mirroredPos;

        // ... (Steps 1-5 remain the same)

        // 6. Mirror the visual orientation
        // We force the X-scale to be negative to ensure the mesh 'looks' like a left-hand signal
        Vector3 currentScale = obj.transform.localScale;
        obj.transform.localScale = new Vector3(-Mathf.Abs(currentScale.x), currentScale.y, currentScale.z);

        // 7. Fixed Rotation: Align to track Tangent
        // Instead of multiplying (*=), we set the rotation to face along the track tangent.
        // DVSignals usually expects signals to face 'Forward' or 'Backward' along the curve.
        if (t < 0.5f) 
        {
            // For the start of the track, face forward
            obj.transform.rotation = Quaternion.LookRotation(tangent, Vector3.up);
        }
        else 
        {
            // For the end of the track, face backward
            obj.transform.rotation = Quaternion.LookRotation(-tangent, Vector3.up);
        }
    }
}