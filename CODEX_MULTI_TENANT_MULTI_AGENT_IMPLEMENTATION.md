# CODEX IMPLEMENTATION SPEC --- Multi-Tenant Multi-Agent AI Platform

## 1. Objective

Refactor and implement the existing `agent_claude_sdk` project as a
**Multi-Tenant Multi-Agent AI Platform**.

The implementation must follow this hierarchy:

``` text
SYSTEM
└── SUPER_ADMIN (exactly one per system)
    ├── TENANT A
    │   ├── ADMIN(s)
    │   ├── USER(s)
    │   └── AGENT(s)
    ├── TENANT B
    │   ├── ADMIN(s)
    │   ├── USER(s)
    │   └── AGENT(s)
    └── TENANT N
```

### Core rules

1.  There is exactly **one `SUPER_ADMIN` account** in the system.
2.  The system supports **multiple Tenants**.
3.  A **Tenant is not a login role**. It is the
    organization/security/data boundary.
4.  Login roles are:
    -   `SUPER_ADMIN`
    -   `ADMIN`
    -   `USER`
5.  `SUPER_ADMIN` manages the entire platform and all tenants.
6.  `ADMIN` belongs to a tenant and manages agents, users, knowledge,
    skills, tools, and workflows within that tenant, subject to platform
    policy.
7.  `USER` belongs to a tenant and only uses agents assigned to that
    user.
8.  Tenant data must be strictly isolated using `tenant_id`.
9.  Do not trust a `tenant_id` supplied by the browser/client for
    tenant-scoped authorization.
10. Resolve tenant context from authenticated identity/JWT/server-side
    authorization context.
11. Do not hard-code individual agents in Python using `if/elif`
    routing.
12. Agents must be database-driven and dynamically composed from model,
    prompt, skills, tools, knowledge, memory, workflow, and permissions.
13. A new agent must be creatable from the browser without changing
    source code or redeploying the application.

------------------------------------------------------------------------

# 2. Terminology

## SUPER_ADMIN

Highest system role.

Responsibilities:

-   Platform management
-   Tenant management
-   Admin management
-   Global agent/skill/tool/model/workflow libraries
-   Security and platform policies
-   Quotas
-   Billing/usage
-   System audit
-   System settings

Constraint:

``` text
Exactly 1 active SUPER_ADMIN account per system.
```

Enforce this at both application and database level where practical.

## TENANT

A tenant represents one customer/company/organization.

Tenant is an entity and isolation boundary, **not a user role**.

Examples:

``` text
Tenant ABC
Tenant XYZ
Tenant DEF
```

Tenant-scoped resources include:

-   admins
-   users
-   agents
-   skills
-   knowledge bases
-   workflows
-   conversations
-   memories
-   executions
-   documents
-   business data
-   usage
-   audit records

## ADMIN

Tenant administrator.

An ADMIN:

-   must belong to a tenant;
-   cannot manage another tenant;
-   manages agents and tenant users;
-   can create/configure tenant agents within policy/quota;
-   can manage tenant knowledge/workflows/skills;
-   can use only tools/models allowed by platform policy.

## USER

End user.

A USER:

-   must belong to a tenant;
-   can see only assigned agents;
-   can chat with/execute assigned agents;
-   can access only their permitted conversations/resources;
-   cannot access Agent Builder or administrative functions.

------------------------------------------------------------------------

# 3. Authorization Model

Use RBAC plus tenant isolation.

``` text
SUPER_ADMIN
    │
    ├── Tenant A
    │      └── ADMIN
    │            ├── USER
    │            └── AGENT
    │
    └── Tenant B
           └── ADMIN
                 ├── USER
                 └── AGENT
```

Required login roles:

``` text
SUPER_ADMIN
ADMIN
USER
```

Do not create a `TENANT` login role.

A tenant should be represented by a `tenants` record and referenced by
`tenant_id`.

------------------------------------------------------------------------

