using System.Text.RegularExpressions;
using Aiq.Gateway.Dlp.Detectors;

namespace Aiq.Gateway.Dlp;

/// <summary>
/// Orchestrator: chạy tất cả detector, resolve overlap, replace bằng
/// token, lưu vault. Un-mask bằng cách regex tìm token rồi lookup vault.
/// </summary>
public sealed partial class PiiScrubber : IPiiScrubber
{
    private readonly IReadOnlyList<IPiiDetector> _detectors;
    private readonly DeterministicMasker _masker;

    public PiiScrubber(IEnumerable<IPiiDetector> detectors, DeterministicMasker masker)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        ArgumentNullException.ThrowIfNull(masker);
        _detectors = detectors.ToList();
        _masker = masker;
    }

    public ScrubResult Scrub(string text) => Scrub(text, DeterministicMasker.GenerateSalt());

    public ScrubResult Scrub(string text, string salt)
    {
        var vault = new PiiVault(salt);
        if (string.IsNullOrEmpty(text))
        {
            return new ScrubResult(text ?? string.Empty, vault, 0);
        }

        // 1. Collect tất cả matches từ mọi detector
        var allMatches = new List<PiiMatch>();
        foreach (var detector in _detectors)
        {
            allMatches.AddRange(detector.Detect(text));
        }

        if (allMatches.Count == 0)
        {
            return new ScrubResult(text, vault, 0);
        }

        // 2. Resolve overlap: khi 2 match overlap, giữ match có PiiType
        //    priority cao hơn (enum order là priority — CMND > Phone > Bank).
        //    Match sắp xếp theo (Start asc, Type asc để tie-break).
        var sorted = allMatches
            .OrderBy(m => m.Start)
            .ThenBy(m => (int)m.Type)
            .ToList();

        var finalMatches = new List<PiiMatch>();
        PiiMatch? last = null;
        foreach (var m in sorted)
        {
            if (last is null || !last.Overlaps(m))
            {
                finalMatches.Add(m);
                last = m;
            }
            else if ((int)m.Type < (int)last.Type)
            {
                // Match mới có priority cao hơn — thay thế
                finalMatches[^1] = m;
                last = m;
            }
        }

        // 3. Replace từ cuối text về đầu để không shift index
        var builder = new System.Text.StringBuilder(text);
        foreach (var m in finalMatches.OrderByDescending(x => x.Start))
        {
            var token = _masker.Mask(m.OriginalValue, salt, m.Type);
            vault.Store(token, m.OriginalValue);
            builder.Remove(m.Start, m.Length);
            builder.Insert(m.Start, token);
        }

        return new ScrubResult(builder.ToString(), vault, finalMatches.Count);
    }

    public string Unmask(string text, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Match pattern "<PREFIX_xxxxxxxxxx>" nơi PREFIX gồm chữ hoa + xxxx
        // là hex lowercase length 10
        return TokenRegex().Replace(text, match =>
        {
            if (vault.TryGetOriginal(match.Value, out var original) && original is not null)
            {
                return original;
            }
            // Token không có trong vault — giữ nguyên defensive, không throw
            return match.Value;
        });
    }

    [GeneratedRegex(@"<[A-Z]+_[a-f0-9]{10}>", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
