namespace Aiq.Gateway.Dlp;

/// <summary>
/// Sprint 7 DLP Phase A — các loại PII mà pipeline detect trước khi gửi
/// text ra LLM cloud. Thứ tự enum trùng với priority khi nhiều detector
/// cùng match một span (CMND > phone > bank account để tránh ambiguity).
///
/// Phase A chỉ cover 9 loại regex-based. Full name + địa chỉ cần NER
/// model → Phase D (sau).
/// </summary>
public enum PiiType
{
    /// <summary>CMND 9 chữ số — giấy tờ cũ, đang bị thay bằng CCCD.</summary>
    Cmnd,
    /// <summary>CCCD 12 chữ số — thẻ căn cước công dân hiện hành.</summary>
    Cccd,
    /// <summary>Mã số thuế (MST) 10 hoặc 13 chữ số.</summary>
    Mst,
    /// <summary>Số điện thoại VN (mobile hoặc landline).</summary>
    Phone,
    /// <summary>Email address.</summary>
    Email,
    /// <summary>Số tài khoản ngân hàng (6-19 chữ số với context "STK/TK").</summary>
    BankAccount,
    /// <summary>Credit/debit card number (13-19 chữ số Luhn-valid).</summary>
    CreditCard,
    /// <summary>Hộ chiếu VN: 1 ký tự + 7 chữ số.</summary>
    Passport,
    /// <summary>Mã bảo hiểm xã hội 10 chữ số với context "BHXH".</summary>
    Bhxh,
    /// <summary>Ngày sinh dạng dd/mm/yyyy hoặc dd-mm-yyyy với context "sinh".</summary>
    DateOfBirth,
}

/// <summary>
/// Mức phân loại dữ liệu theo spec AIQ v0.1 (section Data Architecture).
/// Dùng để quyết định PII có được gửi ra LLM nào — Restricted KHÔNG BAO
/// GIỜ được gửi ra cloud, chỉ xử lý bằng LLM self-host của khách.
/// </summary>
public enum DataClassification
{
    Public,
    Internal,
    Confidential,
    Restricted,
}

public static class PiiTypeExtensions
{
    /// <summary>
    /// Prefix dùng cho token placeholder. SHORT + UPPERCASE để dễ nhận
    /// biết trong text đã mask (ví dụ "&lt;CMND_a1b2c3d4e5&gt;").
    /// </summary>
    public static string TokenPrefix(this PiiType type) => type switch
    {
        PiiType.Cmnd => "CMND",
        PiiType.Cccd => "CCCD",
        PiiType.Mst => "MST",
        PiiType.Phone => "PHONE",
        PiiType.Email => "EMAIL",
        PiiType.BankAccount => "ACCT",
        PiiType.CreditCard => "CARD",
        PiiType.Passport => "PSPT",
        PiiType.Bhxh => "BHXH",
        PiiType.DateOfBirth => "DOB",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown PII type"),
    };

    /// <summary>
    /// Mức phân loại mặc định cho mỗi loại PII. Có thể bị override bởi
    /// policy ở Phase C (classification registry per-tenant).
    /// </summary>
    public static DataClassification DefaultClassification(this PiiType type) => type switch
    {
        PiiType.Mst => DataClassification.Internal,       // MST công khai trên hoá đơn — vẫn sensitive nhưng không như CMND
        PiiType.Email => DataClassification.Internal,
        PiiType.Cmnd => DataClassification.Confidential,
        PiiType.Cccd => DataClassification.Confidential,
        PiiType.Phone => DataClassification.Confidential,
        PiiType.BankAccount => DataClassification.Confidential,
        PiiType.Passport => DataClassification.Confidential,
        PiiType.Bhxh => DataClassification.Confidential,
        PiiType.DateOfBirth => DataClassification.Confidential,
        PiiType.CreditCard => DataClassification.Restricted,  // card never leaves customer boundary
        _ => DataClassification.Internal,
    };
}
