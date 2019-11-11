using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace Harmony
{
    public static class Utilities
    {
        public static byte[] ParseBytesFromString(string str)
        {
            byte[] data = new byte[str.Length / 2];
            for (int index = 0; index < data.Length; index++)
            {
                string byteValue = str.Substring(index * 2, 2);
                data[index] = byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return data;
        }

        public static bool TryParseIPEndPoint(string possible_ep, out IPEndPoint ep)
        {
            try
            {
                var addr = IPAddress.Parse(possible_ep.Split(':')[0]);
                var port = ushort.Parse(possible_ep.Split(':')[1]);

                ep = new IPEndPoint(addr, port);
                return true;
            }
            catch { ep = default; return false; }
        }
    }
}
