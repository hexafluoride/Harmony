using Microsoft.AspNetCore.StaticFiles;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    public static class MimeMapper
    {
        public static FileExtensionContentTypeProvider Provider = new FileExtensionContentTypeProvider();

        public static string MapToMime(string filename)
        {
            if (!Provider.TryGetContentType(filename, out string mime_type))
            {
                mime_type = "application/octet-stream";
            }
            return mime_type;
        }

        public static bool WildcardMatch(string filter, string input)
        {
            if (filter.EndsWith('*'))
                return input.StartsWith(filter.Substring(0, filter.Length - 1));

            return filter == input;
        }
    }
}
