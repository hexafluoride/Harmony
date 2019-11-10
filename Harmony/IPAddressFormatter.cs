using MessagePack;
using MessagePack.Formatters;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Harmony
{
    public class IPAddressResolver : IFormatterResolver
    {
        public static IPAddressResolver Instance = new IPAddressResolver();

        public IMessagePackFormatter<T> GetFormatter<T>()
        {
            return FormatterCache<T>.Formatter;
        }

        private static class FormatterCache<T>
        {
            public static readonly IMessagePackFormatter<T> Formatter;

            // generic's static constructor should be minimized for reduce type generation size!
            // use outer helper method.
            static FormatterCache()
            {
                Formatter = (IMessagePackFormatter<T>)IPAddressResolverGetFormatterHelper.GetFormatter(typeof(T));
            }
        }
    }

    public class IPAddressFormatter : IMessagePackFormatter<IPAddress>
    {
        public static IPAddressFormatter Instance = new IPAddressFormatter();

        public IPAddress Deserialize(byte[] bytes, int offset, IFormatterResolver formatterResolver, out int readSize)
        {
            readSize = 4;
            return new IPAddress(bytes.AsSpan(offset, 4).ToArray());
        }

        public int Serialize(ref byte[] bytes, int offset, IPAddress value, IFormatterResolver formatterResolver)
        {
            var addr_bytes = value.GetAddressBytes();
            Array.Copy(addr_bytes, 0, bytes, offset, addr_bytes.Length);

            return addr_bytes.Length;
        }
    }

    internal static class IPAddressResolverGetFormatterHelper
    {
        // If type is concrete type, use type-formatter map
        static readonly Dictionary<Type, object> formatterMap = new Dictionary<Type, object>()
        {
            {typeof(IPAddress), new IPAddressFormatter()}
        };

        internal static object GetFormatter(Type t)
        {
            object formatter;
            if (formatterMap.TryGetValue(t, out formatter))
            {
                return formatter;
            }

            // If target type is generics, use MakeGenericType.
            if (t.IsGenericParameter && t.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
            {
                return Activator.CreateInstance(typeof(ValueTupleFormatter<,>).MakeGenericType(t.GenericTypeArguments));
            }

            // If type can not get, must return null for fallback mecanism.
            return null;
        }
    }
}
