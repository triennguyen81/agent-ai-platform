using System.IO.Compression;
using System.Text;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Storage;
using Microsoft.Extensions.Options;

namespace Aiq.Gateway.Vouchers;

/// <summary>
/// Sprint 6' A3: build gói hồ sơ ZIP để kế toán gửi cho ngân hàng /
/// kiểm toán — theo BƯỚC 4 workflow của invoice-lifecycle-workflow.md.
///
/// Format:
///   bang-ke.csv           - UTF-8 with BOM, Vietnamese headers
///   chung-tu/{doc}_{name} - copy nguyên file gốc từ MinIO/S3
///
/// Filename output: Export_{NCC-clean}_{fromDate}_{toDate}.zip
/// </summary>
public sealed class VoucherExportService
{
    private readonly IVoucherRepository _repo;
    private readonly IObjectStorage _storage;
    private readonly S3Options _s3;
    private readonly ILogger<VoucherExportService> _logger;

    // Excel opens UTF-8 CSV only if BOM prefix is present. Otherwise VN
    // characters hiển thị mojibake (Ã, á»‡, ...) — same issue kế toán viên
    // gặp khi mở CSV ExpenseRisk cũ.
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public VoucherExportService(
        IVoucherRepository repo,
        IObjectStorage storage,
        IOptions<S3Options> s3Options,
        ILogger<VoucherExportService> logger)
    {
        _repo = repo;
        _storage = storage;
        _s3 = s3Options.Value;
        _logger = logger;
    }

    public async Task WriteZipAsync(
        Guid tenantId,
        Guid userId,
        VoucherExportFilter filter,
        Stream output,
        CancellationToken ct)
    {
        // Limit hard-cap 500 để tránh over-large zip trong 1 request.
        // Kế toán muốn > 500 chứng từ nên chia theo tháng.
        var vouchers = await _repo.ListAsync(
            tenantId, userId,
            voucherType: filter.VoucherType,
            statusFilter: filter.Status,
            search: null,
            sellerMst: filter.SellerMst,
            documentDateFrom: filter.DocumentDateFrom,
            documentDateTo: filter.DocumentDateTo,
            limit: 500,
            offset: 0,
            ct);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // 1. bang-ke.csv
        var csvEntry = zip.CreateEntry("bang-ke.csv", CompressionLevel.Optimal);
        await using (var csvStream = csvEntry.Open())
        {
            await csvStream.WriteAsync(Utf8Bom, ct);
            await using var writer = new StreamWriter(csvStream, new UTF8Encoding(false));
            await writer.WriteLineAsync(
                "STT,Loại chứng từ,Số HĐ,Ngày HĐ,Tên NCC,MST NCC,Tổng tiền,VAT,Trạng thái,Ghi chú");
            var idx = 0;
            foreach (var v in vouchers)
            {
                idx++;
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    idx.ToString(),
                    CsvEscape(v.VoucherType),
                    CsvEscape(v.DocumentNumber ?? ""),
                    v.DocumentDate?.ToString("yyyy-MM-dd") ?? "",
                    CsvEscape(v.SellerName ?? ""),
                    CsvEscape(v.SellerMst ?? ""),
                    v.TotalAmount?.ToString("0.##") ?? "",
                    v.VatAmount?.ToString("0.##") ?? "",
                    CsvEscape(v.Status),
                    CsvEscape(v.ErrorMessage ?? ""),
                }));
            }
        }

        // 2. chung-tu/{safe-name}
        // Skip vouchers without source file. Duplicate filenames get suffixed.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vouchers)
        {
            if (string.IsNullOrWhiteSpace(v.SourceFilePath))
            {
                continue;
            }
            var entryName = MakeSafeEntryName(v, usedNames);
            try
            {
                await using var s3Stream = await _storage.GetAsync(
                    _s3.VoucherBucket, v.SourceFilePath, ct);
                var entry = zip.CreateEntry($"chung-tu/{entryName}", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await s3Stream.CopyToAsync(entryStream, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Voucher export: skip file for voucher {VoucherId} at key {Key}",
                    v.Id, v.SourceFilePath);
                // Ghi lại missing file vào readme trong ZIP
                var missingEntry = zip.CreateEntry("MISSING_FILES.txt", CompressionLevel.Fastest);
                await using var mStream = missingEntry.Open();
                await using var mWriter = new StreamWriter(mStream);
                await mWriter.WriteLineAsync($"{v.DocumentNumber ?? v.Id.ToString()} — {v.SourceFileName ?? v.SourceFilePath}");
            }
        }
    }

    public static string BuildFilename(VoucherExportFilter filter)
    {
        var parts = new List<string> { "Export" };
        if (!string.IsNullOrWhiteSpace(filter.SellerMst))
        {
            parts.Add(SanitizeToken(filter.SellerMst));
        }
        if (filter.DocumentDateFrom.HasValue)
        {
            parts.Add(filter.DocumentDateFrom.Value.ToString("yyyyMMdd"));
        }
        if (filter.DocumentDateTo.HasValue)
        {
            parts.Add(filter.DocumentDateTo.Value.ToString("yyyyMMdd"));
        }
        return string.Join("_", parts) + ".zip";
    }

    private static string MakeSafeEntryName(VoucherRecord v, HashSet<string> used)
    {
        var docNumber = SanitizeToken(v.DocumentNumber ?? v.Id.ToString("N")[..8]);
        var originalName = v.SourceFileName ?? "file";
        var ext = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(ext))
        {
            ext = v.SourceMimeType switch
            {
                "application/pdf" => ".pdf",
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => "",
            };
        }
        var baseName = $"{docNumber}_{Path.GetFileNameWithoutExtension(originalName)}";
        baseName = SanitizeToken(baseName);
        var candidate = baseName + ext;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName}_{suffix}{ext}";
            suffix++;
        }
        return candidate;
    }

    private static string SanitizeToken(string input)
    {
        var chars = input.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

public sealed record VoucherExportFilter(
    string? VoucherType,
    string? Status,
    string? SellerMst,
    DateOnly? DocumentDateFrom,
    DateOnly? DocumentDateTo);
