using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence;

internal static class SessionFileName
{
    public static string Resolve(string directory, SessionKey key, string extension)
    {
        var name = For(key);
        var path = Path.Combine(directory, name + extension);
        if (name.StartsWith('~') && !File.Exists(path))
        {
            var legacy = string.Concat(key.Value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
            if (legacy.Length > 0 && File.Exists(Path.Combine(directory, legacy + extension)))
            {
                throw new InvalidOperationException(
                    $"Session '{key.Value}' has an ambiguous legacy filename. Its data was preserved; explicit migration is required before this key can be used.");
            }
        }
        return path;
    }

    public static string For(SessionKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Value);
        // Preserve conventional legacy filenames. Encode all other keys without deleting
        // characters or folding case. '~' cannot be produced by the legacy sanitizer.
        var value = key.Value;
        if (value.Length <= 120 && value.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') &&
            !IsDeviceName(value))
        {
            return value;
        }
        return "~" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool IsDeviceName(string value) =>
        value is "con" or "prn" or "aux" or "nul" ||
        (value.Length == 4 && (value.StartsWith("com", StringComparison.Ordinal) || value.StartsWith("lpt", StringComparison.Ordinal)) &&
         value[3] is >= '1' and <= '9');
}
