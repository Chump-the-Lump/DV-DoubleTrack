using System.Collections;
using Bolt;
using Bolt.Dependencies.NCalc;
using TMPro;
using UniRx.Triggers;
using UnityEngine;
using UnityEngine.Events;
using UnityModManagerNet;

namespace DoubleTrack;

public static class SignalFixer
{
    static bool active   = false;
    public static List<string> AdditionalSignalsToFix = new List<string>();

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
            FixSignalStandard(railTrack);
        }
        
        foreach (RailTrack railTrack in RailTrackRegistry.RailTracks)
        {
            AddListeners(railTrack);
        }
    }

    private static void AddListeners(RailTrack railTrack)
    {
        foreach (Transform child in railTrack.transform)
        {
            if (!child.name.Contains("Signal")) continue;
            TextMeshPro[] texts = child.GetComponentsInChildren<TextMeshPro>();
            TextMeshPro signalID = null;
            foreach (TextMeshPro text in texts)
            {
                if (text.transform.parent.name == "LocationSign") signalID = text;
            }
            DTSignalListener listner = child.gameObject.AddComponent<DTSignalListener>();
            listner.textMeshPro = signalID;
            listner.OnSignalEnabled.AddListener(()=>
            {
                if(AdditionalSignalsToFix.Contains(signalID.text)) MirrorSignal(child.gameObject);
            });
        }
    }

    private static void FixSignalStandard(RailTrack railTrack)
    {
        DoubleRailTrack doubleInfo = railTrack.GetComponent<DoubleRailTrack>();
        if (doubleInfo == null) return;

        List<GameObject> signals = new List<GameObject>();
        foreach (Transform child in railTrack.transform)
        {
            if (!child.name.Contains("Signal")) continue;
            signals.Add(child.gameObject);
        }
        
        if (signals.Count < 2) return;

        if (doubleInfo.IsSiding ^ doubleInfo.Offset > 0)
        {
            MirrorSignal(signals[0]);

            if (signals.Count == 4) MirrorSignal(signals[2]);
        }
        else
        {
            MirrorSignal(signals[1]);

            if (signals.Count == 4) MirrorSignal(signals[3]);

        }

        if (signals.Count != 4) return;
            
        if(Vector3.Distance(signals[2].transform.position, signals[3].transform.position) > 40f)return;
        if(Vector3.Dot(signals[2].transform.forward,signals[3].transform.position-signals[2].transform.position) > 0) SwapSignalPositions(signals[2], signals[3]);
    }
        
    private static void MirrorSignal(GameObject signal)
    {
        signal.transform.localScale = signal.transform.localScale with
        {
            x = signal.transform.localScale.x * -1
        };
        
        foreach (BoxCollider box in signal.GetComponentsInChildren<BoxCollider>())
        {
            box.size = box.size with
            {
                x = box.size.x * -1
            };
        }
        
        foreach (TextMeshPro txt in signal.GetComponentsInChildren<TextMeshPro>())
        {
            txt.transform.localScale = txt.transform.localScale with
            {
                x = txt.transform.localScale.x * -1
            };
        }
    }
        
    
    private static void SwapSignalPositions(GameObject a, GameObject b)
    {
        (a.transform.position, b.transform.position) = (b.transform.position, a.transform.position);
    }

   
}

public class DTSignalListener : MonoBehaviour
{
    public UnityEvent OnSignalEnabled = new UnityEvent();

    public TextMeshPro textMeshPro;
    void Start()
    {
        StartCoroutine(LabelCoro());
    }
    IEnumerator LabelCoro()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);
            if (!textMeshPro.text.Contains("X-XXXX"))
            {
                OnSignalEnabled.Invoke();
                OnSignalEnabled.RemoveAllListeners();
                Destroy(this);
            }
        }
    }
}