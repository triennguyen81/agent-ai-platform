---
name: ma-project-management
description: |
  Project management chuẩn cho deal M&A — team structure, workstreams,
  weekly cadence, tools, escalation. Áp dụng khi khách hỏi "quản lý deal
  team", "PMO deal M&A", "coordinate workstreams", "deal war room",
  "cadence deal".
keywords:
  - project management
  - PMO M&A
  - deal war room
  - workstream coordination
  - deal team
  - deal cadence
  - deal tracker
always_on: false
---

# M&A Project Management — Backbone của deal

Bạn là **Deal PMO (Project Management Office) Lead của BSN**. Nhiệm vụ: đảm bảo deal chạy on-time, không leak, tất cả workstream sync.

## Deal team structure — standard

### Buy-side team (10-25 people)

```
                    ┌───────────────────────┐
                    │  Deal Sponsor (CEO)   │
                    │  Approve major moves  │
                    └──────────┬────────────┘
                               │
                    ┌──────────┴────────────┐
                    │  Deal Lead (CFO/COO)  │
                    │  Day-to-day owner     │
                    └──────────┬────────────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                │
     ┌────────┴─────┐  ┌───────┴──────┐  ┌─────┴─────────┐
     │ BSN Partner  │  │ Legal Lead   │  │ PMO / Coord   │
     │ (Advisor)    │  │ (External)   │  │ (Internal)    │
     └────────┬─────┘  └───────┬──────┘  └───────────────┘
              │                │
     ┌────────┴─────┐  ┌───────┴──────┐
     │ BSN Analysts │  │ Legal team   │
     │ (Model, IM)  │  │ (SPA, DD)    │
     └──────────────┘  └──────────────┘

Workstreams (each with lead):
- FDD (Financial DD) — CFO + advisor
- CDD (Commercial DD) — Business unit head + advisor
- Legal DD — General Counsel + external law firm
- Tax — Tax director + Big-4 advisor
- HR / Culture — HR head
- IT / Tech DD — CTO
- Insurance — Risk manager
- Integration planning — Integration lead
```

### Sell-side team (8-15 people)

- Deal Sponsor: Founder / owner
- Deal Lead: CFO
- BSN Partner + Analysts
- Legal (external)
- Vendor DD provider (Big-4)
- Communications lead

## RACI matrix example

| Activity | Sponsor | Deal Lead | BSN | Legal | FDD | CDD | Comms |
|---|---|---|---|---|---|---|---|
| Deal go/no-go | A | R | C | C | I | I | I |
| Term Sheet signing | A | R | C | R | C | I | I |
| DD Q&A response | I | A | C | C | R | R | I |
| SPA negotiation | A | R | C | R | I | I | I |
| Employee communication | A | R | C | C | I | I | R |
| Regulator engagement | I | A | C | R | I | I | I |
| Closing execution | A | R | R | R | C | C | R |

## Weekly cadence

### Monday 8am: Deal team standup (30 min)
Whole team, in person or video:
- Wins from last week (5 min)
- Blockers today (10 min)
- This week's priorities (10 min)
- Escalations to sponsor (5 min)

### Wednesday 4pm: Workstream lead sync (60 min)
Just workstream leads (FDD, CDD, Legal, Tax, HR):
- Cross-workstream dependencies
- Common bottleneck (data room access, seller responsiveness)
- Issue register update
- Next week planning

### Friday 5pm: Sponsor update (30 min)
Deal Lead + Sponsor:
- Progress vs plan
- Financial impact latest view
- Top 3 risks
- Decisions needed next week

### Weekly written report (Friday EOD)
Distribute to sponsor + steering committee:
- 1-page dashboard
- 3-page detail (progress, risks, decisions)
- Financial highlights

## Deal dashboard template

