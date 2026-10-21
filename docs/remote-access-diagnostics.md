# Remote-access diagnostics

Use this API to inspect the server's network observations before investigating a
remote-access problem. It **does not enable remote access** or prove that a server
is reachable or safe to publish.

This documents the endpoint already present on `master`, delivered in
[server PR #249](https://github.com/tesserafin-project/tesserafin/pull/249)
with its generated Web SDK. Issue
[#309](https://github.com/tesserafin-project/tesserafin/issues/309) describes that
same work as not implemented; it does not require a second endpoint. This guide
adds no server capability and does not certify a particular downloaded release.
Check the API document served by your installed build when its surface differs.

## Request

`POST /System/RemoteAccess/Diagnostics`, with a JSON body and an authenticated
administrator credential accepted by `Policies.RequiresElevation`. This uses the
existing elevation policy; it does not introduce a fresh-password challenge.
An ordinary user is refused. The inherited API-key authentication path also
satisfies this policy; treat such a key as an administrator credential.

Use the server's configured base URL, including any base path. For example, on a
local installation without a base path:

```sh
curl --fail-with-body --silent --show-error \
  --header @/path/to/private-admin-header.txt \
  --header 'Content-Type: application/json' \
  --data '{"Hostname":null,"IPv4Policy":"Unspecified","IPv6Policy":"Unspecified"}' \
  http://127.0.0.1:8096/System/RemoteAccess/Diagnostics
```

The private header file contains `Authorization: MediaBrowser Token="YOUR_TOKEN"`
using an existing administrator token; restrict the file to its owner. Do not put
a real token in the URL, command history or a bug report. The example uses HTTP
only on loopback; use an already trusted connection for another host.

| Field | Meaning |
| --- | --- |
| `Hostname` | Optional hostname to inspect, without a scheme, port or path. `null` requests a report without a DNS lookup. |
| `IPv4Policy` | Required: `Unspecified`, `DoNotPublish` or `Publish`. |
| `IPv6Policy` | Required: the same three named values. |

The policies describe the question being asked, not a configuration change. Use
`Unspecified` when no publication choice has been made. Numeric enum values,
missing policies and unrecognised values are rejected. To inspect DNS, replace
`null` with a hostname you control, keeping it in the JSON body. A valid hostname
causes a bounded lookup through the host's resolver; it is not an external
reachability test.

| Response | Interpretation |
| --- | --- |
| `200` | A report was collected. This includes an absent or syntactically invalid hostname, reported as a finding. |
| `400` | Invalid request body or a missing/invalid family policy. |
| `401` | Authentication failed or no credential was supplied. A disabled administrator is also rejected during authentication. |
| `403` | The authenticated caller does not satisfy the administrator policy. |
| `404` | Check the configured base path and whether the installed build exposes this endpoint. |

## Read the observations, not a verdict

A report has a schema version, a collection timestamp, the interpreted input,
backend bind posture and ports, proxy-trust counts, local-address class counts,
observations of ingress ports 80/443, DNS outcome and findings. Findings preserve
the engine's order and carry a code, severity and confidence. The response has
`Cache-Control: no-store`; each call collects again, one collection at a time.
Cancellation propagates through collection and DNS resolution.

Every report includes these four unresolved observations:

- `ExternalReachabilityUnverified`
- `FirewallStateUnknown`
- `RouterMappingUnknown`
- `CertificateReadinessUnverified`

A hostname resolving to an expected address, or an ingress port appearing free,
does not resolve those unknowns. There is no overall ready/secure/reachable flag,
score or publication permission. In particular, a successful `/health` request
and a `200` here do not test access from outside the host.

The operation writes no application/network configuration or publication
evidence, manages no proxy or certificate, and changes no listener, firewall or
router. Existing local installation defaults remain unchanged.

## Share a report

The wire projection omits raw local addresses, configured proxy addresses,
interface names, MAC addresses and process identities. It still includes the
normalised hostname supplied by the caller, network observations, ports and a
timestamp. Review those before sharing. Send only the relevant findings through
[the support channel](support.md); never attach the authentication header file.

## Contract and existing verification

The dedicated wire DTOs live in
[`Tesserafin.Server/Api/RemoteAccess`](../Tesserafin.Server/Api/RemoteAccess),
separate from the internal diagnostic records. The canonical contract includes
`CollectRemoteAccessDiagnostics` at the route above. A generated client already
exists in Tesserafin Web; changing this contract requires the normal paired SDK
transaction, not a hand-written API wrapper.

The server's [`ci/web-pair.lock.json`](../ci/web-pair.lock.json) selects the exact
merged Web commit used by the provenance gate. The SDK pin's source commit may
have a different identity after a rebase/squash: schema 2 requires matching
canonical bytes, locks and generated-file provenance, rather than treating
non-ancestry alone as a failure. Do not update the pair lock just because a newer
Web commit exists.

Existing executable checks:

| Area | Check |
| --- | --- |
| Real authentication/authorization pipeline | [`RemoteAccessDiagnosticsAuthenticationTests`](../tests/Tesserafin.Server.Integration.Tests/Controllers/RemoteAccessDiagnosticsAuthenticationTests.cs) |
| Request validation and report responses | [`RemoteAccessDiagnosticsEndpointTests`](../tests/Tesserafin.Server.Integration.Tests/Controllers/RemoteAccessDiagnosticsEndpointTests.cs) |
| Runtime registration, cancellation and non-mutation | [`RemoteAccessDiagnosticsRuntimeTests`](../tests/Tesserafin.Server.Integration.Tests/Controllers/RemoteAccessDiagnosticsRuntimeTests.cs) |
| Wire projection and architectural boundary | [`Diagnostics/RemoteAccess`](../tests/Tesserafin.Server.Tests/Diagnostics/RemoteAccess) |
| Generated canonical contract matches the server | [`OpenApiContractTests`](../tests/Tesserafin.Server.Integration.Tests/OpenApiContractTests.cs) |
| Exact server/Web pair, generated SDK and content identity | [`verify-sdk-provenance.sh`](../ci/verify-sdk-provenance.sh) |

From a server checkout with the prerequisites described in
[the development instructions](../README.md#development):

```sh
./ci/run.sh
./ci/tests/web-provenance-fixtures.test.sh
./ci/verify-sdk-provenance.sh --server-repo . --workdir /tmp/tesserafin-diagnostics-pair
```

The provenance work directory must be empty. With a shallow server clone, fetch
the SDK pin’s `sourceCommit` first so the verifier can read its contract and lock.
That gate needs network access to
clone the locked Web commit and verify the generated SDK; a missing prerequisite
or an incomplete verification is not a pass. Prior acceptance is recorded in
PR #249. A documentation edit does not re-run that historical acceptance or
certify public exposure. Managed publication remains separate work under
[#241](https://github.com/tesserafin-project/tesserafin/issues/241).