# 4. Permission Matrix

  Capability               SUPER_ADMIN   ADMIN                           USER
  ------------------------ ------------- ------------------------------- -------------------------
  Platform settings        Full          No                              No
  Create tenant            Yes           No                              No
  Edit/disable tenant      Yes           No                              No
  Delete tenant            Yes           No                              No
  Manage tenant quota      Yes           No                              No
  Create ADMIN             Yes           No                              No
  Assign ADMIN to tenant   Yes           No                              No
  Manage users             All tenants   Own tenant                      No
  Create agent             Yes           Own tenant                      No
  Edit agent               Yes           Own tenant                      No
  Delete agent             Yes           Own tenant, policy-controlled   No
  Publish agent            Yes           Own tenant, policy-controlled   No
  Assign agent to user     Yes           Own tenant                      No
  Execute agent            Yes           Yes                             Assigned only
  Platform skills          Full          Read/use/clone if allowed       No
  Tenant skills            Full          Own tenant                      No
  Platform tools           Full          Use allowed tools               Via assigned agent only
  Tenant knowledge         Full          Own tenant                      Via assigned agent only
  Workflow management      Full          Own tenant                      No
  Model management         Full          Use allowed models              No
  System audit             Full          No                              No
  Tenant audit             Full          Own tenant                      Own activity only
  Billing/usage            Full          Own tenant view                 No

------------------------------------------------------------------------

# 5. Tenant Isolation --- Mandatory

Every tenant-owned record must have a `tenant_id`, unless the resource
is explicitly platform/global scoped.

Examples:

``` text
users
agents
skills
knowledge_bases
workflows
conversations
memories
executions
documents
usage_records
audit_logs
```

For shared/global resources, use an explicit scope such as:

``` text
scope = PLATFORM
tenant_id = NULL
```

Tenant resource:

``` text
scope = TENANT
tenant_id = <tenant uuid>
```

All tenant-scoped queries must enforce tenant filtering.

Example:

``` sql
SELECT *
FROM agents
WHERE tenant_id = @CurrentTenantId;
```

Never authorize using:

``` http
GET /api/agents?tenant_id=another-tenant
```

The backend must derive tenant context from authenticated identity.

------------------------------------------------------------------------

# 6. JWT / Authentication Context

Expected claims for ADMIN:

``` json
{
  "sub": "user-id",
  "role": "ADMIN",
  "tenant_id": "tenant-id"
}
```

Expected claims for USER:

``` json
{
  "sub": "user-id",
  "role": "USER",
  "tenant_id": "tenant-id"
}
```

Expected SUPER_ADMIN:

``` json
{
  "sub": "super-admin-id",
  "role": "SUPER_ADMIN"
}
```

`SUPER_ADMIN` may have no tenant.

Create a reusable server-side tenant context abstraction.

Example concept:

``` text
ICurrentUser
ICurrentTenant
TenantContext
AuthorizationService
```

Do not duplicate JWT parsing throughout endpoints.

------------------------------------------------------------------------

# 7. Database Requirements

Review existing migrations/schema first. Reuse existing tables where
sensible and create migrations rather than destructive ad-hoc changes.

Minimum logical schema:

## tenants

``` text
id
name
code
status
max_users
max_agents
max_storage
max_tokens
max_api_calls
monthly_budget
created_at
updated_at
```

## users

``` text
id
tenant_id nullable for SUPER_ADMIN
email
password_hash
display_name
status
created_at
updated_at
```

## roles

Required role values:

``` text
SUPER_ADMIN
ADMIN
USER
```

Remove/deprecate old OWNER semantics if they exist.

## user_roles

``` text
user_id
role_id
```

## agents

``` text
id
tenant_id nullable for platform agents
scope
name
description
status
model_id
system_prompt
version
created_by
created_at
updated_at
```

## agent_assignments

Assign agents to tenants/users as required.

Suggested fields:

``` text
id
tenant_id
agent_id
user_id nullable
assigned_by
status
created_at
```

## agent_skills

``` text
agent_id
skill_id
```

## agent_tools

``` text
agent_id
tool_id
permissions/config
```

## agent_knowledge

``` text
agent_id
knowledge_base_id
```

## agent_workflows

``` text
agent_id
workflow_id
```

Ensure foreign keys, indexes, uniqueness constraints, and tenant-aware
indexes.

------------------------------------------------------------------------

# 8. SUPER_ADMIN Functional Requirements

