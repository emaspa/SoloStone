using System;
using System.Text.RegularExpressions;

namespace SoloStone
{
    // The install code is shared with Windows, and some of its messages give the Windows launcher's commands. Linux players get
    // the solostone commands instead.
    internal static class LinuxMessages
    {
        static readonly Regex Command=new Regex(@"SoloStone\.exe (recover|uninstall) --root ""[^""]*""");
        internal static string Adapt(string text) {
            if(text==null) return null;
            if(text.StartsWith("Recovery complete. Run SoloStone.exe firewall",StringComparison.Ordinal))
                return "Recovery complete. Start SoloStone from your applications menu, or with: solostone play";
            text=Command.Replace(text,"solostone $1");
            return text.Replace("rerun the installer at the original root (see INSTALL-prototype.md)","run solostone setup again")
                .Replace("Then rerun the installer.","Then run solostone setup again.")
                .Replace("Windows denied access to a folder. Choose a folder your account owns.","Access to a folder was denied. Choose a folder your account owns.");
        }
    }
}
