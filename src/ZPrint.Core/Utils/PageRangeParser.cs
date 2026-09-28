using System;
using System.Collections.Generic;
using System.Linq;

namespace ZPrint.Core.Utils
{
    /// <summary>
    /// Utility for parsing and validating human-readable print page specifications (e.g. "1, 3, 5-8, 12").
    /// </summary>
    public static class PageRangeParser
    {
        /// <summary>
        /// Parses a page range string into an ordered, deduplicated list of 1-based page numbers.
        /// </summary>
        /// <param name="rangeSpec">Expression such as "1, 3, 5-8, 10". Pass null or empty for all pages.</param>
        /// <param name="maxPages">Optional upper bound for pages (inclusive).</param>
        /// <returns>Sorted list of selected page numbers.</returns>
        public static IReadOnlyList<int> Parse(string? rangeSpec, int maxPages = int.MaxValue)
        {
            if (string.IsNullOrWhiteSpace(rangeSpec) || rangeSpec!.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (maxPages != int.MaxValue && maxPages > 0)
                {
                    return Enumerable.Range(1, maxPages).ToList();
                }
                return Array.Empty<int>();
            }

            var pages = new HashSet<int>();
            string[] tokens = rangeSpec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                string item = token.Trim();
                if (string.IsNullOrEmpty(item)) continue;

                int dashIndex = item.IndexOf('-');
                if (dashIndex > 0 && dashIndex < item.Length - 1)
                {
                    string startPart = item.Substring(0, dashIndex).Trim();
                    string endPart = item.Substring(dashIndex + 1).Trim();

                    if (int.TryParse(startPart, out int start) && int.TryParse(endPart, out int end))
                    {
                        if (start > end)
                        {
                            int tmp = start;
                            start = end;
                            end = tmp;
                        }

                        start = Math.Max(1, start);
                        end = Math.Min(maxPages, end);

                        for (int p = start; p <= end; p++)
                        {
                            pages.Add(p);
                        }
                    }
                }
                else
                {
                    if (int.TryParse(item, out int singlePage))
                    {
                        if (singlePage >= 1 && singlePage <= maxPages)
                        {
                            pages.Add(singlePage);
                        }
                    }
                }
            }

            return pages.OrderBy(p => p).ToList();
        }

        /// <summary>
        /// Determines whether a given 1-based page number is included in the page range.
        /// </summary>
        public static bool IsPageIncluded(int pageNumber, IReadOnlyList<int> selectedPages)
        {
            if (selectedPages == null || selectedPages.Count == 0) return true;
            return selectedPages.Contains(pageNumber);
        }
    }
}
