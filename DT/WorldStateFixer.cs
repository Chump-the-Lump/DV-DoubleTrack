using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Damage;
using DV.Garages;
using DV.JObjectExtstensions;
using DV.Logic.Job;
using DV.Simulation.Controllers;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using DV.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DoubleTrack;

[HarmonyPatch]
public static class WorldStateFixer
{
    public static bool Patch;
    
    [HarmonyPatch(typeof(StartGameData_FromSaveGame), "LoadingNonBlockingCoro")]
    [HarmonyPostfix]
    public static void PostFix(StartGameData_FromSaveGame __instance)
    {
        JProperty carBlockProperty = __instance.snapshot.Data.Properties().FirstOrDefault(p => p.Name.StartsWith("Cars#"));
        if(__instance.GetSaveGameData().GetJObject(SaveGameKeys.Cars) != null)
        {
            return;
        }
        
        carBlockProperty.Value["trackHash"] = RailTrackRegistryBase.Instance.TracksHash;

        JArray carsArray = ((JObject)carBlockProperty.Value)["carsData"] as JArray;

        if (carsArray != null)
        {
            
            for (int i = carsArray.Count - 1; i >= 0; i--)
            {
                if (carsArray[i]["unique"]?.Value<bool>() != true)
                {
                    carsArray.RemoveAt(i);
                }
            }
        }

        __instance.snapshot.Data.Add(SaveGameKeys.Cars, carBlockProperty.Value.DeepClone());
        carBlockProperty.Remove();

        
        Debug.Log(SaveGameData.LoadFromJson(__instance.snapshot.Data, __instance.snapshot.CustomChunkData));
        AccessTools.Field(typeof(StartGameData_FromSaveGame), "saveGameData").SetValue(__instance, SaveGameData.LoadFromJson(__instance.snapshot.Data, __instance.snapshot.CustomChunkData));
        
        Patch = true;
    }

    private static List<Coroutine> Coroutines = new List<Coroutine>();
    [HarmonyPatch(typeof(HomeGarageReference), MethodType.Constructor)]
    [HarmonyPostfix]
    private static void ReRail(HomeGarageReference __instance)
    {
        if(!Patch)return;
        Coroutines.Add(__instance.StartCoroutine(PollSpawner(__instance)));
        
    }

    static IEnumerator PollSpawner(HomeGarageReference garage)
    {
        yield return new WaitUntil(() => garage.garageCarSpawner != null);
        TrainCar car = garage.GetComponent<TrainCar>();

        yield return new WaitForSeconds(1f);
        do
        {
            garage.garageCarSpawner.ReturnCarHome(car);
            yield return new WaitForSeconds(1f);

        } while (car.derailed);
        
        garage.garageCarSpawner.ReturnCarHome(car);
        car.GetComponent<DamageController>()?.RepairAll();
        yield return new WaitForSeconds(1f);

        for (int i = Coroutines.Count - 1; i >= 0; i--)
        {
            car.StopCoroutine(Coroutines[i]);
            Coroutines.RemoveAt(i);
        }
    }
    
}