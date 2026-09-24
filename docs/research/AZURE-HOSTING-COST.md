# Azure status-service hosting cost

Researched 2026-09-24 against Microsoft and GitHub primary sources. This is a
hosting comparison, not a deployment or a verified subscription quote. No Azure
resources were accessed or changed for this note.

Follow-up evidence: the operator reported B2 / Basic / one instance. Seven-day
minute-average CPU was 6.744% (highest 82%); memory was 69.913% (highest 95%),
with about 10,079 samples per metric. Based on that memory pressure, the user
approved continuing with the separate scale-to-zero alternative. The latest
templates now implement it; the ranked investigation below records the earlier
decision process, not an instruction to share the heavily used CIPP plan.

Reported existing host: Linux container Web App, plan `cippabcmq-plan`, resource
group `CIPP-Resorces`, North Central US, subscription named Microsoft Azure
Sponsorship. SKU, instance count, spare capacity, offer ID and remaining credits
are unverified. The separate ACA/ACR/Log Analytics proposal in
[Azure deployment](../AZURE-DEPLOYMENT.md) is not confirmed deployed.

## Ranked recommendation

1. **Investigate sharing the existing paid App Service plan first.** If its
   actual SKU, instance configuration and measured spare CPU/memory permit this,
   a separate Web App adds no VM compute charge without a plan resize. This is
   a conditional recommendation, not evidence that CIPP has spare capacity.
2. **Otherwise use ACA Consumption with minimum zero, maximum one**, the existing
   container, GHCR and no saved platform logs if loss of retained audit history
   is acceptable. This is the strongest candidate for low incremental cost and
   separation from CIPP's compute; cold starts must be acceptable.
3. **F1 App Service is a trial option**, not the recommended staff production
   endpoint. Its quotas and missing availability features are material.
4. **Do not rewrite into Functions solely to save hosting cost yet.** First
   establish whether options 1 or 2 already meet the budget.

The evidence and prerequisites for this ordering follow. These are proposed
alternatives; the checked-in deployment templates have not been changed.

## Sharing `cippabcmq-plan`

Dedicated App Service tiers charge for plan VM instances, with apps sharing
their CPU/memory. A separate app on unchanged capacity therefore has zero
incremental **plan compute**, although associated services can still cost money.
Sharing also couples capacity and scaling to CIPP. Inspect the actual SKU,
per-instance utilization during busy periods, memory headroom and scaling rules
before choosing this option. A larger plan changes the economics.
([App Service plans](https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans))

By default each app runs on every plan instance. This service's cache, upstream
retry state and rate limiter are process-local; more than one active process is
not a supported scaling design. An existing multi-instance plan needs a verified
app placement solution or a service redesign, not an assumed replica limit.
([App Service placement](https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans),
[CippReader](../../server/CippReader.cs), [Program](../../server/Program.cs))

