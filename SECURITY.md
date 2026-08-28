# Security Policy

## Supported version

Security fixes are applied to the latest revision on the default branch.

## Reporting a vulnerability

Please use a
[private GitHub security advisory](https://github.com/medokin/ReservePane/security/advisories/new)
instead of a public issue. Do not include access tokens, refresh tokens,
credential files, account identifiers, or unredacted API responses.

Include reproduction steps, impact, and the affected revision when possible.

## Credential handling

ReservePane reads only the required fields from existing provider credentials
after discovering the corresponding local prerequisite. It does not modify
credential files or refresh tokens.
Logs intentionally exclude exception messages, request and response headers,
response bodies, credentials, and account identifiers.
