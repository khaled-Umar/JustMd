using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace JustMd.Spelling
{
    /// <summary>The user's Windows language list, in order of preference.</summary>
    internal static class UserLanguages
    {
        private const string KeyPath = @"Control Panel\International\User Profile";
        private const string ValueName = "Languages";

        public static IReadOnlyList<string> Read()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key?.GetValue(ValueName) is string[] languages && languages.Length > 0)
                {
                    return languages;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                // Fall back to the display language.
            }

            return new[] { CultureInfo.CurrentUICulture.Name };
        }
    }
}
