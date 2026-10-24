using System.Runtime.InteropServices;
using System.Text;

namespace ZeppieSays.Core;

public static class UserName
{
    /// <summary>The first name from the Windows account's display name ("Dirk Vaessen" or "Vaessen, Dirk"), or "".</summary>
    public static string First { get; } = Read();

    static string Read()
    {
        try
        {
            var size = 256u;
            var buffer = new StringBuilder((int)size);
            if (GetUserNameEx(NameDisplay, buffer, ref size) && buffer.Length > 0)
            {
                var name = buffer.ToString();
                var comma = name.IndexOf(',');
                var first = comma >= 0 ? name[(comma + 1)..].Trim() : name.Trim();
                return first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            }
        }
        catch { /* fall back to the account name */ }
        var user = Environment.UserName;
        return user.Contains('.') ? "" : user; // "D.Vaessen" reads worse than no name
    }

    const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserNameEx(int nameFormat, StringBuilder buffer, ref uint size);
}
