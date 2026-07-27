---
name: ma-data-room-management
description: |
  Setup và vận hành virtual data room (VDR) cho deal M&A —
  document taxonomy, access control, Q&A tracker, audit log.
  Áp dụng khi khách hỏi "data room", "VDR", "quản lý tài liệu deal",
  "Q&A tracker", "iDeals Intralinks".
keywords:
  - data room
  - VDR
  - virtual data room
  - document management
  - Q&A tracker
  - Intralinks
  - iDeals
  - Ansarada
always_on: false
---

# Virtual Data Room Management

Bạn là **VDR Administrator BSN**. Nhiệm vụ: build data room mà **bidder finds what they need fast + seller doesn't leak sensitive info**.

## VDR selection

### VDR provider comparison (2026)

| Provider | Cost (SME deal) | Best for | Pros | Cons |
|---|---|---|---|---|
| Intralinks | $15-40k | Large / cross-border | Bank-grade, comprehensive | Expensive, complex |
| Datasite (Merrill) | $12-30k | IB standard | Full features, IB familiar | Expensive |
| Ansarada | $8-20k | Mid-market | Purpose-built for M&A, AI Q&A | UI dated |
| Firmex | $6-15k | SME + mid | Good balance | Fewer AI features |
| DealRoom | $4-10k | Small deals | Modern UI, fast setup | Less enterprise-ready |
| iDeals | $5-15k | VN/APAC | Good VN pricing, decent features | Lesser known |
| Sharefile | $3-8k | Very small | Cheap | Not M&A-purpose-built |

**Do NOT use:** Google Drive, Dropbox, WeTransfer for live deal (no audit log, no watermark, no access control granularity).

## Folder taxonomy — standard 12 categories

Tailored per industry, but this is universal skeleton:

```
00. Deal Administration
    /00.1 NDA
    /00.2 Process letter
    /00.3 Presentation schedule

01. Corporate
    /01.1 Charter (Điều lệ) + amendments
    /01.2 Business registration certificate (ĐKKD)
    /01.3 Investment certificate (nếu FDI)
    /01.4 Board / shareholder resolutions (5 năm)
    /01.5 Ownership structure diagram
    /01.6 Subsidiaries + affiliates
    /01.7 Corporate history + milestones

02. Financial
    /02.1 Audited financial statements (5 năm)
    /02.2 Management accounts monthly (24 tháng)
    /02.3 Consolidation workings
    /02.4 Chart of accounts
    /02.5 Tax filings + assessments (5 năm)
    /02.6 Bank statements (12 tháng)
    /02.7 Debt agreements
    /02.8 Guarantees given
    /02.9 Aged AR/AP schedules
    /02.10 Budget vs actual analysis

03. Commercial
    /03.1 Top 20 customer contracts
    /03.2 Top 20 supplier contracts
    /03.3 Distribution agreements
    /03.4 Pricing history + policy
    /03.5 Sales pipeline
    /03.6 Marketing plans
    /03.7 Customer satisfaction data / NPS

04. Legal
    /04.1 Material contract summary
    /04.2 Litigation register (active + threatened)
    /04.3 Regulatory investigations
    /04.4 Licenses + permits (all)
    /04.5 IP registry (trademark, patent, copyright)
    /04.6 Domain names
    /04.7 Consent list (change of control)

05. HR
    /05.1 Org chart + head count
    /05.2 Employment contracts (top 20)
    /05.3 Compensation structure
    /05.4 Benefits + bonus schemes
    /05.5 Union agreement / collective bargaining
    /05.6 Social insurance filings
    /05.7 Disputes / grievances

06. Operations
    /06.1 Real estate: leases + titles
    /06.2 Manufacturing / production
    /06.3 Supply chain map
    /06.4 Inventory management
    /06.5 Quality management
    /06.6 Key equipment schedule
    /06.7 Business continuity plan

07. Technology
    /07.1 IT architecture diagram
    /07.2 Software licenses
    /07.3 Cybersecurity assessment
    /07.4 Data privacy compliance (NĐ 13/2023)
    /07.5 IT contracts
    /07.6 Recent IT incidents

08. Regulatory
    /08.1 Sector licenses (ngành nghề có điều kiện)
    /08.2 Environmental permits
    /08.3 Fire safety certificate
    /08.4 Food safety (nếu F&B)
    /08.5 Import/export permits
    /08.6 Regulator correspondence

09. Insurance
    /09.1 Policy schedule
    /09.2 Recent claims history
    /09.3 Coverage gap analysis

10. Related Party
    /10.1 Related party register (per NĐ 132/2020)
    /10.2 Related party transactions (5 năm)
    /10.3 Transfer pricing documentation

11. Environmental / ESG
    /11.1 Environmental impact assessment
    /11.2 Waste management
    /11.3 Emissions reporting
    /11.4 ESG policies

12. Ancillary
    /12.1 Corporate presentations
    /12.2 Third-party market reports
    /12.3 Industry association memberships
```

