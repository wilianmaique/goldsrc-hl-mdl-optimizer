using System;
using System.Globalization;
using System.IO;

namespace MdlOptimizer
{
    // Keep the arguments with the key so a job started in one language can
    // still be displayed in another after a preference change.
    [Serializable]
    public sealed class LocalizedText : IFormattable
    {
        readonly string key;
        readonly object[] arguments;

        public LocalizedText(string key, params object[] arguments)
        {
            this.key = key;
            this.arguments = arguments;
        }

        public override string ToString() { return L.T(key, arguments); }

        public string ToString(string format, IFormatProvider provider)
        {
            var culture = provider as CultureInfo;
            return culture == null ? ToString() : L.Format(culture.Name, key, arguments);
        }

        // Report profile and view identifiers have historically used Portuguese.
        internal string StableText { get { return L.Format("pt-BR", key, arguments); } }
        internal string Key { get { return key; } }

        public T Attach<T>(T error) where T : Exception
        {
            error.Data[typeof(LocalizedText)] = this;
            return error;
        }

        internal T AsException<T>(Func<string,T> factory) where T : Exception
        {
            return Attach(factory(ToString()));
        }

        public static string Error(Exception error)
        {
            var message = error.Data[typeof(LocalizedText)] as LocalizedText;
            return message == null ? error.Message : message.ToString();
        }

        internal static InvalidDataException InvalidData(string key, params object[] args)
        {
            var text = new LocalizedText(key, args);
            return text.Attach(new InvalidDataException(text.ToString()));
        }
    }
}