App Service compatibility is not yet established: `ServiceSettings` loads JSON
and a secret from files, defaulting to `/config/settings.json` and
`/run/secrets/cipp-client-secret`. Its environment variables select file paths;
they do not replace file contents. App Service app settings are injected as
environment variables. Prove secure file delivery, permissions and rotation, or
implement a reviewed configuration adapter before promising a direct deployment.
([ServiceSettings](../../server/ServiceSettings.cs),
[custom-container configuration](https://learn.microsoft.com/en-us/azure/app-service/configure-custom-container))

## Minimal separate Container App

Proposed shape: Consumption profile, HTTP ingress, `minReplicas: 0`,
`maxReplicas: 1`, single revision mode, 0.25 vCPU / 0.5 GiB. HTTP scaling supports
zero replicas. Budget for startup and the scale-in delay as well as request
handling; sparse requests are not equivalent to zero runtime. Test client
timeouts after a cold start. Process restarts clear this app's caches and limits;
revision replacement can briefly overlap processes.
([ACA scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app),
[existing deployment constraints](../AZURE-DEPLOYMENT.md))

ACA advertises **180,000 vCPU-seconds, 360,000 GiB-seconds and two million requests
per subscription per calendar month**, shared across the subscription. Zero
replicas incur no resource-consumption charge. A running minimum-one replica
incurs active or reduced idle charges; minimum zero does not make its running
replicas free. Additional networking/services and some advanced environment
features can add charges.
([ACA billing](https://learn.microsoft.com/en-us/azure/container-apps/billing))

Derived illustration, only where those grants apply and remain entirely unused:
0.25 vCPU / 0.5 GiB exhausts both compute allowances after 720,000 allocated
seconds, or **200 hours**. A 30-day continuously allocated replica uses 648,000
vCPU-seconds and 1,296,000 GiB-seconds, exceeding those allowances. Idle billing
rates differ; this is allocation arithmetic, not a dollar quote. Subscription
offer eligibility must be checked separately.
([ACA resource meters](https://learn.microsoft.com/en-us/azure/container-apps/billing))

GHCR container-image storage and bandwidth are currently free; GitHub promises
at least one month's notice of policy changes. This is distinct from general
private GitHub Packages quotas. Private GHCR images need pull authentication;
public images permit anonymous pulls. Do not change image visibility just to
avoid credentials. Build/CI usage is a separate budget consideration.
([GitHub billing](https://docs.github.com/en/billing/concepts/product-billing/github-packages),
[GHCR access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry))

This removes the proposed new Basic ACR's registry charge and ACR build meter;
the registry-specific managed identity/AcrPull assignment would no longer be
needed. Basic ACR is not inherently free when the app is idle.
([ACR pricing](https://azure.microsoft.com/en-us/pricing/details/container-registry/))

ACA supports `logs-destination: none`, with live log streaming still available.
That avoids a new Log Analytics destination but loses retained console/system
logs, including this service's console audit records. This is an operational
tradeoff, not a transparent cost optimization.
([ACA logging](https://learn.microsoft.com/en-us/azure/container-apps/log-options),
[audit logging implementation](../../server/Program.cs))

## Free App Service and Functions

F1 lists 60 CPU minutes/day, 1 GB RAM and 1 GB storage, with no SLA and no
supported production use. Microsoft currently lists Linux code and containers
as supported on Free; do not dismiss it using the obsolete claim that Linux
containers always require a paid SKU. Free lacks Always On and custom domains;
the default `azurewebsites.net` hostname has TLS. Quotas and regional/offer
availability still need verification, as does the file configuration above.
([Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/),
[App Service limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#app-service-limits))

Functions supports .NET 10 isolated workers, but **Linux classic Consumption
does not support .NET 10**; Microsoft directs Linux users to Flex Consumption.
Its ASP.NET Core integration does not expose the normal middleware pipeline and
routing, so this service's auth/rate limiting/endpoints need a deliberate port.
([Functions .NET guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide))

Flex advertises 250,000 executions and 100,000 GB-s monthly for on-demand usage;
classic Consumption advertises one million executions and 400,000 GB-s. Grants
are subscription-wide and limited to eligible paid consumption subscriptions.
Storage and networking are billed separately, and always-ready Flex instances
have separate charges. These are not evidence of this Sponsorship offer's
eligibility or of lower total cost after a rewrite.
([Functions pricing](https://azure.microsoft.com/en-us/pricing/details/functions/))

## Sponsorship and final decision gate

A subscription display name does not establish available credit or offer terms.
The published Sponsorship offer has a usage cap and end date, exclusions, and
normally converts to PAYG when the cap or date is reached. It also states that
tiered pricing does not apply and services consume credits at flat PAYG rates.
Consequently, do not promise that this subscription receives the advertised
free grants, that all proposed costs are covered, or that ACA is absolutely
free. Verify the actual offer, credit balance, expiry and applicable meters.
([Sponsorship terms](https://azure.microsoft.com/en-us/pricing/offers/ms-azr-0036p/))

Next evidence needed: plan SKU/instance count and peak CPU/memory; compatibility
of secret-file provisioning; measured service startup/memory; acceptable cold
starts and audit retention; any required fixed outbound IP; and actual regional
offer pricing. Until then, sharing the plan is a promising conditional first
choice, with minimal ACA the separate-compute fallback.
