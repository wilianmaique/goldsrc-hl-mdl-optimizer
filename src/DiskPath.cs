using System;
using System.IO;

namespace MdlOptimizer
{
    /// <summary>Normalizes ordinary Windows file paths before using extended DOS/UNC syntax for I/O.</summary>
    public static class DiskPath
    {
        const string Extended = @"\\?\";
        const string ExtendedUnc = @"\\?\UNC\";

        public static string Display(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (path.StartsWith(ExtendedUnc, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(ExtendedUnc.Length);
            if (path.StartsWith(Extended, StringComparison.Ordinal) && HasDriveRoot(path, Extended.Length))
                return path.Substring(Extended.Length);
            return path;
        }

        public static string Full(string path)
        {
            return Normalize(path, false);
        }

        /// <summary>Use when descending through directory trees whose children may exceed MAX_PATH.</summary>
        public static string Tree(string path)
        {
            return Normalize(path, true);
        }

        static string Normalize(string path, bool forTree)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new LocalizedText("path.empty").AsException(message => new ArgumentException(message, "path"));
            string normalized = path.Replace('/', '\\');
            bool extended = normalized.StartsWith(Extended, StringComparison.Ordinal);
            string ordinary = Display(normalized);
            if (ordinary.StartsWith(Extended, StringComparison.Ordinal) || ordinary.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new LocalizedText("path.unsupported").AsException(message => new NotSupportedException(message));

            // Removing the device prefix first gives relative segments and mixed
            // separators the normal Win32 meaning before bypassing normalization.
            string absolute = Path.GetFullPath(ordinary);
            if (!extended && !forTree && absolute.Length < 248) return absolute;
            if (absolute.StartsWith(@"\\", StringComparison.Ordinal))
                return ExtendedUnc + absolute.Substring(2);
            if (HasDriveRoot(absolute, 0)) return Extended + absolute;
            return absolute;
        }

        static bool HasDriveRoot(string path, int i)
        {
            if (path.Length < i + 3) return false;
            char c = path[i];
            return ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                && path[i+1] == ':' && path[i+2] == '\\';
        }
    }
}
