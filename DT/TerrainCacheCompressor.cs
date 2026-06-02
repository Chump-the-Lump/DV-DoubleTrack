using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DoubleTrack;
using UnityEngine;

public static class TerrainCacheCompressor
{
    /// <summary>
    /// Computes a unique SHA256 string hash of the target track configuration file.
    /// </summary>
    private static string GetTargetFileHash(string targetFilePath)
    {
        if (!File.Exists(targetFilePath))
        {
            // Fallback unique token if target file is missing, ensuring a re-cache once it appears
            return "MISSING_TARGET_FILE_HASH_TOKEN"; 
        }

        using (SHA256 sha256 = SHA256.Create())
        {
            using (FileStream stream = File.OpenRead(targetFilePath))
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2")); // Convert to lowercase hexadecimal string
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Compresses the entire worldwide height cache dictionary into a file with the target file hash signature on Line 1.
    /// </summary>
    public static void SaveCompressedDeltas(string filePath, string targetFilePath, Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>> cache)
    {
        if (cache == null) cache = new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>();

        // Generate the hash string based on the current contents of target.txt
        string currentTargetHash = GetTargetFileHash(targetFilePath);

        // Open a file stream for writing
        using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            using (StreamWriter writer = new StreamWriter(fs))
            {
                // Line 1: Save the track file hash as the version tracking signature
                writer.WriteLine(currentTargetHash);
                writer.Flush(); // Flush text parser out to the file container immediately

                // Line 2: The compressed binary dictionary encoded as a Base64 string line
                using (MemoryStream ms = new MemoryStream())
                {
                    using (GZipStream gzip = new GZipStream(ms, CompressionMode.Compress))
                    {
                        using (BinaryWriter binaryWriter = new BinaryWriter(gzip))
                        {
                            // 1. Write total number of map cells/tiles in the dictionary
                            binaryWriter.Write(cache.Count);

                            foreach (var kvp in cache)
                            {
                                Vector2Int coord = kvp.Key;
                                List<PersistentTerrainManager.HeightDelta> deltas = kvp.Value ?? new List<PersistentTerrainManager.HeightDelta>();

                                // 2. Write the grid coordinates for this map cell
                                binaryWriter.Write(coord.x);
                                binaryWriter.Write(coord.y);

                                // 3. Write total delta modifications count inside this specific cell
                                binaryWriter.Write(deltas.Count);

                                // 4. Stream the data pack
                                foreach (var delta in deltas)
                                {
                                    binaryWriter.Write(delta.x);      // 2 bytes (ushort)
                                    binaryWriter.Write(delta.z);      // 2 bytes (ushort)
                                    binaryWriter.Write(delta.height); // 4 bytes (float)
                                }
                            }
                        }
                    }

                    // Convert compressed binary bytes into a single text line representation
                    string base64DataLine = Convert.ToBase64String(ms.ToArray());
                    writer.WriteLine(base64DataLine);
                }
            }
        }
    }

    /// <summary>
    /// Reads a file, verifies its recorded hash signature against target.txt, and decompresses it back into the worldwide dictionary data type.
    /// If a hash mismatch is encountered (target.txt changed), deletes the cache file and returns an empty dictionary.
    /// </summary>
    public static Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>> LoadCompressedDeltas(string filePath, string targetFilePath)
    {
        if (!File.Exists(filePath))
        {
            UnityEngine.Debug.LogWarning("[DoubleTrack] Failed to find Cache file at " + TrackPlacerEntry.CACHE_PATH);
            return new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>(); // Return an empty dictionary if file doesn't exist yet
        }

        // Calculate what the current expected hash of target.txt should be right now
        string currentExpectedHash = GetTargetFileHash(targetFilePath);

        try
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                using (StreamReader reader = new StreamReader(fs))
                {
                    // Read Line 1: Hash validation
                    string savedHashLine = reader.ReadLine();
                    if (savedHashLine != "#" && (string.IsNullOrEmpty(savedHashLine) || savedHashLine != currentExpectedHash))
                    {
                        Debug.LogWarning($"[DoubleTrack] target.txt configuration changed or cache invalid! Old: {savedHashLine} | Current: {currentExpectedHash}. Purging old cache file.");
                        
                        // Break out of the using streams before attempting deletion
                        reader.Close();
                        fs.Close();
                        
                        // Safely discard the outdated layout
                        SafeDeleteFile(filePath);
                        return new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>();
                    }

                    // Read Line 2: Base64 compressed layout line
                    string base64DataLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(base64DataLine))
                    {
                        Debug.LogWarning("[DoubleTrack] Failed to read cache file!");
                        return new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>(); 
                    }

                    byte[] compressedBytes = Convert.FromBase64String(base64DataLine);

                    using (MemoryStream ms = new MemoryStream(compressedBytes))
                    {
                        using (GZipStream gzip = new GZipStream(ms, CompressionMode.Decompress))
                        {
                            using (BinaryReader binaryReader = new BinaryReader(gzip))
                            {
                                // 1. Read total number of map cell tiles saved
                                int cellCount = binaryReader.ReadInt32();
                                var loadedCache = new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>(cellCount);

                                for (int i = 0; i < cellCount; i++)
                                {
                                    // 2. Extract grid keys
                                    int gridX = binaryReader.ReadInt32();
                                    int gridY = binaryReader.ReadInt32();
                                    Vector2Int coord = new Vector2Int(gridX, gridY);

                                    // 3. Read delta size count for this specific coordinate block
                                    int deltaCount = binaryReader.ReadInt32();
                                    List<PersistentTerrainManager.HeightDelta> deltas = new List<PersistentTerrainManager.HeightDelta>(deltaCount);

                                    // 4. Populate cell modifications pack
                                    for (int j = 0; j < deltaCount; j++)
                                    {
                                        ushort x = binaryReader.ReadUInt16();
                                        ushort z = binaryReader.ReadUInt16();
                                        float height = binaryReader.ReadSingle();

                                        deltas.Add(new PersistentTerrainManager.HeightDelta(x, z, height));
                                    }

                                    // Add completed tile array back into our dictionary container
                                    loadedCache[coord] = deltas;
                                }

                                Debug.Log("[DoubleTrack] Terrain cache read successfully.");
                                return loadedCache;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[DoubleTrack] Critical error parsing cache file dictionary. Deleting file to force clean rebuild.");
            UnityEngine.Debug.LogException(e);
            
            // If the file is corrupted structurally (e.g. invalid Base64 string), delete it as a fallback
            SafeDeleteFile(filePath);
            return new Dictionary<Vector2Int, List<PersistentTerrainManager.HeightDelta>>();
        }
    }

    private static void SafeDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[DoubleTrack] Failed to physically delete outdated cache file: {filePath}");
            UnityEngine.Debug.LogException(ex);
        }
    }
}