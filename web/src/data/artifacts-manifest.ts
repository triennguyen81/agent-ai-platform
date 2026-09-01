// Sprint 4.5 — Artifact Library metadata.
//
// 10 chứng từ mẫu Việt Đăng dùng cho: (1) onboarding kế toán viên mới,
// (2) demo pilot cho khách xem hệ thống hỗ trợ các loại nào, (3) tài liệu
// training. Mỗi artifact có 3-step walkthrough interactive để user học:
//   Bước 1 · Nhận diện loại chứng từ (quiz 4 option)
//   Bước 2 · Fields LLM sẽ extract (7-8 field với explanation)
//   Bước 3 · Bút toán suggest + quiz nhỏ

export type VoucherTypeCode =
  | "invoice_vat_out"
  | "invoice_vat_in"
  | "receipt"
  | "payment"
  | "bank_transfer"
  | "expense_claim";

export interface ArtifactField {
  field: string;
  label: string;
  value: string;
  hint: string;
}

export interface JournalEntry {
  debit: string;
  credit: string;
  amount: number;
  description: string;
}

export interface ArtifactQuizOption {
  text: string;
  correct?: boolean;
}

export interface Artifact {
  id: string;
  filename: string;
  title: string;
  description: string;
  voucherType: VoucherTypeCode;
  voucherTypeLabel: string;
  amount: number;
  currency: string;
  tt200Reference: string;
  // Learning content
  step1Options: ArtifactQuizOption[];
  step1Explanation: string;
  step2Fields: ArtifactField[];
  step3JournalEntries: JournalEntry[];
  step3QuizQuestion: string;
  step3QuizOptions: ArtifactQuizOption[];
  step3Explanation: string;
  riskFlags: string[];
  keyLesson: string;
}

const CHOICES = {
  invoice_vat_out: "Hoá đơn bán (đầu ra) — voucher_type=invoice_vat_out",
  invoice_vat_in: "Hoá đơn mua (đầu vào) — voucher_type=invoice_vat_in",
  receipt: "Phiếu thu tiền — voucher_type=receipt",
  payment: "Phiếu chi tiền mặt — voucher_type=payment",
  bank_transfer: "Uỷ nhiệm chi qua NH — voucher_type=bank_transfer",
  expense_claim: "Đề nghị hoàn ứng NV — voucher_type=expense_claim",
};

function opts(correct: VoucherTypeCode, wrongs: VoucherTypeCode[]): ArtifactQuizOption[] {
  return [
    { text: CHOICES[correct], correct: true },
    ...wrongs.map((k) => ({ text: CHOICES[k] })),
  ].sort(() => 0); // preserve order — quiz is deterministic
}

