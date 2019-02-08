// Copyright (c) 2018-2019 phosphene47
// 
// This software is released under the MIT License.
// https://opensource.org/licenses/MIT

using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace nhitomi
{
    public static class TokenGenerator
    {
        public struct TokenPayload
        {
            public string Source;
            public string Id;
            public DateTime? Expires;
        }

        public static string CreateToken(
            this IDoujin doujin,
            string secret,
            Encoding encoding = null,
            JsonSerializer serializer = null,
            double expiresIn = double.PositiveInfinity
        )
        {
            encoding = encoding ?? Encoding.UTF8;
            serializer = serializer ?? JsonSerializer.CreateDefault();

            // Create identity
            var payloadData = new TokenPayload
            {
                Source = doujin.Source.Name,
                Id = doujin.Id,
                Expires = double.IsInfinity(expiresIn)
                    ? (DateTime?)null
                    : DateTime.UtcNow.AddMinutes(expiresIn)
            };

            // Serialize payload
            string payload;

            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream, encoding))
            {
                serializer.Serialize(writer, payloadData);
                writer.Flush();

                payload = Convert.ToBase64String(stream.ToArray());
            }

            // Signature
            var signature = getPayloadSignature(payload, secret, encoding);

            // Token (similar to JWT, without header)
            return $"{payload}.{signature}";
        }

        static string getPayloadSignature(string payload, string secret, Encoding encoding)
        {
            using (var hmac = new HMACSHA256(encoding.GetBytes(secret)))
                return Convert
                    .ToBase64String(hmac.ComputeHash(encoding.GetBytes(payload)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        public static bool TryDeserializeToken(
            string token,
            string secret,
            out string sourceName,
            out string id,
            Encoding encoding = null,
            JsonSerializer serializer = null,
            bool validateExpiry = true
        )
        {
            try
            {
                encoding = encoding ?? Encoding.UTF8;
                serializer = serializer ?? JsonSerializer.CreateDefault();

                // Get parts
                var payload = token.Substring(0, token.IndexOf('.'));
                var signature = token.Substring(token.IndexOf('.') + 1);

                // Verify signature
                if (signature != getPayloadSignature(payload, secret, encoding))
                {
                    sourceName = null;
                    id = null;
                    return false;
                }

                // Deserialize payload
                TokenPayload payloadData;

                using (var stream = new MemoryStream(Convert.FromBase64String(payload)))
                using (var streamReader = new StreamReader(stream, encoding))
                using (var jsonReader = new JsonTextReader(streamReader))
                    payloadData = serializer.Deserialize<TokenPayload>(jsonReader);

                // Test expiry time
                if (validateExpiry &&
                    DateTime.UtcNow >= payloadData.Expires)
                {
                    sourceName = null;
                    id = null;
                    return false;
                }

                sourceName = payloadData.Source;
                id = payloadData.Id;
                return true;
            }
            catch
            {
                sourceName = null;
                id = null;
                return false;
            }
        }
    }
}