Create/complete a SUPER_ADMIN portal with:

## Dashboard

Display:

-   tenant count
-   admin count
-   user count
-   agent count
-   skill count
-   tool count
-   workflow count
-   executions
-   token/API usage
-   AI cost
-   storage
-   errors
-   security alerts
-   system health

## Tenant Management

Support:

-   list/search tenants
-   create tenant
-   edit tenant
-   enable/disable
-   lock/unlock
-   delete where safe
-   view quota
-   update quota
-   view usage
-   view audit/activity

## ADMIN Management

Support:

-   create ADMIN
-   assign ADMIN to tenant
-   edit ADMIN
-   enable/disable
-   reset password
-   remove/reassign tenant
-   view activity

## Platform Agent Management

Support:

-   create
-   edit
-   clone
-   test
-   publish
-   enable/disable
-   version
-   rollback
-   assign to tenant
-   usage/logs

## Platform Skill Library

Support:

-   upload `SKILL.md`
-   AI generate
-   edit
-   test
-   version
-   publish
-   disable
-   clone
-   assign to agents

## Platform Tool Library

Support:

-   create
-   upload
-   AI-generated skeleton
-   input/output schema
-   permissions
-   risk level
-   testing
-   versioning
-   publish/disable

Dangerous tools require approval/policy enforcement.

## Model Management

Support provider/model records and configuration:

-   OpenAI
-   Anthropic
-   Google
-   Ollama/local
-   extensible providers

Configuration:

-   enabled
-   default
-   temperature
-   token limit
-   timeout
-   cost metadata
-   usage

## Knowledge / RAG

Support:

-   PDF
-   Word
-   Excel
-   text/document uploads
-   knowledge bases
-   index/re-index
-   chunking
-   embeddings
-   search
-   delete
-   assignment to agents/tenants

## Workflow Management

Support:

-   create/edit
-   test
-   publish
-   version
-   rollback
-   enable/disable

Workflow nodes should support at minimum:

-   Agent
-   Tool
-   Condition
-   Router
-   Retry
-   Timeout
-   Approval
-   Human Review
-   Parallel
-   Sequential

## Audit / Security / Usage

Provide platform-wide:

-   audit
-   security events
-   agent executions
-   tool executions
-   workflow executions
-   token usage
-   API usage
-   storage
-   AI cost

------------------------------------------------------------------------

# 9. ADMIN Functional Requirements

ADMIN is tenant-scoped.

## Dashboard

Display only current tenant:

-   users
-   agents
-   workflows
-   knowledge
-   executions
-   token usage
-   tool usage
-   storage
-   quota
-   errors

## User Management

ADMIN can:

-   create USER
-   edit USER
-   enable/disable USER
-   delete USER where safe
-   reset password
-   assign/revoke agent

ADMIN cannot create another tenant or SUPER_ADMIN.

## Agent Management

ADMIN can:

-   create tenant agent
-   edit
-   clone
-   test
-   publish if policy allows
-   enable/disable
-   version
-   rollback
-   assign/revoke to USER

## Skills

ADMIN can:

-   browse platform skills
-   use allowed platform skills
-   clone platform skills if policy allows
-   create/upload tenant skills
-   AI-generate tenant skills
-   edit/test/version tenant skills
-   attach skills to agents

ADMIN cannot modify the original global Platform Skill.

## Tools

ADMIN can:

-   browse allowed platform tools
-   configure permitted parameters
-   test allowed tools
-   attach allowed tools to agents

Must enforce platform risk/policy restrictions.

## Knowledge

ADMIN can:

-   create tenant knowledge bases
-   upload documents
-   index/re-index
-   search/delete
-   attach knowledge to agents

## Workflow

ADMIN can:

-   create tenant workflows
-   edit/test
-   publish
-   version/rollback
-   enable/disable

------------------------------------------------------------------------

# 10. USER Functional Requirements

USER should have a minimal interface.

Required screens/functions:

-   Login
-   My Agents
-   Agent Chat / Execute
-   My Conversations
-   View execution result
-   Upload input/document only when the assigned agent allows it
-   Logout/profile

USER must not see:

-   Tenant Management
-   User Management
-   Agent Builder
-   Skill Management
-   Tool Management
-   Model Management
-   System Settings
-   Platform Audit

------------------------------------------------------------------------

# 11. Agent Builder

Agent Builder is available to `SUPER_ADMIN` and `ADMIN` subject to
scope/policy.

Agent definition must support:

``` text
Identity
├── Name
├── Description
└── Category

Model
├── Provider
├── Model
├── Temperature
├── Token Limit
└── Timeout

System Prompt

Skills
├── Platform Library
└── Tenant Library

Tools
├── Allowed Platform Tools
└── Tenant Configuration

Knowledge
├── Platform Knowledge if allowed
└── Tenant Knowledge

Memory
├── Conversation
├── User
├── Agent
└── Tenant

Workflow

Permissions

Version
```

Agent creation/update must be persisted in the database.

------------------------------------------------------------------------

# 12. Dynamic Agent Runtime

Do not implement individual agents as hard-coded routing branches.

Forbidden design:

``` python
if agent_name == "accounting":
    ...
elif agent_name == "invoice":
    ...
elif agent_name == "sales":
    ...
```

Required execution flow:

``` text
Request
  ↓
Authenticate
  ↓
Resolve role + tenant
  ↓
Resolve requested/selected agent
  ↓
Check assignment
  ↓
Load Agent Definition from DB
  ↓
Load Model
  ↓
Load System Prompt
  ↓
Load Skills
  ↓
Load Tools
  ↓
Load Knowledge
  ↓
Load Memory
  ↓
Load Workflow
  ↓
Permission + quota check
  ↓
Execute
  ↓
Audit + usage record
  ↓
Response
```

The runtime must support adding a new database-defined agent without
editing Python source.

------------------------------------------------------------------------

# 13. Multi-Agent Orchestration

Support a master/router pattern where appropriate:

``` text
USER REQUEST
     ↓
MASTER / ROUTER
     ↓
TASK ANALYSIS
     ↓
DYNAMIC AGENT SELECTION
     ↓
SPECIALIST AGENT(S)
     ↓
SKILLS + TOOLS + KNOWLEDGE + MEMORY
     ↓
WORKFLOW / APPROVAL
     ↓
AUDIT
     ↓
FINAL RESPONSE
```

Do not require every request to use a master agent. Direct execution of
an explicitly selected assigned agent should also be supported.

------------------------------------------------------------------------

# 14. Audit Requirements

Record important operations including:

-   authentication
-   tenant create/update/delete
-   user/admin changes
-   agent create/update/publish/delete
-   skill changes
-   tool changes
-   knowledge changes
-   workflow changes
-   permission changes
-   agent execution
-   tool execution
-   workflow execution
-   approval decisions
-   security events

Execution audit should capture, where applicable:

``` text
tenant_id
user_id
agent_id
workflow_id
tool_id
execution_id
action
status
duration
token_usage
cost
timestamp
metadata
```

Do not store secrets or raw credentials in audit logs.

------------------------------------------------------------------------

# 15. Versioning

Support versioning for:

-   Agent
-   System Prompt
-   Skill
-   Tool
-   Workflow

At minimum:

``` text
Draft
Published
Archived
```

Provide:

-   publish
-   clone
-   rollback
-   version history

Avoid destructive overwrites of published definitions.

------------------------------------------------------------------------

# 16. Quota Enforcement

Tenant quota should be enforceable for at least:

-   users
-   agents
-   storage
-   token usage
-   API/execution usage
-   optional monthly AI cost

Check quota server-side before relevant operations.

------------------------------------------------------------------------

# 17. Frontend Navigation

## SUPER_ADMIN

``` text
Dashboard
Tenants
Admins
Users
Agents
Agent Builder
Skills
Tools
Models
Knowledge Bases
Workflows
Roles / Permissions
Usage & Billing
Audit Logs
Security
System Settings
```

## ADMIN

``` text
Dashboard
Users
Agents
Agent Builder
Skills
Tools
Knowledge Bases
Workflows
Usage
Reports
Tenant Settings
Audit / Activity
```

## USER

``` text
My Agents
Chat
My Conversations
Profile
```