export const ARTIFACTS: Artifact[] = [
  {
    id: "01-hoa-don-vat-nha-xinh-30tr",
    filename: "01-hoa-don-vat-nha-xinh-30tr.png",
    title: "Hoá đơn VAT bán — Nha Xinh 30tr",
    description:
      "HĐ đầu ra bán cho phòng khám Nha Xinh, thanh toán chuyển khoản 30 ngày (bán chịu).",
    voucherType: "invoice_vat_out",
    voucherTypeLabel: "Hoá đơn bán (đầu ra)",
    amount: 30000000,
    currency: "VND",
    tt200Reference: "TT200 điều 27 (TK 131 phải thu khách hàng)",
    step1Options: opts("invoice_vat_out", ["invoice_vat_in", "receipt", "payment"]),
    step1Explanation:
      "Tiêu đề 'HOÁ ĐƠN GTGT' + Bên bán là Việt Đăng (MST 0311999999 — cty mình) → HĐ đầu ra. Nếu bên bán không phải mình thì mới là HĐ đầu vào.",
    step2Fields: [
      {
        field: "seller_mst",
        label: "MST bên bán",
        value: "0311999999",
        hint: "MST Việt Đăng — luôn cố định trong mọi HĐ đầu ra. LLM dùng để phân biệt in/out.",
      },
      {
        field: "buyer_mst",
        label: "MST bên mua",
        value: "0311234567",
        hint: "MST khách — dùng lookup sim_customers để enrich AR context (credit_limit, current_ar, aging).",
      },
      {
        field: "document_number",
        label: "Số HĐ",
        value: "00001091",
        hint: "Unique per seller trong 1 năm. Dùng cho duplicate detection E2 (BR-2).",
      },
      {
        field: "total_amount",
        label: "Tổng thanh toán",
        value: "30,000,000 VNĐ",
        hint: "Trigger risk flag `invoice_over_500m_requires_l2_approval` nếu > 500M.",
      },
      {
        field: "vat_rate",
        label: "Thuế suất VAT",
        value: "10%",
        hint: "Chuẩn TT78. Case đặc biệt: 5% (thiết bị y tế), 8% (2024 giảm), 0% (xuất khẩu).",
      },
      {
        field: "payment_method",
        label: "Hình thức TT",
        value: "CK",
        hint: "CK = chuyển khoản (Nợ 131). Nếu TM = tiền mặt trả ngay (Nợ 111).",
      },
    ],
    step3JournalEntries: [
      {
        debit: "131",
        credit: "5111",
        amount: 27272727,
        description: "Doanh thu bán hàng chưa thuế (30M / 1.10)",
      },
      {
        debit: "131",
        credit: "33311",
        amount: 2727273,
        description: "VAT đầu ra 10%",
      },
    ],
    step3QuizQuestion: "Tại sao dùng TK 131 mà không phải TK 111 hoặc 112?",
    step3QuizOptions: [
      { text: "Vì khách bán chịu — chưa thu tiền, ghi tăng công nợ 131", correct: true },
      { text: "Vì khách trả ngay tiền mặt (nên dùng 111)" },
      { text: "Vì khách chuyển khoản (nên dùng 112)" },
      { text: "Vì luôn dùng 131 cho tất cả HĐ bán" },
    ],
    step3Explanation:
      "TK 131 = Phải thu khách hàng. Khi bán chịu (payment_method=CK nhưng chưa nhận tiền), ghi Nợ 131 tăng công nợ. Sau khi khách thu tiền → phiếu thu ghi Có 131 giảm nợ (xem mẫu 04).",
    riskFlags: [],
    keyLesson:
      "Bán chịu qua CK 30 ngày là pattern phổ biến nhất cho phòng khám. Cần theo dõi aging để đòi đúng hạn.",
  },
  {
    id: "02-hoa-don-vat-bv-rhm-650tr",
    filename: "02-hoa-don-vat-bv-rhm-650tr.png",
    title: "Hoá đơn VAT bán — BV RHM 650tr",
    description:
      "HĐ giá trị lớn (650M) bán cho BV Răng Hàm Mặt — trigger L2 approval flag.",
    voucherType: "invoice_vat_out",
    voucherTypeLabel: "Hoá đơn bán (đầu ra) — giá trị lớn",
    amount: 651000000,
    currency: "VND",
    tt200Reference: "TT200 điều 27, TT78 quy định về HĐ giá trị cao",
    step1Options: opts("invoice_vat_out", ["invoice_vat_in", "bank_transfer", "receipt"]),
    step1Explanation:
      "Cùng loại HĐ bán như mẫu 01 nhưng giá trị 650M lớn → hệ thống fire risk flag L2 approval. Kế toán trưởng phải duyệt trước khi accept.",
    step2Fields: [
      {
        field: "seller_mst",
        label: "MST bên bán",
        value: "0311999999",
        hint: "Việt Đăng — như mọi HĐ đầu ra.",
      },
      {
        field: "buyer_mst",
        label: "MST bên mua",
        value: "0311999001",
        hint: "MST BV RHM — sim_customers có category='benh_vien' với credit_limit 2 tỷ.",
      },
      {
        field: "total_amount",
        label: "Tổng thanh toán",
        value: "651,000,000 VNĐ",
        hint: "> 500M → trigger `invoice_over_500m_requires_l2_approval` — cần duyệt.",
      },
      {
        field: "line_items",
        label: "Chi tiết dòng",
        value: "3 items (ghế KaVo E80 x 2 + module implant)",
        hint: "LLM parse array line_items[] với description + quantity + unit_price + vat.",
      },
      {
        field: "vat_rate",
        label: "VAT",
        value: "5% (thiết bị y tế)",
        hint: "Ghế nha khoa thuộc nhóm thiết bị y tế → VAT 5% (không phải 10%).",
      },
    ],
    step3JournalEntries: [
      {
        debit: "131",
        credit: "5111",
        amount: 620000000,
        description: "Doanh thu bán TB y tế chưa VAT",
      },
      {
        debit: "131",
        credit: "33311",
        amount: 31000000,
        description: "VAT đầu ra 5% (thiết bị y tế)",
      },
    ],
    step3QuizQuestion: "Rủi ro nào hệ thống flag đầu tiên cho HĐ này?",
    step3QuizOptions: [
      { text: "invoice_over_500m_requires_l2_approval — cần kế toán trưởng duyệt", correct: true },
      { text: "has_overdue_90d — nợ quá hạn" },
      { text: "would_breach_credit_limit — vượt hạn mức" },
      { text: "Không có flag nào" },
    ],
    step3Explanation:
      "Threshold 500M được config trong appsettings ExpenseRisk.L2ApprovalAbove. HĐ 651M > 500M → banner amber trên detail page + button Accept vẫn hiện nhưng cần approval trước khi UC-ACC-002 (Sprint 5).",
    riskFlags: ["invoice_over_500m_requires_l2_approval"],
    keyLesson:
      "HĐ lớn cho BV có 2 điểm khác PK: (1) VAT 5% không 10% vì TB y tế, (2) cần L2 approval. Aging BV thường 60-90 ngày (không 30 như PK).",
  },
  {
    id: "03-hoa-don-vat-dong-a-15tr",
    filename: "03-hoa-don-vat-dong-a-15tr.png",
    title: "Hoá đơn VAT bán — Đông Á 15tr",
    description: "HĐ giá trị nhỏ — customer_not_found scenario (MST chưa có sim_customers).",
    voucherType: "invoice_vat_out",
    voucherTypeLabel: "Hoá đơn bán (đầu ra) — KH mới",
    amount: 15000000,
    currency: "VND",
    tt200Reference: "TT200 điều 27",
    step1Options: opts("invoice_vat_out", ["invoice_vat_in", "expense_claim", "payment"]),
    step1Explanation: "Cùng pattern HĐ đầu ra, nhưng đây là case KH mới chưa có trong master.",
    step2Fields: [
      {
        field: "buyer_mst",
        label: "MST bên mua",
        value: "0311234571",
        hint: "MST PK Đông Á — case KH master có sẵn (PK-005). Nếu MST không match, ap_context.status='customer_not_found' → banner amber.",
      },
      {
        field: "total_amount",
        label: "Tổng",
        value: "15,000,000 VNĐ",
        hint: "Không trigger L2 (< 500M).",
      },
    ],
    step3JournalEntries: [
      {
        debit: "131",
        credit: "5111",
        amount: 13636364,
        description: "Doanh thu chưa VAT",
      },
      { debit: "131", credit: "33311", amount: 1363636, description: "VAT 10%" },
    ],
    step3QuizQuestion: "Nếu MST khách không có trong sim_customers, hệ thống làm gì?",
    step3QuizOptions: [
      { text: "Trả ar_context.status='customer_not_found' + hiện UI cho tạo KH mới", correct: true },
      { text: "Reject voucher, không cho accept" },
      { text: "Tự tạo customer mới ngay lập tức" },
      { text: "Bỏ qua và save voucher không có AR context" },
    ],
    step3Explanation:
      "Fallback graceful — voucher vẫn extract được, chỉ thiếu AR context. Kế toán cần tạo KH master trước (endpoint POST /customers, tương lai Sprint 5) hoặc verify MST trên HĐ có bị OCR sai không.",
    riskFlags: [],
    keyLesson:
      "Case customer_not_found rất phổ biến khi KH mới. UX cần thân thiện — không block workflow, chỉ nhắc user hoàn thiện master data.",
  },
  {
    id: "04-phieu-thu-nha-xinh",
    filename: "04-phieu-thu-nha-xinh.png",
    title: "Phiếu thu — Nha Xinh 183.75M",
    description:
      "Phiếu thu áp cho 3 HĐ đầu ra của Nha Xinh, có chiết khấu thanh toán sớm 3.75M.",
    voucherType: "receipt",
    voucherTypeLabel: "Phiếu thu tiền + chiết khấu",
    amount: 183750000,
    currency: "VND",
    tt200Reference: "TT200 điều 27 (Có 131), điều 43 (TK 5211 chiết khấu TT)",
    step1Options: opts("receipt", ["invoice_vat_out", "payment", "bank_transfer"]),
    step1Explanation:
      "Tiêu đề 'PHIẾU THU', có payer + số tiền thu. Khác HĐ bán ở chỗ không có seller/buyer thuế mà là payer + payee.",
    step2Fields: [
      {
        field: "payer_mst",
        label: "MST người trả",
        value: "0311234567",
        hint: "MST Nha Xinh — LLM có thể extract vào payer_mst hoặc buyer_mst. Backend fallback qua cả 2 keys.",
      },
      {
        field: "amount",
        label: "Số tiền thu",
        value: "183,750,000 VNĐ",
        hint: "Trigger FIFO matching — apply oldest invoice trước.",
      },
      {
        field: "discount_hint",
        label: "Gợi ý chiết khấu",
        value: "3,750,000 (~2%)",
        hint: "Shortfall ≤ 3% + invoice chưa quá hạn → auto detect chiết khấu TT sớm.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "1121",
        credit: "131",
        amount: 183750000,
        description: "Nhận TT khách qua NH",
      },
      {
        debit: "5211",
        credit: "131",
        amount: 3750000,
        description: "Chiết khấu thanh toán sớm cho KH",
      },
    ],
    step3QuizQuestion: "Chiết khấu 3.75M hạch toán TK nào?",
    step3QuizOptions: [
      { text: "Nợ 5211 / Có 131 — giảm phải thu, ghi giảm doanh thu", correct: true },
      { text: "Nợ 6421 / Có 131 — chi phí bán hàng" },
      { text: "Nợ 811 / Có 131 — chi phí khác" },
      { text: "Không cần hạch toán riêng — chỉ ghi thu số tiền thực tế" },
    ],
    step3Explanation:
      "TT200 điều 43: Chiết khấu TT sớm = giảm trừ doanh thu (contra revenue) → TK 5211. FIFO matching áp đủ giá trị invoice (Có 131) + discount row (Nợ 5211) để invoice remaining_after = 0.",
    riskFlags: [],
    keyLesson:
      "FIFO oldest first + discount auto-detect là 2 innovation lớn của UC2. Kế toán viên bấm 1 nút → hệ thống lo cả 2 việc.",
  },
  {
    id: "05-uy-nhiem-chi-kavo",
    filename: "05-uy-nhiem-chi-kavo.png",
    title: "Uỷ nhiệm chi — KaVo 412.5M (deposit 30%)",
    description: "UNC chuyển 15,000 EUR ~ 412.5M đợt deposit 30% cho KaVo theo PO-2026-0032.",
    voucherType: "bank_transfer",
    voucherTypeLabel: "Uỷ nhiệm chi qua NH (deposit)",
    amount: 412500000,
    currency: "VND",
    tt200Reference: "TT200 điều 24 (TK 331 phải trả NCC), điều 25 (TK 112 TGNH)",
    step1Options: opts("bank_transfer", ["payment", "invoice_vat_in", "receipt"]),
    step1Explanation:
      "Tiêu đề 'UỶ NHIỆM CHI' + có tên NH + số IBAN nước ngoài → bank_transfer. Khác payment (phiếu chi) ở chỗ đi qua NH, không phải quầy TM.",
    step2Fields: [
      {
        field: "beneficiary_mst",
        label: "MST người hưởng",
        value: "DE812526315 (KaVo)",
        hint: "MST nước ngoài (VAT-ID DE) — LLM extract vào beneficiary_mst. Backend map → sim_suppliers.",
      },
      {
        field: "amount",
        label: "Số tiền chi",
        value: "412,500,000 VNĐ (15,000 EUR)",
        hint: "Multi-currency — cần capture exchange_rate.",
      },
      {
        field: "reference",
        label: "Nội dung TT",
        value: "PO-2026-0032 deposit 30%",
        hint: "Ghi rõ PO ref để đối chiếu 3-way match sau này.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "331",
        credit: "1122",
        amount: 412500000,
        description: "Trả trước NCC KaVo 30% deposit (TGNH ngoại tệ EUR)",
      },
    ],
    step3QuizQuestion: "Đây là 'trả trước' — bên NCC chưa giao hàng. TK 331 hạch toán ra sao?",
    step3QuizOptions: [
      { text: "TK 331 chuyển sang dư Nợ — coi như ứng trước NCC", correct: true },
      { text: "Không dùng TK 331, dùng TK 141 (tạm ứng)" },
      { text: "Chỉ ghi Có 1122 mà không đối ứng TK nào" },
      { text: "Dùng TK 138 (phải thu khác)" },
    ],
    step3Explanation:
      "TK 331 mặc định dư Có (phải trả). Khi trả trước NCC (deposit) → tạm thời có dư Nợ. Sau khi nhận đủ HĐ + hàng, 2 khoản đối trừ → về 0.",
    riskFlags: [],
    keyLesson:
      "T/T deposit pattern rất phổ biến khi mua nhập khẩu (NCC nước ngoài yêu cầu 30-50% trước ship). Cần track riêng để không double-count.",
  },
  {
    id: "06-de-nghi-hoan-ung",
    filename: "06-de-nghi-hoan-ung.png",
    title: "Hoàn ứng — Trần Văn Đức HN 6.35M",
    description:
      "NV-0007 công tác Hà Nội 4 ngày, chi 11.35M — ứng trước 5M, đề nghị hoàn 6.35M.",
    voucherType: "expense_claim",
    voucherTypeLabel: "Đề nghị hoàn ứng nhân viên",
    amount: 11350000,
    currency: "VND",
    tt200Reference: "TT200 điều 20 (TK 141 tạm ứng), điều 39 (TK 6417 CP bán hàng)",
    step1Options: opts("expense_claim", ["payment", "invoice_vat_in", "bank_transfer"]),
    step1Explanation:
      "Có employee_code + items[] (airfare/hotel/meal/transport) + advance_taken → expense_claim, không phải payment thường.",
    step2Fields: [
      {
        field: "employee_code",
        label: "Mã NV",
        value: "NV-0007",
        hint: "Match sim_employees.employee_code → lookup outstanding_advance, ytd_expense, budget.",
      },
      {
        field: "total_claim",
        label: "Tổng chi",
        value: "11,350,000 VNĐ",
        hint: "Sum các items — dùng cho budget check.",
      },
      {
        field: "advance_taken",
        label: "Tạm ứng đã lấy",
        value: "5,000,000 VNĐ",
        hint: "Đối trừ TK 141 khi apply.",
      },
      {
        field: "items[]",
        label: "Chi tiết items",
        value: "4 khoản: vé máy bay, KS, ăn uống, đi lại",
        hint: "Mỗi item có category → map sang TK. VD ăn uống → 6417 (dept KD) hoặc 6428.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "6417",
        credit: "141",
        amount: 5000000,
        description: "Đối trừ tạm ứng đã lấy",
      },
      {
        debit: "6417",
        credit: "1111",
        amount: 6350000,
        description: "Hoàn tiền phần chi vượt tạm ứng",
      },
    ],
    step3QuizQuestion: "advance_covered = min(total_claim, outstanding_advance) = ?",
    step3QuizOptions: [
      { text: "min(11.35M, 5M) = 5M — trừ TK 141 5M, còn 6.35M chi tiền mặt (TK 111)", correct: true },
      { text: "11.35M — trừ toàn bộ tổng chi vào 141" },
      { text: "6.35M — chỉ số tiền hoàn" },
      { text: "0 — không đối trừ ứng" },
    ],
    step3Explanation:
      "Logic: nếu tạm ứng >= tổng chi → không cần hoàn, thậm chí NV còn trả lại. Nếu tạm ứng < tổng chi → hoàn phần thiếu. `min()` bảo vệ trường hợp NV đã dùng hết ứng.",
    riskFlags: [],
    keyLesson:
      "Hoàn ứng phổ biến nhất trong SME. Cần 2 tài khoản: 141 (đối trừ ứng) + 111/112 (chi thêm). Nhiều kế toán viên bỏ qua bước đối trừ 141 → sổ 141 phình lên vô lý.",
  },
  {
    id: "07-hoa-don-ncc-kavo",
    filename: "07-hoa-don-ncc-kavo.png",
    title: "HĐ NCC — KaVo 1.375 tỷ (PO match)",
    description:
      "Commercial Invoice KaVo cho ghế E80, khớp PO-2026-0032 + GRN-2026-078 — full 3-way match.",
    voucherType: "invoice_vat_in",
    voucherTypeLabel: "Hoá đơn mua NCC (đầu vào)",
    amount: 1375000000,
    currency: "VND",
    tt200Reference: "TT200 điều 24 (TK 331), TT78 điều 15 (VAT được khấu trừ)",
    step1Options: opts("invoice_vat_in", ["invoice_vat_out", "bank_transfer", "payment"]),
    step1Explanation:
      "Bên bán = KaVo (NCC nước ngoài, VAT-ID DE) — không phải Việt Đăng → đầu vào. Buyer = Việt Đăng. Universal-extractor tự phân biệt qua so sánh MST bán/mua với tenant.",
    step2Fields: [
      {
        field: "seller_mst",
        label: "MST bên bán (NCC)",
        value: "DE812526315",
        hint: "MST nước ngoài — lookup sim_suppliers theo mst hoặc supplier_code. Nếu không match → supplier_not_found.",
      },
      {
        field: "total_amount",
        label: "Tổng HĐ",
        value: "1,375,000,000 VNĐ (50,000 EUR)",
        hint: "> 500M + > 100M → trigger L2 approval + đòi hỏi PO match.",
      },
      {
        field: "po_reference",
        label: "PO reference",
        value: "PO-2026-0032",
        hint: "LLM có thể extract vào matched_invoice_number field — dùng cho 3-way match.",
      },
      {
        field: "incoterm",
        label: "Điều khoản giao hàng",
        value: "CIF Ho Chi Minh Port",
        hint: "Ảnh hưởng khi ghi nhận: CIF = trách nhiệm ship + insurance của NCC; FOB = mình lo.",
      },
      {
        field: "exchange_rate",
        label: "Tỷ giá",
        value: "27,500 VND/EUR",
        hint: "Tỷ giá VCB ngày hạch toán. Chênh lệch tỷ giá vào TK 635/515 khi thanh toán.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "1561",
        credit: "331",
        amount: 1237500000,
        description: "Nhập kho ghế KaVo E80 (chưa VAT)",
      },
      {
        debit: "242",
        credit: "331",
        amount: 137500000,
        description: "CP đào tạo + lắp đặt (phân bổ)",
      },
    ],
    step3QuizQuestion: "Tại sao HĐ này KHÔNG có bút toán TK 1331 (VAT đầu vào)?",
    step3QuizOptions: [
      { text: "Vì VAT nhập khẩu 10% do hải quan thu riêng — có tờ khai HQ khác", correct: true },
      { text: "Vì NCC nước ngoài không có VAT đầu vào Việt Nam" },
      { text: "Vì HĐ dùng EUR không phải VND" },
      { text: "Vì thiết bị y tế được miễn VAT" },
    ],
    step3Explanation:
      "Nhập khẩu 2 loại thuế: (1) thuế nhập khẩu 5%, (2) VAT nhập khẩu 10% — cả 2 do hải quan thu tại port và có tờ khai riêng. VAT nhập khẩu ghi Nợ 1331 / Có 33312 khi làm tờ khai. HĐ NCC chỉ chứa giá trị hàng + service.",
    riskFlags: ["expense_over_500m_requires_l2_approval"],
    keyLesson:
      "Import invoice = 3 bút toán: HĐ NCC (Nợ 156/242 / Có 331) + Tờ khai HQ (Nợ 1331 / Có 33312 cho VAT NK) + Tờ khai thuế NK (Nợ 156/242 / Có 3333). Kế toán viên hay quên bước 3.",
  },
  {
    id: "08-hoan-ung-cong-tac",
    filename: "08-hoan-ung-cong-tac.png",
    title: "Hoàn ứng — Đà Nẵng có tiếp khách 11.35M",
    description:
      "NV-0007 công tác ĐN 3 ngày, có 1 khoản tiếp khách 2.7M → trigger TT96 warning.",
    voucherType: "expense_claim",
    voucherTypeLabel: "Đề nghị hoàn ứng NV có TT96",
    amount: 11350000,
    currency: "VND",
    tt200Reference: "TT200 điều 20 + điều 39, TT96/2015 điều 4 khoản 2.21",
    step1Options: opts("expense_claim", ["payment", "invoice_vat_in", "bank_transfer"]),
    step1Explanation:
      "Cùng loại expense_claim như mẫu 06, khác biệt là có item 'Tiếp khách 2.7M' → hệ thống fire TT96 warning để nhắc theo dõi 15% cap.",
    step2Fields: [
      {
        field: "items[4].category",
        label: "Category item 5",
        value: "'Tiếp khách'",
        hint: "MapCategoryToAccount detect → TK 6428 + tt96_flagged=true.",
      },
      {
        field: "items[4].amount",
        label: "Amount tiếp khách",
        value: "2,700,000 VNĐ",
        hint: "Chi phí quà/tiếp khách được khấu trừ tối đa 15% tổng chi phí trong kỳ (TT96/2015).",
      },
      {
        field: "has_tt96_category",
        label: "TT96 flag",
        value: "true",
        hint: "Fire warning box amber trong ExpenseClaimPanel — nhắc kế toán track riêng.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "6417",
        credit: "141",
        amount: 5000000,
        description: "Đối trừ tạm ứng (chi phí BH)",
      },
      {
        debit: "6417",
        credit: "1111",
        amount: 3650000,
        description: "Hoàn 4 khoản (máy bay + KS + ăn + đi lại)",
      },
      {
        debit: "6428",
        credit: "1111",
        amount: 2700000,
        description: "Chi phí tiếp khách (TT96 - track riêng)",
      },
    ],
    step3QuizQuestion: "Vì sao tiếp khách hạch toán 6428 mà không 6417 dù NV là dept KD?",
    step3QuizOptions: [
      { text: "TT96 xếp tiếp khách vào chi phí quản lý DN (6428) để track 15% cap riêng", correct: true },
      { text: "Vì tiếp khách không thuộc bán hàng nên không được ghi 6417" },
      { text: "Vì 6428 có mức khấu trừ VAT khác 6417" },
      { text: "Vì tiếp khách phải hạch toán tổng vào 6428 rồi phân bổ" },
    ],
    step3Explanation:
      "TT96 quy định chi tiếp khách/quà biếu bị cap 15% tổng chi phí. Nếu trộn vào 6417 → khó track. Best practice: 6428 riêng + monitoring script cuối quý so với tổng CP.",
    riskFlags: [],
    keyLesson:
      "Sales dept nhưng tiếp khách vẫn về 6428. 15% cap tính trên TỔNG chi phí (không phải doanh thu). Vượt cap → phần vượt không được khấu trừ thuế TNDN.",
  },
  {
    id: "09-uy-nhiem-chi-vn",
    filename: "09-uy-nhiem-chi-vn.png",
    title: "UNC — KaVo balance 828M (payment matching)",
    description:
      "UNC 30k EUR trả tiếp cho KaVo — apply cho HĐ KAV-2026-0812 (payment_matching FIFO).",
    voucherType: "bank_transfer",
    voucherTypeLabel: "Uỷ nhiệm chi (thanh toán 70% balance)",
    amount: 828000000,
    currency: "VND",
    tt200Reference: "TT200 điều 24, điều 25 (TK 331, TK 112)",
    step1Options: opts("bank_transfer", ["payment", "receipt", "invoice_vat_in"]),
    step1Explanation:
      "Giống mẫu 05 nhưng đợt 2 — khi HĐ NCC đã book vào sim_ap_ledger, payment này áp vào để giảm 331.",
    step2Fields: [
      {
        field: "beneficiary_mst",
        label: "MST KaVo",
        value: "DE812526315",
        hint: "Lookup sim_suppliers → NCC-DE-001. Payment matching sẽ tìm open payables của NCC này.",
      },
      {
        field: "amount",
        label: "Số tiền chi",
        value: "828,000,000 VNĐ (30k EUR + phí SWIFT)",
        hint: "Payment_matching FIFO oldest — nếu KaVo có nhiều HĐ chưa trả, áp cho HĐ cũ nhất trước.",
      },
      {
        field: "reference",
        label: "Nội dung TT",
        value: "KAV-2026-0812 balance 70%",
        hint: "Ghi rõ HĐ để manual verify với payment_matching auto-detect.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "331",
        credit: "1122",
        amount: 826500000,
        description: "Trả NCC KaVo 30k EUR (TGNH ngoại tệ)",
      },
      {
        debit: "6425",
        credit: "1121",
        amount: 1500000,
        description: "Phí chuyển tiền quốc tế + SWIFT",
      },
    ],
    step3QuizQuestion: "Payment_matching apply 828M vào HĐ KaVo — thứ tự nào đúng?",
    step3QuizOptions: [
      { text: "FIFO — apply cho HĐ có due_date sớm nhất (cũ nhất) trước", correct: true },
      { text: "LIFO — apply cho HĐ mới nhất trước" },
      { text: "Amount-based — apply cho HĐ nhỏ nhất trước" },
      { text: "Manual — user chọn HĐ nào apply" },
    ],
    step3Explanation:
      "FIFO oldest first là quy tắc kế toán chuẩn — trả nợ cũ trước để tránh overdue penalty. AI KHÔNG auto-detect chiết khấu ở AP side (khác AR) vì chiết khấu NCC thường negotiate trực tiếp, không suy diễn được từ shortfall.",
    riskFlags: [],
    keyLesson:
      "Payment matching AP đơn giản hơn AR — không có discount auto-detect. Nếu NCC cho chiết khấu, kế toán viên phải nhập manual vào extract.discount field.",
  },
  {
    id: "10-phieu-chi-tien-mat",
    filename: "10-phieu-chi-tien-mat.png",
    title: "Phiếu chi TM 35M — vi phạm TT78",
    description:
      "Chi TM 35M > 30M threshold → không được khấu trừ VAT đầu vào (TT78 điều 6).",
    voucherType: "payment",
    voucherTypeLabel: "Phiếu chi tiền mặt (vi phạm TT78)",
    amount: 35000000,
    currency: "VND",
    tt200Reference: "TT200 điều 20, TT78/2014 điều 6 khoản 1 điểm b",
    step1Options: opts("payment", ["bank_transfer", "receipt", "invoice_vat_in"]),
    step1Explanation:
      "Payment ≠ bank_transfer: phiếu chi TM tại quầy (payment_method=TM), UNC qua NH (bank_transfer). LLM phân biệt qua payment_method field + có/không thông tin NH.",
    step2Fields: [
      {
        field: "payment_method",
        label: "Hình thức TT",
        value: "TM (tiền mặt)",
        hint: "Trigger check TT78 — nếu TM + total > 30M → fire flag.",
      },
      {
        field: "amount",
        label: "Số tiền chi",
        value: "35,000,000 VNĐ",
        hint: "> 30M threshold (config ExpenseRisk.CashPaymentVatDeductibleThreshold).",
      },
      {
        field: "beneficiary_mst",
        label: "MST NCC",
        value: "0201777002",
        hint: "NCC-VN-002 — lookup sim_suppliers, check first_transaction, aging.",
      },
      {
        field: "invoice_ref",
        label: "HĐ tham chiếu",
        value: "00007823",
        hint: "Trả cho HĐ đầu vào cụ thể — payment_matching sẽ apply vào HĐ này.",
      },
    ],
    step3JournalEntries: [
      {
        debit: "1561",
        credit: "1111",
        amount: 35000000,
        description: "Trả TM NCC (KHÔNG có TK 1331 vì vi phạm TT78)",
      },
    ],
    step3QuizQuestion: "Nếu chuyển sang UNC (thay vì TM), bút toán khác ra sao?",
    step3QuizOptions: [
      { text: "Nợ 1561 31.5M + Nợ 1331 3.5M / Có 1121 35M — được khấu trừ VAT 3.5M", correct: true },
      { text: "Y hệt như TM — VAT phụ thuộc HĐ, không phụ thuộc hình thức TT" },
      { text: "Chỉ đổi TK Có từ 1111 thành 1121, không có VAT" },
      { text: "Không có bút toán khác biệt" },
    ],
    step3Explanation:
      "TT78 điều 6: HĐ đầu vào > 20M (thực tế 30M) phải có chứng từ TT qua NH mới được khấu trừ VAT. Chi TM → mất khoản khấu trừ 3.5M — thiệt hại thực tế. Khuyến nghị: luôn dùng UNC/CK cho HĐ lớn.",
    riskFlags: ["expense_over_30m_cash_payment"],
    keyLesson:
      "Đây là lỗi kế toán rất phổ biến — chi TM cho tiện nhưng mất khấu trừ VAT. Hệ thống fire flag ĐỎ để cảnh báo trước khi accept. Nếu vẫn phải chi TM (VD NCC không có TK NH), cần document lý do.",
  },
];

export function getArtifact(id: string): Artifact | undefined {
  return ARTIFACTS.find((a) => a.id === id);
}
