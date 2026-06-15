using DoubleTrack;
using HarmonyLib;
using Ludiq;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityModManagerNet;

namespace DoubleTrack
{


    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        public static UnityEvent SaveSettings = new UnityEvent();
        public enum JunctionMode
        {
            Normal,
            SpringRight,
            SpringLeft,
            RemoteDispatch
        }

        public static JunctionMode StaticMode;

        [Draw("Junction Mode")] public JunctionMode Mode = JunctionMode.Normal;

        public override void Save(UnityModManager.ModEntry entry)
        {
            if(!MultiplayerShim.IsHost)return;
            StaticMode = Mode;
            Save(this, entry);
            UpdateSettings();
            SaveSettings.Invoke();
        }

        public static void UpdateSettings()
        {
            if (AllTracksPatch.AddedJunctions?.Count > 0) foreach (var junction in AllTracksPatch.AddedJunctions)
            {
                SwitchManager? jSwitch = junction?.gameObject.GetOrAddComponent<SwitchManager>();
                jSwitch?.Init();
            }
        }

        public void OnChange()
        {
            
        }
    }

    public static class TrackPlacerEntry
    {
        public static UnityModManager.ModEntry ModEntry;
        public static string TARGET_PATH = "";
        public static string CACHE_PATH = "";
        public static bool Load(UnityModManager.ModEntry entry)
        {
            ModEntry = entry;
            var harmony = new Harmony(entry.Info.Id);
            harmony.PatchAll();
            
            TARGET_PATH = Path.Combine(TrackPlacerEntry.ModEntry.Path, "target.csv");
            CACHE_PATH = Path.Combine(TrackPlacerEntry.ModEntry.Path, "terrain.dat");

            Settings? settings = null;

            try
            {
                settings = UnityModManager.ModSettings.Load<Settings>(ModEntry);
                if (settings != null)
                {
                    settings.Save(ModEntry);
                    ModEntry.Logger.Log("Loaded existing settings");
                }
                else
                {
                    ModEntry.Logger.Log("Created new settings (no existing file)");
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log("Failed to load settings, using defaults: " + ex.Message);
            }

            ModEntry.OnGUI = settings.Draw;
            ModEntry.OnSaveGUI = settings.Save;

            SceneManager.sceneLoaded += AllTracksPatch.LoadTracks;
            PersistentTerrainManager.Initialize();
            MultiplayerShim.Initialize(ModEntry);
            
            return true;
        }
    }
}