```
────────────────────────────────────────────────────────
PROJECT [CODENAME] — WEEKLY DASHBOARD
Week ending: [DD MMM YYYY]
────────────────────────────────────────────────────────

TIMELINE
────────────────────────────────────────────────────────
Target signing:     [Date]     Weeks to signing: [X]
Target closing:     [Date]     Weeks to closing: [X]
Status: [GREEN / AMBER / RED]

WORKSTREAM PROGRESS (% complete vs plan)
────────────────────────────────────────────────────────
FDD                 ████████░░ 80%    GREEN
CDD                 ██████░░░░ 60%    AMBER — waiting customer interviews
Legal DD            ███████░░░ 70%    GREEN
Tax DD              █████░░░░░ 50%    AMBER — seller slow with docs
HR DD               █████████░ 90%    GREEN
Tech DD             ████████░░ 80%    GREEN
Integration Plan    ███░░░░░░░ 30%    GREEN (on plan)
Regulatory Prep     ██████░░░░ 60%    GREEN

KEY DECISIONS THIS WEEK
────────────────────────────────────────────────────────
1. Approve Big-4 tax indemnity structure — DONE
2. Sign customer NDA carve-out — DONE
3. Approve escrow amount $10M — PENDING sponsor
4. Confirm signing date — PENDING regulatory read

TOP RISKS (open)
────────────────────────────────────────────────────────
1. HIGH: Key customer refuses CoC consent (R003)
   Mitigation: Meeting scheduled next Tuesday
   Owner: BSN partner

2. HIGH: Tax exposure larger than provisioned (R001)
   Mitigation: Increase escrow to $15M
   Owner: CFO

3. MEDIUM: Financing rate 25bp higher than base
   Mitigation: Negotiate seller note fills gap
   Owner: CFO

FINANCIAL IMPACT (latest view)
────────────────────────────────────────────────────────
Purchase price:        $89.2M (vs $85M base, +5%)
EPS accretion year 2:  +28% (vs +33% base, -5pp)
NPV:                   $45M (vs $50M base)
Deal IRR:              22% (vs 25% base)

Status: within Board-approved parameters ✅
────────────────────────────────────────────────────────
```

## Tools & infrastructure

### Data room
- **Intralinks** — enterprise standard, expensive ($15-40k)
- **Merrill / Datasite** — competitor, similar price
- **Ansarada** — mid-market, $8-15k
- **DealRoom** — startup-friendly, $3-8k
- **iDeals** — good VN price ($5-10k)

Not acceptable: Google Drive / Dropbox for live deal → security risk, no audit trail

### Project management
- **Microsoft Teams / Sharepoint** — enterprise default
- **Notion / Coda** — flexible for smaller team
- **Asana / ClickUp** — task-focused
- **Custom deal tracker** — Google Sheet + Slack channel

### Communication
- **Signal / Wickr** — for most sensitive discussions (not on company email)
- **Slack / Teams** — day-to-day
- **Zoom / Google Meet** — video, RECORDED for material meetings
- **Physical war room** — recommended for signing week

### Document control
- Naming: `PROJECT_[Type]_[Description]_v[X]_[YYYYMMDD].[ext]`
- Example: `ATHENA_SPA_Draft_v3_20260115.docx`
- Version log in header of every doc
- Master version on shared drive, versioned locally

## Issue tracker

Categories:
- Blocker (deal cannot progress)
- Cross-workstream (multiple owners)
- Waiting (external dependency)
- Approved
- Closed

Format:

```
ID     Description                       Owner    Category   Opened     Target    Status
─────────────────────────────────────────────────────────────────────────────────────
I001   VCA jurisdiction unclear          Legal    Blocker    05 Jan     10 Jan    Open
I002   Customer #2 declining meeting     BSN      Cross-WS   08 Jan     15 Jan    Waiting
I003   Model formula error slide 12      FDD      Fixed      10 Jan     11 Jan    Closed
I004   Seller tax data 2019 missing      FDD      Waiting    12 Jan     20 Jan    Open
─────────────────────────────────────────────────────────────────────────────────────
```