## Naming convention

**Rule: `[Category]_[Description]_[Language]_[YYYY-MM-DD].pdf`**

Examples:
- `02.1_AuditedFS_VN_2024-12-31.pdf`
- `04.4_LicenseFoodSafety_2025-06-15.pdf`
- `03.1_Contract_TopCustomer_ABC_2024-03-20.pdf`

For multi-page docs with amendments:
- `02.7_LoanAgreement_Vietcombank_Master_2022-05-10.pdf`
- `02.7_LoanAgreement_Vietcombank_Amend1_2023-11-08.pdf`

## Access levels — Tiered

### Tier A: Public (pre-NDA)
- Teaser only
- No sensitive info

### Tier B: NDA-signed (basic access)
- 60-80% of folders visible
- Customer names anonymized
- Financial summary only, not full detail

### Tier C: Shortlisted / IOI submitted (full access)
- 100% except most sensitive
- Full financial detail
- Contract summaries (not full contracts for top customers — those redacted names)

### Tier D: Preferred bidder (post-LOI, confirmatory DD)
- 100% including full customer contracts
- Live Q&A access
- Site visit permission

### Tier E: Advisor / auditor (specialized)
- Only their scope (tax advisor sees tax docs)
- Read-only, no download for most sensitive

## Watermarking

Every PDF displayed with watermark showing:
- Bidder company name
- User name + email
- Timestamp of view
- "CONFIDENTIAL - PROJECT [CODENAME]"

Diagonal, semi-transparent, unavoidable.

## Access & audit log

**VDR must log:**
- Who accessed which document
- When (timestamp)
- Duration of view
- Downloads (allow / block per document)
- Prints (typically blocked or logged)

**Weekly bidder activity report to seller:**

```
Bidder                Docs viewed   Total time   Focus areas
────────────────────────────────────────────────────────
PE Firm A            342           28 hours     Financial, tax
Strategic Buyer B    156           12 hours     Customer contracts
PE Firm C            89            5 hours      Low engagement
────────────────────────────────────────────────────────

Insight: Bidder C likely dropping out. Strategic B focused on customer.
```

## Q&A tracker

**Structure:**

```
Q#   Date       Bidder   Category    Question                    Answer   Answered by   Status
───────────────────────────────────────────────────────────────────────────────────────────
Q001 05 Jan     PE-A     Financial   Adjusted EBITDA 2024 detail  [ans]   CFO           Answered
Q002 06 Jan     Strat-B  Legal       Customer XYZ contract term   [ans]   Legal         Answered
Q003 07 Jan     PE-A     Operations  Site visit availability     [ans]   COO           Answered
Q004 08 Jan     PE-C     Financial   Cash flow monthly 24m       PENDING FDD           Open
Q005 08 Jan     Strat-B  HR          Retention plan top 10       PENDING HR            Open
───────────────────────────────────────────────────────────────────────────────────────────
```

**Rules:**
- Every Q&A visible to ALL bidders (fair process)
- Questions asked once — subsequent bidders get same answer
- Response SLA: 48-72 hours
- Sensitive Q's flagged for management review before response
- Weekly Q&A digest emailed to bidders

## Redaction protocol

**What to redact (before uploading):**
- Personal data (individual bank accounts, ID numbers) per NĐ 13/2023
- Third-party confidential info (customer names in some contracts)
- Attorney-client privileged
- Trade secrets (formulas, source code, algorithms)
- Sensitive employee compensation individual level
- Competitor / market intel from third-party research (license restrictions)

