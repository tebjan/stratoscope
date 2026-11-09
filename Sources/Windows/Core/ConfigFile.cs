using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stratoscope.Core;

/// <summary>Writes one key of the settings file, for the menu. Everything else in the file stays; comments do not (JSON has none).</summary>
public static class ConfigFile
{
    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/>, creating the file when it is missing. Returns false, and leaves the file as
    /// it is, when the file cannot be parsed as a JSON object or cannot be written.
    /// </summary>
    public static bool SetValue(string path, string key, string value)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: options) is not JsonObject existing) return false;
                root = existing;
            }
            else root = new JsonObject();

            root[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }
}
