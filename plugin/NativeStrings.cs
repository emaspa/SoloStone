using System;

internal static class NativeStrings
{
    internal static string Get(string key, string english) => Resolve(key, GameStrings.Get(key), english);

    // A class name standing on its own (a heading); inside an English sentence keep LocalCoreProgression.ClassName.
    internal static string ClassName(string english) => Get("GLOBAL_CLASS_" + english.Replace(" ", "").ToUpperInvariant(), english);

    internal static string Resolve(string key, string localized, string english) =>
        string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal) ? english : localized;
}
