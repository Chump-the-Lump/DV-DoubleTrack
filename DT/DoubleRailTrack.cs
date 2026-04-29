using UnityEngine;

namespace DoubleTrack;

public class DoubleRailTrack : MonoBehaviour
{
    public float Offset;
    public RailTrack ThisRailTrack;
    public RailTrack OtherRailTrack;
    public bool IsSiding;

    void Awake()
    {
       StartCoroutine(SignalFixer.SignalFinder());
    }
}