using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stratoscope.Core;

/// <summary>
/// Writes one setting of the settings file, for the menu. The file is edited as text, so its comments stay: an active line gets the new value,
/// a commented example is switched on, and otherwise a line is added at the top. The Mac app does the same (Config.setValue).
/// </summary>
public static class ConfigFile
{
    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/>, creating the file from the commented starter when it is missing. Returns false,
    /// and leaves the file as it is, when the file cannot be read as a JSON object, cannot be written, or the value is not a plain word.
    /// </summary>
    public static bool SetValue(string path, string key, string value)
    {
        if (!Plain(key) || !Plain(value)) return false;
        try
        {
            string text;
            if (File.Exists(path))
            {
                text = File.ReadAllText(path);
                var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                if (JsonNode.Parse(text, documentOptions: options) is not JsonObject) return false;
            }
            else text = Config.Template;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, Edit(text, key, value), new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The text with <paramref name="key"/> set. A trailing comma after the new line is fine: the settings are read with trailing commas allowed.</summary>
    internal static string Edit(string text, string key, string value)
    {
        var name = Regex.Escape(key);
        var active = new Regex("^([ \\t]*\"" + name + "\"[ \\t]*:[ \\t]*)\"[^\"\\r\\n]*\"", RegexOptions.Multiline);
        if (active.IsMatch(text)) return active.Replace(text, "${1}\"" + value + "\"", 1);

        var example = new Regex("^([ \\t]*)//[ \\t]*(\"" + name + "\"[ \\t]*:[ \\t]*)\"[^\"\\r\\n]*\"", RegexOptions.Multiline);
        if (example.IsMatch(text)) return example.Replace(text, "${1}${2}\"" + value + "\"", 1);

        var open = Regex.Match(text, "^(?![ \\t]*//)[^\\r\\n]*?\\{", RegexOptions.Multiline);
        return text.Insert(open.Index + open.Length, Environment.NewLine + "  \"" + key + "\": \"" + value + "\",");
    }

    private static bool Plain(string text) => text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
}