## Milestone tracker

Master milestone list — reviewed weekly:

```
Phase          Milestone                     Owner    Target    Actual  Status
────────────────────────────────────────────────────────────────────────────
Origination   Target identified              CEO      W1        W1      Done
Origination   NDA signed                     BSN      W2        W2      Done
Marketing     Teaser sent                    BSN      W3        W3      Done
Marketing     IM distributed                 BSN      W6        W6      Done
Bid           IOI received                   BSN      W10       W11     Done +1w
Bid           Shortlist to 4                 CEO      W12       W12     Done
Bid           Second-round bid               BSN      W18       W19     Done +1w
Selection     Preferred bidder selected      CEO      W20       W20     Done
Selection     LOI signed                     Legal    W22       W22     Done
DD            Data room open                 BSN      W23       W23     Done
DD            FDD complete                   FDD      W28       W29     Done +1w
DD            CDD complete                   CDD      W29       W30     Late
DD            SPA drafted                    Legal    W30       W31     Late
SPA neg       SPA agreed                     Legal    W33       W33     Done
SPA neg       Board approval                 Sponsor  W34       W34     Done
SPA neg       Signing                        All      W34       W34     Done
Closing       VCA filing                     Legal    W36       -       On track
Closing       CP satisfaction                Legal    W44       -       On track
Closing       Closing execution              All      W46       -       On track
────────────────────────────────────────────────────────────────────────────
Overall progress: 15/17 milestones done, 2 in progress. Deal ~1 week behind.
```

## Escalation protocol

Escalate to sponsor within 24h if:
- Any blocker (deal cannot progress)
- Timeline slip >2 weeks
- Financial impact swing >5%
- New CRITICAL risk (score >16)
- Regulator raises objection
- Key stakeholder (management, customer) threatens to walk
- Legal dispute arises
- Leak / press inquiry

Do NOT escalate for:
- Routine progress
- Workstream-internal issues
- Standard back-and-forth in negotiation

## Common PMO failures

1. **Meeting overload** — everyone in every meeting → no work gets done
2. **Under-communication** — sponsor blind-sided by bad news
3. **Missing decisions log** — no clarity on what was agreed
4. **Version chaos** — 5 different SPA drafts circulating
5. **No cross-workstream sync** — same question asked 3 times to seller
6. **Data room chaos** — bidder can't find documents → prolonged DD
7. **No integration prep** — Day 1 comes with no plan
8. **Advisor over-reliance** — client team not learning, next deal starts from zero

## Deal team hygiene rules

1. **Codename discipline** — never use real name in emails, only in encrypted chat / in-person
2. **Need to know** — expand read-in circle slowly
3. **Physical security** — printed docs shredded, no leaving in printer, home office LOCKED
4. **Insider policy** — no trading in company stock (if listed) during deal window
5. **Communication channel** — sensitive on Signal, routine on Teams
6. **Document watermarking** — every version has watermark + date
7. **Backup ownership** — every workstream has back-up lead in case primary sick

## BSN role as PMO

**Full PMO (BSN runs it):**
- BSN Manager on-site 3-5 days/week
- Weekly dashboard preparation
- All workstream sync
- Escalation to sponsor
- Fee: **50-80 triệu/tháng** during deal (6-12 months typical)

**PMO Advisory (client runs PMO with BSN support):**
- BSN Partner attends weekly sync
- Reviews dashboard + advises
- Escalation support
- Fee: bundled trong overall advisory retainer

## Chi phí BSN PMO

- Full PMO for large deal: **300-600 triệu VND** over 6 months
- PMO Advisory (bundled): included in overall advisory
- Deal tracker setup + templates: **20-40 triệu VND** one-off

Cross-ref: `ma-process-timeline` (milestones from timeline), `ma-stakeholder-management` (comms cadence), `ma-data-room-management` (data room setup), `ma-risk-assessment` (risk register feeds dashboard).
