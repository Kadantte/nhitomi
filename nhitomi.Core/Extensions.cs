using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace nhitomi.Core
{
    public static class Extensions
    {
        public static async Task<HttpRequestMessage> CloneAsync(this HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = await request.Content.CloneAsync().ConfigureAwait(false),
                Version = request.Version
            };

            foreach (var prop in request.Properties)
                clone.Properties.Add(prop);

            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }

        public static async Task<HttpContent> CloneAsync(this HttpContent content)
        {
            if (content == null)
                return null;

            var ms = new MemoryStream();

            await content.CopyToAsync(ms).ConfigureAwait(false);

            ms.Position = 0;

            var clone = new StreamContent(ms);

            foreach (var header in content.Headers)
                clone.Headers.Add(header.Key, header.Value);

            return clone;
        }

        public static bool OrderlessEquals<T>(this IEnumerable<T> enumerable,
                                              IEnumerable<T> other)
        {
            var set = new HashSet<T>(enumerable);

            // items in other must be present in set
            if (other.Any(item => !set.Remove(item)))
                return false;

            // set should not be a superset
            return set.Count == 0;
        }

        public static async Task<T> DeserializeAsync<T>(this JsonSerializer serializer,
                                                        HttpContent content)
        {
            using (var reader = new StringReader(await content.ReadAsStringAsync()))
            using (var jsonReader = new JsonTextReader(reader))
                return serializer.Deserialize<T>(jsonReader);
        }

        public static T Deserialize<T>(this JsonSerializer serializer,
                                       Stream stream)
        {
            using (var reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
                return serializer.Deserialize<T>(jsonReader);
        }

        public static T[] Subarray<T>(this T[] array,
                                      int index) => array.Subarray(index, array.Length - index);

        public static T[] Subarray<T>(this T[] array,
                                      int index,
                                      int length)
        {
            var subarray = new T[length];

            Array.Copy(array, index, subarray, 0, length);

            return subarray;
        }

        /// <summary>
        /// https://stackoverflow.com/a/24087164
        /// </summary>
        public static IEnumerable<IEnumerable<T>> ChunkBy<T>(this IEnumerable<T> source,
                                                             int chunkSize) => source
                                                                              .Select((x,
                                                                                       i) => new
                                                                               {
                                                                                   Index = i, Value = x
                                                                               })
                                                                              .GroupBy(x => x.Index / chunkSize)
                                                                              .Select(x => x.Select(v => v.Value));

        public static IDoujinClient FindByName(this IEnumerable<IDoujinClient> clients,
                                               string name) => clients.FirstOrDefault(
            c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static void Destructure<T>(this T[] items,
                                          out T t0,
                                          out T t1)
        {
            t0 = items.Length > 0 ? items[0] : default;
            t1 = items.Length > 1 ? items[1] : default;
        }

        public static int ReadInt32Be(this BinaryReader reader)
        {
            var buffer = reader.ReadBytes(sizeof(int));

            if (BitConverter.IsLittleEndian)
                Array.Reverse(buffer);

            return BitConverter.ToInt32(buffer, 0);
        }

        public static ulong ReadUInt64Be(this BinaryReader reader)
        {
            var buffer = reader.ReadBytes(sizeof(ulong));

            if (BitConverter.IsLittleEndian)
                Array.Reverse(buffer);

            return BitConverter.ToUInt64(buffer, 0);
        }

        public static string SubstringFromEnd(this string str,
                                              int count) => str.Substring(str.Length - count, count);

        public static string RemoveFromEnd(this string str,
                                           int count) => str.Remove(str.Length - count, count);

        public static string Format(this double[] elapsed)
        {
            var time = elapsed[0];

            if (time < 100)
                return $"{Math.Round(time, 3)}ms";

            if (time < 60000)
                return $"{Math.Round(time / 1000, 2)}s";

            return $"{Math.Round(time / 60000, 2)}m";
        }

        public static IEnumerable<T> NullIfEmpty<T>(this IEnumerable<T> source) => source.Any() ? source : null;

        public static IDisposable Measure(out double[] elapsed) => new MeasureContext(elapsed = new double[1]);

        public class MeasureContext : IDisposable
        {
            public readonly double[] Store;
            public readonly Stopwatch Watch;

            public MeasureContext(double[] store)
            {
                Store = store;
                Watch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                Watch.Stop();
                Store[0] = Watch.Elapsed.TotalMilliseconds;
            }
        }

        public static IAsyncEnumerable<T> Interleave<T>(IEnumerable<IAsyncEnumerable<T>> source) =>
            AsyncEnumerable.CreateEnumerable(() =>
            {
                var enumerators = source.Select(s => s.GetEnumerator()).ToList();
                var current     = -1;

                return AsyncEnumerable.CreateEnumerator(
                    async token =>
                    {
                        while (enumerators.Count > 0)
                        {
                            // Next enumerator
                            if (++current >= enumerators.Count)
                                current = 0;

                            if (await enumerators[current].MoveNext(token))
                                return true;

                            // Remove failing enumerators
                            enumerators.RemoveAt(current);
                        }

                        return false;
                    },
                    () => enumerators[current].Current,
                    () =>
                    {
                        foreach (var enumerator in enumerators)
                            enumerator.Dispose();
                    }
                );
            });

        public static Task<T> AsCompletedTask<T>(this T obj) => Task.FromResult(obj);

        /// Computes and returns the Damerau-Levenshtein edit distance between two strings, 
        /// i.e. the number of insertion, deletion, sustitution, and transposition edits
        /// required to transform one string to the other. This value will be >= 0, where 0
        /// indicates identical strings. Comparisons are case sensitive, so for example, 
        /// "Fred" and "fred" will have a distance of 1. This algorithm is basically the
        /// Levenshtein algorithm with a modification that considers transposition of two
        /// adjacent characters as a single edit.
        /// http://blog.softwx.net/2015/01/optimizing-damerau-levenshtein_15.html
        /// </summary>
        /// <remarks>See http://en.wikipedia.org/wiki/Damerau%E2%80%93Levenshtein_distance
        /// Note that this is based on Sten Hjelmqvist's "Fast, memory efficient" algorithm, described
        /// at http://www.codeproject.com/Articles/13525/Fast-memory-efficient-Levenshtein-algorithm.
        /// This version differs by including some optimizations, and extending it to the Damerau-
        /// Levenshtein algorithm.
        /// Note that this is the simpler and faster optimal string alignment (aka restricted edit) distance
        /// that difers slightly from the classic Damerau-Levenshtein algorithm by imposing the restriction
        /// that no substring is edited more than once. So for example, "CA" to "ABC" has an edit distance
        /// of 2 by a complete application of Damerau-Levenshtein, but a distance of 3 by this method that
        /// uses the optimal string alignment algorithm. See wikipedia article for more detail on this
        /// distinction.
        /// </remarks>
        /// <param name="s">String being compared for distance.</param>
        /// <param name="t">String being compared against other string.</param>
        /// <param name="maxDistance">The maximum edit distance of interest.</param>
        /// <returns>int edit distance, >= 0 representing the number of edits required
        /// to transform one string to the other, or -1 if the distance is greater than the specified maxDistance.</returns>
        public static int DamLev(this string s,
                                 string t,
                                 int maxDistance = int.MaxValue)
        {
            if (string.IsNullOrEmpty(s))
                return ((t ?? "").Length <= maxDistance) ? (t ?? "").Length : -1;

            if (string.IsNullOrEmpty(t))
                return (s.Length <= maxDistance) ? s.Length : -1;

            // if strings of different lengths, ensure shorter string is in s. This can result in a little
            // faster speed by spending more time spinning just the inner loop during the main processing.
            if (s.Length > t.Length)
            {
                var temp = s;
                s = t;
                t = temp; // swap s and t
            }

            var sLen = s.Length; // this is also the minimun length of the two strings
            var tLen = t.Length;

            // suffix common to both strings can be ignored
            while ((sLen > 0) && (s[sLen - 1] == t[tLen - 1]))
            {
                sLen--;
                tLen--;
            }

            var start = 0;

            if ((s[0] == t[0]) || (sLen == 0))
            {
                // if there's a shared prefix, or all s matches t's suffix
                // prefix common to both strings can be ignored
                while ((start < sLen) && (s[start] == t[start]))
                    start++;

                sLen -= start; // length of the part excluding common prefix and suffix
                tLen -= start;

                // if all of shorter string matches prefix and/or suffix of longer string, then
                // edit distance is just the delete of additional characters present in longer string
                if (sLen == 0)
                    return (tLen <= maxDistance) ? tLen : -1;

                t = t.Substring(start, tLen); // faster than t[start+j] in inner loop below
            }

            var lenDiff = tLen - sLen;

            if ((maxDistance < 0) || (maxDistance > tLen))
                maxDistance = tLen;
            else if (lenDiff > maxDistance)
                return -1;

            var v0 = new int[tLen];
            var v2 = new int[tLen]; // stores one level further back (offset by +1 position)
            int j;

            for (j = 0; j < maxDistance; j++)
                v0[j] = j + 1;

            for (; j < tLen; j++)
                v0[j] = maxDistance + 1;

            var jStartOffset = maxDistance - (tLen - sLen);
            var haveMax      = maxDistance < tLen;
            var jStart       = 0;
            var jEnd         = maxDistance;
            var sChar        = s[0];
            var current      = 0;

            for (var i = 0; i < sLen; i++)
            {
                var prevsChar = sChar;
                sChar = s[start + i];
                var tChar = t[0];
                var left  = i;
                current = left + 1;
                var nextTransCost = 0;
                // no need to look beyond window of lower right diagonal - maxDistance cells (lower right diag is i - lenDiff)
                // and the upper left diagonal + maxDistance cells (upper left is i)
                jStart += (i > jStartOffset) ? 1 : 0;
                jEnd   += (jEnd < tLen) ? 1 : 0;

                for (j = jStart; j < jEnd; j++)
                {
                    var above         = current;
                    var thisTransCost = nextTransCost;
                    nextTransCost = v2[j];
                    v2[j]         = current = left; // cost of diagonal (substitution)

                    left =
                        v0[j]; // left now equals current cost (which will be diagonal at next iteration)

                    var prevtChar = tChar;
                    tChar = t[j];

                    if (sChar != tChar)
                    {
                        if (left < current)
                            current = left; // insertion

                        if (above < current)
                            current = above; // deletion

                        current++;

                        if ((i != 0) && (j != 0)
                                     && (sChar == prevtChar)
                                     && (prevsChar == tChar))
                        {
                            thisTransCost++;

                            if (thisTransCost < current)
                                current = thisTransCost; // transposition
                        }
                    }

                    v0[j] = current;
                }

                if (haveMax && (v0[i + lenDiff] > maxDistance))
                    return -1;
            }

            return (current <= maxDistance) ? current : -1;
        }

        // https://stackoverflow.com/questions/281640/how-do-i-get-a-human-readable-file-size-in-bytes-abbreviation-using-net
        // Returns the human-readable file size for an arbitrary, 64-bit file size 
        // The default format is "0.### XB", e.g. "4.2 KB" or "1.434 GB"
        public static string GetBytesReadable(this long i)
        {
            // Get absolute value
            var absolute_i = (i < 0 ? -i : i);
            // Determine the suffix and readable value
            string suffix;
            double readable;

            if (absolute_i >= 0x1000000000000000) // Exabyte
            {
                suffix   = "eb";
                readable = (i >> 50);
            }
            else if (absolute_i >= 0x4000000000000) // Petabyte
            {
                suffix   = "pd";
                readable = (i >> 40);
            }
            else if (absolute_i >= 0x10000000000) // Terabyte
            {
                suffix   = "tb";
                readable = (i >> 30);
            }
            else if (absolute_i >= 0x40000000) // Gigabyte
            {
                suffix   = "gb";
                readable = (i >> 20);
            }
            else if (absolute_i >= 0x100000) // Megabyte
            {
                suffix   = "mb";
                readable = (i >> 10);
            }
            else if (absolute_i >= 0x400) // Kilobyte
            {
                suffix   = "kb";
                readable = i;
            }
            else
            {
                return i.ToString("0 b"); // Byte
            }

            // Divide by 1024 to get fractional value
            readable = (readable / 1024);
            // Return formatted number with suffix
            return readable.ToString("0.### ") + suffix;
        }

        static Regex _namedFormatRegex = new Regex(@"\{(.+?)\}", RegexOptions.Compiled);

        /// <summary>
        /// https://stackoverflow.com/questions/1010123/named-string-format-is-it-possible
        /// Thanks Pavlo Neyman
        /// </summary>
        public static string NamedFormat(this string format,
                                         IDictionary<string, object> values) => _namedFormatRegex
                                                                               .Matches(format)
                                                                               .Cast<Match>()
                                                                               .Select(m => m.Groups[1].Value)
                                                                               .Aggregate(format,
                                                                                          (current,
                                                                                           key) =>
                                                                                          {
                                                                                              var colonIndex =
                                                                                                  key.IndexOf(':');

                                                                                              return current.Replace(
                                                                                                  $"{{{key}}}",
                                                                                                  colonIndex > 0
                                                                                                      ? string.Format(
                                                                                                          $"{{0:{key.Substring(colonIndex + 1)}}}",
                                                                                                          values[
                                                                                                              key
                                                                                                                 .Substring(
                                                                                                                      0,
                                                                                                                      colonIndex)] ??
                                                                                                          string.Empty)
                                                                                                      : values[key]
                                                                                                          ?.ToString() ??
                                                                                                        string.Empty);
                                                                                          });

        public static string Serialize(this JsonSerializer json,
                                       object value)
        {
            using (var writer = new StringWriter())
            {
                json.Serialize(writer, value);

                return writer.ToString();
            }
        }

        public static T Deserialize<T>(this JsonSerializer json,
                                       string value)
        {
            using (var reader = new StringReader(value))
            using (var jsonReader = new JsonTextReader(reader))
                return json.Deserialize<T>(jsonReader);
        }
    }
}