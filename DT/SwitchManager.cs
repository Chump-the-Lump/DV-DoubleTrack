using System.Collections;
using dnlib;
using DoubleTrack;
using UnityEngine;
using Settings = DoubleTrack.Settings;

[DisallowMultipleComponent]
public class SwitchManager : MonoBehaviour
{
    private Junction junction;

    public bool RecursiveLock = false;
    private void Start()
    {
        junction = gameObject.GetComponent<Junction>();
        junction.Switched += OnSwitch;
        Init();
    }
    

    public void Init()
    {
        if(junction==null)return;
        if (DoubleTrack.Settings.StaticMode == DoubleTrack.Settings.JunctionMode.SpringRight)
        {
            if(MultiplayerShim.IsHost)junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)junction.defaultSelectedBranch);
            VisualSwitchEditor.DisableLever(junction);
        }else if (DoubleTrack.Settings.StaticMode == DoubleTrack.Settings.JunctionMode.SpringLeft)
        {
            if(MultiplayerShim.IsHost)junction.Switch(Junction.SwitchMode.NO_SOUND, (byte) ((uint) junction.defaultSelectedBranch + 1U));
            VisualSwitchEditor.DisableLever(junction);
        }else if (DoubleTrack.Settings.StaticMode == DoubleTrack.Settings.JunctionMode.RemoteDispatch)
        {
            VisualSwitchEditor.DisableLever(junction);
        }
        else
        {
            VisualSwitchEditor.EnableLever(junction);
        }
    }

    private void OnSwitch(Junction.SwitchMode mode, int branch)
    {
        if(!MultiplayerShim.IsHost)return;
        if(!RecursiveLock)StartCoroutine(SwitchCoro(mode, branch));
    }

    private IEnumerator SwitchCoro(Junction.SwitchMode mode, int branch)
    {
        RecursiveLock = true;
        
        yield return new WaitForSeconds(0.1f);
        
        switch (DoubleTrack.Settings.StaticMode)
        {
            case DoubleTrack.Settings.JunctionMode.SpringLeft:
                if(branch != (byte)junction.defaultSelectedBranch)junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)junction.defaultSelectedBranch);
                break;
            
            case DoubleTrack.Settings.JunctionMode.SpringRight:
                if(branch != (byte) ((uint) junction.defaultSelectedBranch + 1U))junction.Switch(Junction.SwitchMode.NO_SOUND, (byte) ((uint) junction.defaultSelectedBranch + 1U));
                break;
            
            case DoubleTrack.Settings.JunctionMode.RemoteDispatch:
                if(mode == Junction.SwitchMode.FORCED)
                    junction.Switch(Junction.SwitchMode.NO_SOUND);
                break;
        }
        RecursiveLock = false;
    
    }
}

public static class VisualSwitchEditor
{
    public static void DisableLever(Junction junction)
    {
        junction.transform.parent.transform.FindChildRecursive("switch_lever").gameObject.SetActive(false);
        junction.transform.parent.transform.FindChildRecursive("SwitchTrigger").transform.localScale = Vector3.one * 0.0001f;
        junction.transform.parent.transform.FindChildRecursive("switch_sign").transform.localScale = Vector3.one * 0.5f;
    }
    
    public static void EnableLever(Junction junction)
    {
        junction.transform.parent.transform.FindChildRecursive("switch_lever").gameObject.SetActive(true);
        junction.transform.parent.transform.FindChildRecursive("SwitchTrigger").transform.localScale = Vector3.one;
        junction.transform.parent.transform.FindChildRecursive("switch_sign").transform.localScale = Vector3.one;
    }
}