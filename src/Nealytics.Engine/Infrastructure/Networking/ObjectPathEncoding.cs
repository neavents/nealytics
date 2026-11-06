namespace Nealytics.Engine.Infrastructure.Networking;

using System.Globalization;
using System.Text;

internal static class ObjectPathEncoding
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(Encoding.UTF8.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static string Encode(string value, bool preserveSlash)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        StringBuilder result = new(bytes.Length);
        foreach (byte character in bytes)
        {
            if (character is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'
                || (preserveSlash && character == (byte)'/'))
            {
                result.Append((char)character);
            }
            else
            {
                result.Append('%').Append(character.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return result.ToString();
    }
}