Menus must be generated from authenticated role/permissions, not only
hidden visually. Backend authorization remains mandatory.

------------------------------------------------------------------------

# 18. API Security Requirements

Every privileged endpoint must enforce authorization server-side.

Examples:

``` text
/api/platform/*        → SUPER_ADMIN
/api/tenants/*         → SUPER_ADMIN
/api/admin/*           → SUPER_ADMIN or tenant ADMIN depending endpoint
/api/agents/*          → scope-aware
/api/users/*           → scope-aware
/api/chat/*            → authenticated + agent assignment
```

For ADMIN requests:

``` text
CurrentTenantId = JWT/server auth context
```

Never allow ADMIN to change target tenant by editing request JSON/query
string.

For USER agent execution verify:

``` text
user.tenant_id == agent tenant/scope authorization
AND
user has active assignment to agent
AND
agent is enabled/published
```

------------------------------------------------------------------------

# 19. Migration / Compatibility Requirements

Before changing code:

1.  Inspect the complete existing repository.
2.  Inspect current database migrations.
3.  Identify existing roles (`super_admin`, `admin`, `owner`, `user`,
    etc.).
4.  Identify current tenant/organization tables and references.
5.  Identify existing agent assignment model.
6.  Identify current authentication/JWT claims.
7.  Identify frontend role/menu logic.
8.  Identify existing APIs that depend on `owner` or `organization`.

Then produce a migration strategy.

Expected semantic change:

``` text
OLD:
SUPER_ADMIN
OWNER
ADMIN
USER
Organization

NEW:
SUPER_ADMIN
ADMIN
USER
Tenant
```

`OWNER` must be removed/deprecated/migrated according to existing data.

If the existing project uses `organization_id`, migrate or map it
carefully to `tenant_id`.

Do not silently destroy existing data.

------------------------------------------------------------------------

# 20. Implementation Order

Implement in this order:

## Phase 1 --- Repository analysis

-   map architecture
-   map database
-   map auth/RBAC
-   map existing tenant/org implementation
-   map agent runtime
-   map frontend routes

Do not modify files during initial analysis.

## Phase 2 --- Database/RBAC

-   tenants
-   roles
-   user tenant association
-   single SUPER_ADMIN constraint
-   agent scope
-   assignments
-   migrations/indexes/FKs

## Phase 3 --- Authentication & tenant context

-   JWT claims
-   current user
-   current tenant
-   policies
-   tenant isolation

## Phase 4 --- SUPER_ADMIN APIs

-   tenants
-   admins
-   users
-   platform agents
-   assignments

## Phase 5 --- ADMIN APIs

-   tenant users
-   tenant agents
-   assignment to users
-   tenant resources

## Phase 6 --- Dynamic agent runtime

-   database-defined agents
-   dynamic skills/tools/knowledge/workflows
-   permission checks
-   execution audit

## Phase 7 --- Frontend

-   SUPER_ADMIN screens
-   ADMIN screens
-   USER screens
-   role-based navigation
-   Agent Builder

## Phase 8 --- Tests

-   RBAC
-   cross-tenant access
-   assignments
-   dynamic agent execution
-   quotas
-   audit

## Phase 9 --- Documentation

Update:

-   README
-   architecture docs
-   API docs
-   migration docs
-   deployment instructions

------------------------------------------------------------------------

# 21. Mandatory Tests

Implement automated tests for these scenarios.

### SUPER_ADMIN

-   can see all tenants
-   can create tenant
-   can create ADMIN and assign tenant
-   can assign platform agent to tenant

### ADMIN

-   can access own tenant
-   cannot access another tenant
-   can create USER in own tenant
-   cannot create USER in another tenant
-   can create/configure own tenant agent
-   can assign agent to own tenant USER
-   cannot assign agent to another tenant USER

### USER

-   sees only assigned agents
-   can execute assigned enabled agent
-   cannot execute unassigned agent
-   cannot access Agent Builder
-   cannot access admin APIs
-   cannot read another user's protected conversation

### Tenant isolation

Explicitly test Tenant A attempting to access Tenant B using:

-   URL IDs
-   query parameters
-   request body tenant IDs
-   direct resource IDs

All must be rejected.

