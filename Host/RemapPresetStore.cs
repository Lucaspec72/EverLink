using System.IO;
using System.Text.Json;

namespace EverLinkHost;

/// <summary>
/// Stores remap presets as individual JSON files in a "RemapPresets" folder next to the
/// exe - one file per preset, named after the preset itself - rather than one entry in the
/// shared settings.json. This is deliberate: presets are meant to be shareable (copy one
/// file to give someone your mapping) and browsable outside the app, which a folder of
/// individual files supports naturally and a single combined settings blob doesn't.
/// </summary>
public static class RemapPresetStore
{
    private static string PresetsFolder => Path.Combine(AppContext.BaseDirectory, "RemapPresets");

    /// <summary>Lists all saved preset names (without the .json extension), sorted
    /// alphabetically - what populates the editable dropdown's suggestion list.</summary>
    public static List<string> ListPresetNames()
    {
        try
        {
            if (!Directory.Exists(PresetsFolder)) return new List<string>();

            return Directory.GetFiles(PresetsFolder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Folder missing/inaccessible - treat as "no presets yet" rather than erroring.
            return new List<string>();
        }
    }

    /// <summary>Saves the given mapping under the given name, overwriting any existing
    /// preset with that name. Returns true on success.</summary>
    public static bool Save(string presetName, Dictionary<RemapTarget, string> mapping)
    {
        var safeName = SanitizeFileName(presetName);
        if (string.IsNullOrWhiteSpace(safeName)) return false;

        try
        {
            Directory.CreateDirectory(PresetsFolder);
            var path = Path.Combine(PresetsFolder, safeName + ".json");

            // Stored as <string, string> (enum name -> source name) rather than serializing
            // the RemapTarget enum keys directly - System.Text.Json's default dictionary
            // key handling for non-string keys is finicky across versions, and plain
            // string keys make the saved file trivially readable/editable by hand too.
            var serializable = mapping.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            var json = JsonSerializer.Serialize(serializable, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Loads a preset by name into the given mapping dictionary, overwriting
    /// existing entries. Returns true if the preset was found and loaded successfully.
    /// Unrecognized target names in the file (e.g. from a future version with more targets)
    /// are silently skipped rather than causing a load failure.</summary>
    public static bool Load(string presetName, Dictionary<RemapTarget, string> mapping)
    {
        var safeName = SanitizeFileName(presetName);
        var path = Path.Combine(PresetsFolder, safeName + ".json");

        try
        {
            if (!File.Exists(path)) return false;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded is null) return false;

            foreach (var (key, value) in loaded)
            {
                if (Enum.TryParse<RemapTarget>(key, out var target))
                    mapping[target] = value;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes a saved preset by name ("forget"). Returns true if a file was
    /// actually removed.</summary>
    public static bool Delete(string presetName)
    {
        var safeName = SanitizeFileName(presetName);
        var path = Path.Combine(PresetsFolder, safeName + ".json");

        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Strips characters that aren't valid in a Windows filename, so a preset name
    /// typed freely by the user can't produce an invalid or path-escaping file path.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned;
    }
}
