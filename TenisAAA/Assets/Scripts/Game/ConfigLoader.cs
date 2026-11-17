using System.IO;
using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>Loads StreamingAssets/tennis_config.json (writes the defaults the first time in the editor).
    /// Designers tune gameplay by editing the JSON only.</summary>
    public static class ConfigLoader
    {
        public const string FileName = "tennis_config.json";

        public static TennisConfig Load()
        {
            string path = Path.Combine(Application.streamingAssetsPath, FileName);
            var cfg = new TennisConfig();
            try
            {
                if (File.Exists(path)) JsonUtility.FromJsonOverwrite(File.ReadAllText(path), cfg);
#if UNITY_EDITOR
                else { Directory.CreateDirectory(Application.streamingAssetsPath); File.WriteAllText(path, JsonUtility.ToJson(cfg, true)); }
#endif
            }
            catch (System.Exception e) { Debug.LogWarning($"[Tenis] config: {e.Message} — using defaults"); }
            return cfg;
        }
    }

    public static class CourtSpace
    {
        /// <summary>Core (x = length, y = width, z = up; right-handed) → Unity (left-handed, y up).
        /// Unity z = court length, Unity x = -court y so the Near player's right is screen-right.</summary>
        public static Vector3 ToUnity(Vec3 v) => new Vector3((float)-v.y, (float)v.z, (float)v.x);
        public static Vec3 ToCore(Vector3 u) => new Vec3(u.z, -u.x, u.y);
    }
}
