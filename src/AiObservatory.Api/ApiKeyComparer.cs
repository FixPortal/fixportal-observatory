using System.Security.Cryptography;
using System.Text;

namespace AiObservatory.Api;

internal static class ApiKeyComparer
{
    public static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
