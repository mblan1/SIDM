using System.Security.Cryptography;
using System.Text;

namespace SIDM.Ipc;

public static class PipeNameProvider
{
    /// <summary>
    /// Returns a pipe name unique to the current OS user. Different users on the
    /// same machine get different pipes, so a child user's BrowserHost can't
    /// reach the admin's running SIDM and vice versa.
    /// </summary>
    public static string ForCurrentUser()
    {
        var user = Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user));
        var slug = Convert.ToHexString(hash, 0, 4).ToLowerInvariant(); // 8 hex chars
        return $"SIDM.IPC.{slug}";
    }
}
