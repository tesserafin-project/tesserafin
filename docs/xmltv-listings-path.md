# XMLTV listings path — operator guidance

The XMLTV listings provider reads its guide data from a path the operator
configures. That path may be an `http`/`https` URL or **any path on the server's
host filesystem**. This is intentional: the operator decides where their guide
data lives, and the server does not confine it to a managed directory.

## Who can set it

The listings provider configuration is written through
`POST /LiveTv/ListingProviders`, which carries
`[Authorize(Policy = Policies.RequiresElevation)]`. That policy requires the
custom authentication scheme plus the `Administrator` role claim. Concretely:

- an unauthenticated request is rejected;
- an ordinary authenticated user is rejected — the role claim is only issued to
  a user with `PermissionKind.IsAdministrator`, or to an API key;
- the incomplete-startup grant (`FirstTimeSetupOrElevated`) does **not** apply —
  this endpoint does not use that policy.

No less-trusted caller can set the path.

## What the server does with it

The configured path is **only read**:

- `Validate` calls `File.Exists` on it;
- `GetXml` opens it for reading and copies the contents into the server's own
  cache directory, `<cache>/xmltv/<provider id>.xml`.

Every write, delete and overwrite performed by the provider targets that cache
file. The operator's own file is never written, renamed, deleted, or passed to a
subprocess. `XmlTvOperatorPathBoundaryTests` asserts this against the real
provider, including that the file's bytes and last-write time are unchanged
after a listings fetch.

## Consequences for the operator

- Point the setting at a file the server process can read. A path outside every
  server-managed directory is supported and expected.
- Treat the setting as administrator-only configuration, exactly like a library
  path: an administrator who can set it can already read any file the server
  process can read, and can already point the library at arbitrary host
  directories.
- Static analysis reports this path as user-influenced. That is accurate as far
  as it goes — the value does come from a request body — but the trust boundary
  is the elevated-administrator authorization above, not a containment root.
  Introducing a containment root here would break legitimate operator-configured
  storage.
