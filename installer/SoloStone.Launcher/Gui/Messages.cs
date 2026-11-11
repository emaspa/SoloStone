using System;
using System.IO;

namespace SoloStone
{
    // Windows reported "permission was cancelled" (error 1223), or the prompt timed out, for the firewall step. Nothing else was changed:
    // setup keeps the copy, and uninstall stops before it removes anything (the rules go first).
    public sealed class FirewallCancelledException : InvalidOperationException
    {
        public const string RemovingText = "Permission wasn't given, so nothing was removed. Click Uninstall again.";
        public FirewallCancelledException(bool removing=false) : base(removing ? RemovingText
            : "Firewall permission was cancelled. SoloStone was kept, but can't start until the firewall rules are added.") { }
    }
    public static class Messages
    {
        public const string UpdateFinishing = "SoloStone is finishing an update. The launcher opens by itself when it is done.";
        // Exit 42: the plugin's own checks. detail names what the client lacks.
        public static string ClientBlocked(string detail) => "This Hearthstone client no longer matches SoloStone, so the game stopped"
            + (string.IsNullOrEmpty(detail) ? "." : ": " + detail + ".") + " Your saves were not changed. SoloStone needs an update for this client.";
        // Exit 47: the game's firewall check refused. reason is that check's own sentence, run again by the launcher; null if it now passes.
        public static string FirewallRefused(string reason) => "The game could not verify SoloStone's firewall protection and stopped. "
            + (string.IsNullOrEmpty(reason) ? "Fix the firewall protection, then play." : reason);
        // One place for what a player sees when something goes wrong. The command line and the windows share it.
        public static string Friendly(Exception e) {
            if (e is InvalidOperationException) return e.Message;
            if (e is OperationCanceledException) return "Stopped. Nothing else was changed.";
            if (e is UnauthorizedAccessException) return "Windows denied access to a folder. Choose a folder your account owns.";
            if (e is IOException) return "A required file is missing, busy, or cannot be copied. Quit Battle.net and the game, check available space, then try again.";
            return "SoloStone could not complete this step. Your original game was not changed. Details: "+Record(e);
        }
        public static string Unexpected(Exception e) => "SoloStone hit a problem in this window. Close SoloStone and start it again. Details: "+Record(e);
        // An unexpected failure must stay diagnosable: the friendly sentence hides the type and stack.
        static string Record(Exception e) {
            string path=Path.Combine(Path.GetTempPath(),"SoloStone-error.log");
            try { File.AppendAllText(path,DateTime.UtcNow.ToString("o")+" "+e+Environment.NewLine); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            return path;
        }
        public static bool Expected(Exception e) => e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException || e is OperationCanceledException;
    }
}
