# Security

Do not report security vulnerabilities through public GitHub issues.
Report them to the [Microsoft Security Response Center](https://msrc.microsoft.com/create-report).
See [Microsoft's coordinated vulnerability disclosure policy](https://www.microsoft.com/msrc/cvd).

This development/test sample uses synthetic traffic, deliberately public visitor
access and an absolute demo deadline. It is not a production security boundary.
Managed identity authorizes server-side ingestion; it does not authenticate visitors.
Keep Application Insights local authentication disabled and grant only the
destination-scoped publisher role.

The documented diagnostic endpoint exposes allowlisted booleans, counters, status
codes and fixed failure codes. Never extend it to disclose credentials, raw SDK
payloads, headers, environment values or exception messages.
