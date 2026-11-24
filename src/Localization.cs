using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading;

[assembly: InternalsVisibleTo("MdlOptimizer")]
[assembly: InternalsVisibleTo("MdlOptimizer.Cli")]
[assembly: InternalsVisibleTo("WorkflowTests")]

namespace MdlOptimizer
{
    internal static class L
    {
        static readonly string[] Codes = { "pt-BR", "en", "es", "ru" };
        static readonly CultureInfo[] Formats = Array.ConvertAll(
            new[] { "pt-BR", "en-US", "es-ES", "ru-RU" }, CultureInfo.GetCultureInfo);
        static readonly Dictionary<string, string[]> Catalog = LoadCatalog();
        static CultureInfo systemCulture;
        static string settingsFile;
        static volatile string preference;

        static L()
        {
            Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDL Optimizer", "language.txt"), CultureInfo.CurrentUICulture);
        }

        static Dictionary<string, string[]> LoadCatalog()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var assembly = typeof(L).Assembly;
            foreach (var name in assembly.GetManifestResourceNames()) {
                if (!name.StartsWith("MdlOptimizer.Strings", StringComparison.Ordinal)) continue;
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(name), Encoding.UTF8)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.Length == 0 || line[0] == '#') continue;
                        var cells = line.Split('\t');
                        if (cells.Length != Codes.Length + 1 || Array.Exists(cells, String.IsNullOrWhiteSpace))
                            throw new InvalidDataException("Invalid translation in " + name + ": " + cells[0]);
                        var values = new string[Codes.Length];
                        for (int i = 0; i < values.Length; i++)
                            values[i] = cells[i + 1].Replace("\\n", Environment.NewLine);
                        result.Add(cells[0], values);
                    }
                }
            }
            return result;
        }

        internal static void Initialize(string path, CultureInfo system)
        {
            settingsFile = path;
            systemCulture = system;
            preference = "";
            try {
                string stored = File.ReadAllText(path).Trim();
                if (Array.IndexOf(Codes, stored) >= 0) preference = stored;
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            } catch (SecurityException) { }
        }

        static int LanguageIndex(CultureInfo culture)
        {
            switch (culture.TwoLetterISOLanguageName) {
                case "pt": return 0;
                case "es": return 2;
                case "ru": return 3;
                default: return 1;
            }
        }

        public static string Preference { get { return preference; } }
        public static string Language { get { return Codes[LanguageIndex(Culture)]; } }
        public static CultureInfo Culture
        {
            get {
                int index = Array.IndexOf(Codes, preference);
                if (index >= 0) return Formats[index];
                index = LanguageIndex(systemCulture);
                if (index != 0 && !systemCulture.IsNeutralCulture &&
                    systemCulture.TwoLetterISOLanguageName == Codes[index]) return systemCulture;
                return Formats[index];
            }
        }

        // CLI overrides apply to one invocation. Only the window saves a preference.
        internal static void UseForSession(string code)
        {
            if (code != "" && Array.IndexOf(Codes, code) < 0) throw new ArgumentOutOfRangeException("code");
            preference = code;
            ApplyUICulture();
        }

        internal static void ApplyUICulture()
        {
            var culture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        public static bool SetPreference(string code)
        {
            UseForSession(code);
            string temp = null;
            try {
                if (code == "") {
                    try { File.Delete(settingsFile); }
                    catch (DirectoryNotFoundException) { }
                    return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                temp = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, code, new UTF8Encoding(false));
                try { File.Move(temp, settingsFile); }
                catch (IOException) {
                    if (!File.Exists(settingsFile)) throw;
                    File.Replace(temp, settingsFile, null);
                }
                return true;
            } catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            finally {
                if (temp != null && File.Exists(temp)) {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (SecurityException) { }
                }
            }
        }

        public static string T(string key, params object[] args)
        {
            return Render(Culture, key, args);
        }

        internal static string Format(string language, string key, params object[] args)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return Render(culture.IsNeutralCulture ? Formats[LanguageIndex(culture)] : culture, key, args);
        }

        static string Render(CultureInfo culture, string key, object[] args)
        {
            string format = Catalog[key][LanguageIndex(culture)];
            return args.Length == 0 ? format : String.Format(culture, format, args);
        }
    }
}
