# Phase 1 - Foundation and Identity

## Objective

Create the solution foundation and make all browser, API, and device access
authenticated before any home-automation feature is exposed. Deliver the
in-memory home state, pluggable persistence seams, and DaveIdentity as a
library hosted by DaveBrain.

## Prerequisites

- Phase 0 decisions are recorded.
- The Windows Server HTTPS certificate and service-account plan are available.

## Implementation work

### 1. Solution and project graph

1. Create `DaveBrain`, `DaveHome`, `DaveIdentity`, and `DaveEar`, plus one
   matching test project per production project.
2. Enable nullable reference types, implicit usings, central package versions,
   and SDK-provided analyzers.
3. Add only `DaveBrain -> DaveHome` and `DaveBrain -> DaveIdentity` project
   references. Enforce that DaveIdentity and DaveEar do not reference
   DaveBrain or DaveHome.
4. Give DaveIdentity public `AddDaveIdentity(...)` and
   `MapDaveIdentityEndpoints(...)` integration methods. DaveBrain's startup
   code composes the two libraries but owns no OAuth protocol logic.

### 2. Home state and storage ports

1. Define immutable domain records, `HomeStateDocument`, `HomeState`,
   `StoreRevision`, `IHomeStateStore`, `IHomeStatePersistence`, and
   `ICommandAuditStore` in DaveHome.
2. Implement the in-memory indexed `IHomeStateStore`, including GUID
   validation, normalized-name indexes, location containment/cycle checks, and
   all-or-nothing candidate-state validation.
3. Implement `JsonSnapshotHomeStatePersistence` with temporary-file write,
   flush, backup, atomic replace, expected revision, and startup upgrade.
4. Implement the bounded in-memory command audit store.
5. Build the reusable home-state persistence contract suite. The JSON provider
   must pass it before it becomes the default. Keep the suite provider-neutral
   for future SQL implementations.

### 3. DaveIdentity and local accounts

1. Define `IdentityStateDocument`, `IIdentityUserStore`,
   `IOAuthClientStore`, and `IOAuthGrantStore` in DaveIdentity.
2. Implement `JsonSnapshotIdentityPersistence` with the same atomic and
   revision semantics as home-state persistence.
3. Use ASP.NET Core Identity's supported password hasher, cookie/session
   mechanisms, lockout support, security stamps, and role abstractions. Do not
   write custom password hashing or cookie cryptography.
4. When no active administrator exists, create the first local `Admin` as
   `Dave` with the one-time initial password `Dave`. Force its first
   successful sign-in to the password-change page and invalidate that initial
   password after a replacement is saved.
5. Implement Admin-only local account management: create, disable, reset
   password, assign/remove `Admin` or `User`, and invalidate sessions/grants
   after an account or role change.

### 4. Internal OAuth/OIDC authority

1. Register the fixed internal clients and scopes from Phase 0.
2. Implement discovery, JWKS, authorization, token, device, verification,
   revocation, and user-info endpoints in DaveIdentity using OpenIddict for
   authorization-server protocol behavior and Microsoft facilities elsewhere.
3. Use the configured asymmetric Windows Certificate Store key for token
   signing. Publish only public keys through JWKS and retain prior public keys
   during rotation overlap.
4. Implement authorization-code PKCE, local login/consent, device-code
   approval, one-time code consumption, rotating refresh tokens, revocation,
   and expiry cleanup.
5. Implement an internal Ear client-certificate authority, CSR validation,
   certificate issuance/renewal/revocation, Kestrel client-certificate
   validation, and `cnf.x5t#S256` token binding for DaveEar.
6. Add named policies for dashboard reads, configuration, hub management, Ear
   management, and diagnostic commands. Require both appropriate scope/client
   and `Admin` role for mutations.

### 5. Initial administration surface

1. Add the Razor Pages shell and local OIDC login/logout behavior.
2. Implement authenticated dashboard and location-tree views.
3. Allow only Admins to create, edit, and remove locations.
4. Implement Ear enrollment, heartbeat, disablement, and revocation endpoints
   under the policies defined above.
5. Apply HTTPS, secure HTTP-only same-site cookies, anti-forgery checks, and
   consistent problem-details responses from the first page/endpoint.

## Validation while implementing

- Run unit tests after each domain, identity, and protocol component.
- Exercise login, logout, lockout, password reset, role change, disabled
  account, consent denial, device-code expiry, refresh rotation, revocation,
  invalid scope/client, mTLS certificate mismatch, expired certificate, and
  certificate-revocation paths.
- Run the home and identity persistence contract suites against temporary
  snapshot directories.
- Verify that an anonymous visitor, User, Admin, disabled user, revoked Ear,
  and unregistered Ear all receive the correct access result.

## Exit checklist

- [ ] DaveBrain hosts DaveIdentity without OAuth/OIDC details leaking into
      DaveBrain pages/controllers.
- [ ] Home and identity snapshots restore valid state after restart.
- [ ] The first Admin is provisioned as the one-time `Dave` first-login
      account and must change its password before accessing other routes.
- [ ] Users are read-only and Admins alone can mutate configuration.
- [ ] DaveEar can complete device authorization and an approved enrollment.
- [ ] The provider contract suites pass for the default JSON providers.
