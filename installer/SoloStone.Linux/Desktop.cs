using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SoloStone
{
    // The applications-menu entry runs `solostone play --desktop` without a terminal. The launcher's messages go to a log,
    // the start to a notification, and errors and client warnings to a dialog: kdialog on KDE, zenity elsewhere.
    internal static class Desktop
    {
        internal const string Title="SoloStone";
        static string StateHome => Environment.GetEnvironmentVariable("XDG_STATE_HOME") is string x && x.StartsWith("/") ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","state");
        internal static string LogPath => Path.Combine(StateHome,"solostone","launcher.log");

        internal const long LogLimit=1<<20;
        // Appended to, because a second click on the menu entry must not wipe the running game's log; past LogLimit it
        // moves to launcher.log.old. A log that can't be written never stops the game.
        internal static Action<string> OpenLog() {
            string path=LogPath;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if(File.Exists(path) && new FileInfo(path).Length>LogLimit) File.Move(path,path+".old",true);
            } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { }
            return line=>{ try { File.AppendAllText(path,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")+line+"\n"); } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { } };
        }

        internal static bool KdeSession => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")??"").Split(':').Contains("KDE");
        // The dialog programs to try, in order. kind is "error", or "confirm" (exit code 0 means go ahead).
        // zenity reads Pango markup in --text unless told not to; a message can hold < and & from a path.
        internal static string[][] Dialogs(string kind,string message,bool kde) {
            string[] kdialog=kind=="error" ? new[]{"kdialog","--title",Title,"--error",message}
                : new[]{"kdialog","--title",Title,"--warningcontinuecancel",message};
            string[] zenity=kind=="error" ? new[]{"zenity","--error","--no-markup","--title="+Title,"--text="+message}
                : new[]{"zenity","--question","--no-markup","--title="+Title,"--text="+message,"--ok-label=Play","--cancel-label=Cancel"};
            return kde ? new[]{kdialog,zenity} : new[]{zenity,kdialog};
        }
        // The dialog's exit code, or null when neither program is installed.
        static int? Show(string kind,string message) {
            foreach(string[] command in Dialogs(kind,message,KdeSession)) {
                string program=LinuxConfig.OnPath(command[0]); if(program==null) continue;
                var start=new ProcessStartInfo(program) { UseShellExecute=false };
                foreach(string part in command.Skip(1)) start.ArgumentList.Add(part);
                try { using(var dialog=Process.Start(start)) { dialog.WaitForExit(); return dialog.ExitCode; } }
                catch(System.ComponentModel.Win32Exception) { }
            }
            return null;
        }
        internal static void Error(string message,string icon) { if(Show("error",message)==null) Notify(message,icon); }
        // With no dialog program the warning can't be confirmed here; the error that follows says to use --accept-warning.
        internal static bool Confirm(string message) => Show("confirm",message+"\n\nPlay anyway?")==0;
        internal static void Notify(string message,string icon) {
            string program=LinuxConfig.OnPath("notify-send"); if(program==null) return;
            var start=new ProcessStartInfo(program) { UseShellExecute=false };
            foreach(string part in new[]{"--app-name="+Title}.Concat(icon!=null && File.Exists(icon) ? new[]{"--icon="+icon} : new string[0]).Concat(new[]{Title,message}))
                start.ArgumentList.Add(part);
            try { using(var notify=Process.Start(start)) notify.WaitForExit(5000); } catch(System.ComponentModel.Win32Exception) { }
        }
    }
}
