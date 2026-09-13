# ADR-001: OpenIddict and mTLS for DaveIdentity

## Status

Accepted.

## Context

DaveBrain is the internal OpenID Connect provider and OAuth 2.1 authorization
server for DaveBot. It requires:

- Authorization Code with PKCE for the DaveBrain Razor Pages client;
- Device Authorization Grant for DaveEar enrollment;
- a non-replayable, proof-of-possession mechanism for DaveEar access tokens;
- discovery metadata, JWKS, token issuance/refresh, consent, and revocation;
- local users and `Admin`/`User` roles;
- custom/provider-neutral durable stores so JSON snapshots can later be
  replaced with SQL.

The project preference is to use Microsoft and .NET built-in components where
they meet the requirement. ASP.NET Core Identity handles local accounts,
password hashing, roles, lockout, security stamps, and cookies but does not
provide a complete OAuth/OIDC authorization server.

OpenIddict is Apache-2.0 licensed, .NET-native, supports device authorization
and custom stores, but does not natively support DPoP (RFC 9449). OpenIddict's
maintainer confirms that DPoP is unsupported in its client, server, and
validation stacks. Its maintained mutual TLS support is the standards-based
proof-of-possession alternative for this service-to-service scenario.

## Decision

Use the following split in `DaveIdentity`:

| Responsibility | Selected technology |
|---|---|
| Local users, roles, password hashes, lockout, sessions | ASP.NET Core Identity |
| Authentication/authorization policies and cookies | ASP.NET Core Authentication and Authorization |
| Data Protection | ASP.NET Core Data Protection with Windows DPAPI |
| Cryptographic primitives and certificate access | .NET BCL / Windows Certificate Store |
| OAuth/OIDC authorization-server protocol | OpenIddict, Apache-2.0 |
| DaveEar proof of possession | OAuth 2.0 mutual TLS certificate-bound access tokens, RFC 8705 |

DaveIdentity uses OpenIddict only for the authorization-server protocol
features that Microsoft components do not provide. It remains the only
non-Microsoft identity dependency and is confined to the DaveIdentity project.
DaveBrain depends only on DaveIdentity's public registration and
endpoint-mapping APIs; DaveEar depends only on published standard endpoints.

Use one dedicated internal certificate authority for DaveEar client
certificates, separate from the public-facing self-signed server TLS
certificate and the OAuth token-signing certificate. During device enrollment,
DaveEar generates a private key and certificate-signing request. After an
Admin completes the Device Authorization Grant consent flow, DaveIdentity
issues an Ear-specific client certificate and records its SHA-256 thumbprint.

DaveEar presents that certificate for refresh-token and resource requests.
OpenIddict issues access tokens with `cnf.x5t#S256` bound to the client
certificate. DaveBrain accepts an Ear request only when:

1. The certificate chain is trusted by the internal Ear client-certificate CA.
2. The certificate is valid and not revoked.
3. Its SHA-256 thumbprint matches the token's `cnf.x5t#S256` confirmation
   claim.
4. The token issuer, subject, client ID, scope, certificate thumbprint, and
   Ear ID match the enrollment record.

Configure separate Kestrel listeners with the same server TLS certificate:
port 443 does not request client certificates and serves browser,
administration, discovery, device-code, and enrollment routes; port 8443
requires client certificates and serves Ear token refresh/resource routes.

Pin a current supported OpenIddict version centrally in Phase 1. Before
release, verify the selected version's Device Authorization Grant, mutual TLS
client authentication, certificate-bound access-token validation,
discovery/JWKS, PKCE, revocation, and custom-store integration.

## Consequences

### Positive

- Uses a fully open-source, Apache-2.0 OAuth/OIDC component.
- Uses Microsoft/BCL components for accounts, crypto, policy, hosting, and
  certificate integration.
- Avoids hand-written OAuth/OIDC and DPoP protocol code.
- Protects against access-token replay without storing shared device secrets.
- Fits the in-process DaveIdentity library and future SQL storage seam.

### Negative

- Every Ear needs an issued client certificate, secure private-key storage,
  certificate renewal, revocation, and recovery procedures.
- The server must maintain a dedicated internal Ear client-certificate CA.
- Browser and Ear endpoint TLS configuration must permit client certificates
  without requiring them from administration browsers.
- OpenIddict remains an external package that requires version pinning and
  security updates.

## Rejected alternatives

| Alternative | Reason |
|---|---|
| Duende IdentityServer Community Edition | Not fully open-source and has production licensing conditions. |
| ASP.NET Core Identity alone | It is not a complete OAuth/OIDC authorization server and does not provide device authorization or certificate-bound token protocol behavior. |
| Hand-written OAuth/OIDC or DPoP implementation | Security-critical, difficult to validate, and unnecessary when mTLS is available. |
| OpenIddict plus native DPoP | OpenIddict does not natively support DPoP. |
| Bearer tokens or `private_key_jwt` alone | Neither binds resource requests to the Ear certificate/private key; a stolen access token remains replayable. |
| External cloud identity provider | DaveBrain must be the internal identity provider. |

## Follow-up

Phase 1 must:

1. Pin the selected supported OpenIddict version in central package management.
2. Implement the DaveIdentity adapters and mTLS certificate issuance,
   validation, thumbprint binding, renewal, and revocation.
3. Run OAuth/OIDC integration tests for PKCE, device authorization, mTLS,
   certificate mismatch, refresh rotation, discovery/JWKS, and revocation.
4. Record the exact package version and Ear certificate/key-rotation procedure
   in operations documentation.

## References

- [OpenIddict issue #2483: DPoP support](https://github.com/openiddict/openiddict-core/issues/2483)
- [OpenIddict 7.7.0 release notes](https://github.com/openiddict/openiddict-core/releases/tag/7.7.0)
- [RFC 8705: OAuth 2.0 Mutual-TLS Client Authentication and Certificate-Bound Access Tokens](https://www.rfc-editor.org/rfc/rfc8705)