### SUPER_ADMIN uniqueness

-   creating a second active SUPER_ADMIN must fail.

------------------------------------------------------------------------

# 22. Definition of Done

Implementation is complete only when:

-   [ ] Exactly one SUPER_ADMIN is enforced.
-   [ ] Multiple tenants can be created.
-   [ ] Tenant is an entity/boundary, not a login role.
-   [ ] ADMIN and USER belong to tenants.
-   [ ] Cross-tenant access is blocked server-side.
-   [ ] SUPER_ADMIN can create/manage tenants.
-   [ ] SUPER_ADMIN can create ADMIN and assign tenant.
-   [ ] ADMIN can create/manage tenant USERs.
-   [ ] SUPER_ADMIN can create platform agents and assign them to
    tenants.
-   [ ] ADMIN can create/manage permitted tenant agents.
-   [ ] ADMIN can assign agents to tenant USERs.
-   [ ] USER sees and executes only assigned agents.
-   [ ] Agent Builder persists definitions to DB.
-   [ ] Agent runtime loads definitions dynamically.
-   [ ] Adding a new agent does not require Python source modification.
-   [ ] Skills/tools/knowledge/workflows are dynamically attachable.
-   [ ] Execution permissions are enforced.
-   [ ] Audit logging exists.
-   [ ] Versioning exists for core definitions.
-   [ ] Quotas are enforced server-side.
-   [ ] Frontend menus/routes match roles.
-   [ ] Automated RBAC and tenant-isolation tests pass.
-   [ ] Docker Compose stack builds and starts successfully.
-   [ ] Existing useful functionality is preserved.

------------------------------------------------------------------------

# 23. Codex Working Instructions

When implementing this specification:

1.  **First analyze the entire repository. Do not immediately rewrite
    the project.**
2.  Reuse existing architecture and conventions where reasonable.
3.  Prefer incremental migrations over replacing working components.
4.  Do not delete existing accounting/invoice functionality unless it
    conflicts with the architecture; adapt it to tenant scope.
5.  Do not hard-code tenant IDs, user IDs, agent IDs, passwords, API
    keys, or secrets.
6.  Do not weaken authentication to make tests pass.
7.  Every tenant-sensitive backend query must be reviewed for
    cross-tenant leakage.
8.  Keep API Gateway, Agent Runtime, Web, PostgreSQL, Redis, Qdrant, and
    existing infrastructure working.
9.  Add migrations for schema changes.
10. Build and run tests after each major phase.
11. Run the full Docker Compose stack before declaring completion.
12. Report all changed files and migrations.
13. Report unresolved risks/TODOs explicitly.
14. Do not claim a feature works unless it has been implemented and
    tested.
15. Preserve backward compatibility where practical.

------------------------------------------------------------------------

# 24. Expected Final Architecture

``` text
                         SYSTEM
                           │
                    SUPER_ADMIN
                    (exactly one)
                           │
          ┌────────────────┼────────────────┐
          │                │                │
       TENANT A         TENANT B         TENANT C
          │                │                │
       ADMIN(s)         ADMIN(s)         ADMIN(s)
          │                │                │
     ┌────┴────┐      ┌────┴────┐      ┌────┴────┐
     │         │      │         │      │         │
   USERS     AGENTS  USERS    AGENTS  USERS     AGENTS
               │                │                │
          ┌────┼────┐      ┌────┼────┐      ┌────┼────┐
          │    │    │      │    │    │      │    │    │
        Skills Tools KB   Skills Tools KB   Skills Tools KB
          │    │    │      │    │    │      │    │    │
          └────┼────┘      └────┼────┘      └────┼────┘
               │                │                │
            Workflow         Workflow         Workflow
               │                │                │
            Execution        Execution        Execution
               │                │                │
               └────────────────┼────────────────┘
                                │
                              AUDIT
```

## Final role semantics

``` text
SUPER_ADMIN
= highest system role
= exactly one account
= manages entire platform and all tenants

TENANT
= organization/customer entity
= data and security isolation boundary
= NOT a login role

ADMIN
= tenant administrator
= manages agents and users/resources inside assigned tenant

USER
= end user
= only uses assigned agents
```