**How:**
- Use Adobe Acrobat Pro redact tool (not black rectangle overlay — text still searchable)
- Verify by trying to search redacted term after
- Keep clean + redacted versions separately

## Site visits — managed as extension of VDR

**Coordination:**
- Schedule via VDR
- NDA already signed
- Site visit protocol document
- Confidentiality reminder at start
- No photography without permission
- Escort at all times
- Debrief with bidder immediately after

**Site visit briefing package (uploaded to VDR):**
- Site map
- Key contacts list
- Health & safety briefing
- Product/service demo agenda
- Q&A prep for management

## VN-specific data room considerations

### Language
- **Vietnamese primary** for legal, HR, regulatory docs (they exist only in VN)
- **English translation** provided for:
  - Financial statements (dual VN + IFRS)
  - Material contracts (top 20)
  - Key management contracts
  - Litigation summary
- **Auto-translate** for the rest with disclaimer

### Legal docs must include
- Giấy chứng nhận đăng ký doanh nghiệp (business reg cert)
- Giấy chứng nhận đầu tư (if FDI, IRC)
- Điều lệ công ty (charter) + amendments
- Biên bản ĐHĐCĐ / HĐQT (shareholder + board minutes)
- Sổ đăng ký cổ đông (shareholder register)
- Giấy phép con (sector licenses)

### Tax docs (heavy scrutiny)
- Tờ khai VAT hàng tháng 5 năm
- Tờ khai CIT hàng năm 5 năm
- Biên bản thanh tra thuế 10 năm (if any)
- Related party transaction reporting (NĐ 132)
- Transfer pricing documentation

### Real estate
- Sổ đỏ (LURC) certified copy
- Land lease contracts
- Zoning approval
- Đền bù giải phóng mặt bằng documentation

## Data room lifecycle

### Setup phase (Week 1-3 of prep)
- Select VDR provider
- Set up folder taxonomy
- Bulk upload initial 500-800 docs
- Set access permissions
- Test with internal team

### Active phase (Week 8-30, marketing + DD)
- New docs added as prepared
- Q&A tracker managed daily
- Weekly bidder report
- Redactions maintained

### Confirmatory DD phase (Week 22-30)
- Deep access for preferred bidder
- Real-time responsiveness
- Physical war room may open

### Closing phase (Week 33-46)
- Final documents added (signing bundle)
- Data room "frozen" post-signing
- Handover to buyer post-close

### Post-close (Month 1-3)
- Data room download by buyer (final version)
- Retention 12-24 months for record
- Then deleted per NDA obligations

## Common mistakes

1. **Under-populated at launch** — bidder loses confidence
2. **Chaotic organization** — bidder waste time searching
3. **Sensitive docs uploaded** without redaction → seller exposed
4. **No Q&A discipline** — same question answered inconsistently
5. **Slow response** — bidder frustration, deal cools
6. **No access log review** — miss signal bidder losing interest
7. **Watermark disabled** for convenience → leak risk
8. **Full access at NDA stage** — most sensitive out too early
9. **Site visit unstructured** — bidder overwhelmed or under-impressed

## BSN VDR service tiers

**Setup only:** Configure VDR, taxonomy, initial upload. **20-40 triệu VND** one-off.

**Managed VDR (recommended):** Setup + daily Q&A + weekly reports + bidder engagement analysis. **30-50 triệu VND / tháng**.

**Full DD workstream orchestration:** Managed VDR + workstream sync + escalations. **80-120 triệu VND / tháng**.

## Chi phí total (VDR provider + BSN service)

- Small deal: $5-10k VDR + 20-40M BSN = ~150-200M VND setup + monthly
- Mid deal: $10-20k VDR + 40-80M BSN = ~350-500M VND setup + monthly
- Large deal: $20-40k VDR + 80-150M BSN = ~700M-1.2B VND setup + monthly

Cross-ref: `ma-project-management` (VDR is PMO backbone), `ma-marketing-and-materials` (VDR docs from IM), `ma-financial-due-diligence` + `ma-commercial-due-diligence` (DD teams use VDR).
