# CIPP status adapter authentication research

Researched 2026-09-23. This is a design note, not deployed-instance verification.
No CIPP settings or application registrations were changed. Source inspection
uses CIPP-API commit `c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600`.

## Supported minimum

The documented external REST flow uses an API client's application ID and secret,
Entra's tenant-specific token endpoint, the client's configured API scope, and
`grant_type=client_credentials`. The resulting bearer token authenticates API
requests. Copy the instance's displayed API URL and scope rather than deriving
them from the companion's saved browser origin. The documentation's illustrative
scope is `api://{application-id}/.default`.
([Setup and authentication](https://docs.cipp.app/api-documentation/setup-and-authentication))

An administrator creates or imports and enables the API client in CIPP's
integration page, assigns custom roles and optional IP restrictions, and saves
the configuration to Azure. The page supplies the API URL, token URL and tenant
ID. These are instance configuration requirements, not evidence the deployed
instance has already enabled external API access.
([CIPP API integration](https://docs.cipp.app/user-documentation/cipp/integrations/cipp-api))

`ListTenantOnboarding` requires `Tenant.Administration.Read`; its implementation
reads the complete `TenantOnboarding` table and parses its steps, relationship and
logs. It does not itself implement a relationship-ID or tenant query filter.
Therefore matching one relationship locally must not be presented as a
server-enforced restriction on what the credential can read.
([Endpoint source](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/Tenant/Administration/Tenant/Invoke-ListTenantOnboarding.ps1))

For least privilege, propose a dedicated client/custom role with the required
read category, no write categories, and endpoint restrictions for other
unneeded endpoints. The built-in `readonly` role is broader. CIPP documents
permissions by category/pattern and endpoint blocking; granting a category is
not an exact one-endpoint allowlist. Validate the effective role against the
deployed version, including negative tests for mutation endpoints.
([Role documentation](https://docs.cipp.app/setup/setting-up-cipp/roles),
[access enforcement](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPCore/Public/Authentication/Test-CIPPAccess.ps1))

Design implication: a downloadable desktop companion should not contain a
shared embedded client secret. A REST implementation needs an explicit
credential ownership/storage decision, such as an operator-provisioned local
credential or an authenticated service that holds the secret. Neither exists
merely because the companion stores a trusted CIPP origin. This is an
architectural recommendation, not a CIPP setup requirement.

## Secretless interactive alternative: conditional

Current official documentation also supports public-client authorization-code
PKCE and loopback callbacks for **MCP**, on the latest CIPP infrastructure. It
uses an MCP-enabled API client plus a separate shared `CIPP-MCP` resource app;
the client supplies role/IP restrictions. Therefore “CIPP supports only client
secrets” would be inaccurate. This documentation does not establish a general
native REST OAuth contract for `GET /api/ListTenantOnboarding`.
([MCP setup and authentication](https://docs.cipp.app/user-documentation/cipp/integrations/cipp-api#cipp-mcp))

Source evidence makes MCP worth evaluating: the catalog projects GET/POST
operations with `.Read` permissions from the runtime OpenAPI document;
`ExecTool` dispatches selected tools through normal API authorization. MCP itself
requires `CIPP.Core.Read`, in addition to the selected endpoint's permission.
This suggests onboarding status could be called through MCP if present in the
deployed catalog, but that exact live discovery/authentication path was not
tested. Do not assume an MCP audience token is accepted directly by REST.
([Catalog builder](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPCore/Public/MCP/Get-CippMcpToolCatalog.ps1),
[tool dispatch](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPCore/Public/MCP/Get-CippMcpToolResult.ps1),
[MCP endpoint](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/CIPP/MCP/Invoke-ExecMcp.ps1))

## Integration boundary

The optional next component is a read-only status adapter, separate from the
customer's Microsoft approval session. First verify the actual deployed API
origin, version, authorized authentication flow and response contract. Then
read status and match the relationship ID, reporting unavailable/unverified
when authentication or correlation fails. The existing companion does not
call this API, and local queue state is not CIPP job status.
([Current workspace contract](../OPERATOR-WORKSPACE.md))
