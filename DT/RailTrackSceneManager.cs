using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using DV.Utils;
using HarmonyLib;
using Rewired;

namespace DoubleTrack
{
    public class RailTrackSceneManager : MonoBehaviour
    {
        private SceneSplitManager _sector;
        private List<Transform> _localColliderSegments;
        private bool _initialized = false;
        public static Dictionary<string, List<Transform>> TileNameToRailTrack = new Dictionary<string, List<Transform>>();
        void Awake()
        {
            _sector = GetComponent<SceneSplitManager>();
        }

        void Start()
        {
            RemoveVanillaSignsAndColliders();
            if (!TileNameToRailTrack.TryGetValue(_sector.sceneName, out _localColliderSegments)) StartCoroutine(DeferredRegistration(5));
            else ActivateExistingTrack();
        }

        private void ActivateExistingTrack()
        {
            foreach (Transform transform in _localColliderSegments)transform.gameObject.SetActive(true);
        }

        private IEnumerator DeferredRegistration(int frameDelay)
        {
            for (int i = 0; i < frameDelay; i++) yield return null;
            RegisterAndSyncSegments();
            _initialized = true;
        }

        private void RegisterAndSyncSegments()
        {
            _localColliderSegments = new List<Transform>();
            // These are static map coordinates and do not change with the WorldMover
            float minX = _sector.position.x;
            float maxX = _sector.position.x + _sector.size.x;
            float minZ = _sector.position.z;
            float maxZ = _sector.position.z + _sector.size.z;

            foreach (var track in AllTracksPatch.AddedTracks)
            {
                if (track == null) continue;
                Transform colRoot = track.transform.Find("COLLIDERS");
                if (colRoot == null) continue;

                foreach (Transform segment in colRoot)
                {
                    Vector3 trueWorldPos = segment.position;

                    if (trueWorldPos.x >= minX && trueWorldPos.x <= maxX &&
                        trueWorldPos.z >= minZ && trueWorldPos.z <= maxZ)
                    {
                        if (!_localColliderSegments.Contains(segment))
                        {
                            WorldMover.Instance.objectsToMove.Add(segment);
                            segment.transform.position += WorldMover.currentMove;
                            segment.gameObject.SetActive(true);
                        }
                    }
                }
            }

            TileNameToRailTrack[_sector.sceneName] = _localColliderSegments;

        }

        private void RemoveVanillaSignsAndColliders()
        {
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name.Contains("Near_SignGenerator")/*||child.name.Contains("Colliders")*/)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        void OnDestroy()
        {
            foreach (var segment in _localColliderSegments) if (segment != null) segment.gameObject.SetActive(false);
        }
    }
    
    [HarmonyPatch(typeof(SceneSplitManager), "Start")]
    public static class SceneSplitManagerPatch
    {
        static void Postfix(SceneSplitManager __instance)
        {
            if (__instance.GetComponent<RailTrackSceneManager>() == null)
                __instance.gameObject.AddComponent<RailTrackSceneManager>();
        }
    }
}