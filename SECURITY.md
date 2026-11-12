# Security policy

## Reporting a vulnerability

Please report a suspected vulnerability privately, not in a public issue, discussion or pull request.

Use GitHub's private vulnerability reporting for this repository: open the **Security** tab and choose
**Report a vulnerability**. The report is visible only to the maintainers.

Include what you can of:

- the affected package and version (or commit),
- the configuration that matters (authentication mode, data source engine, deployment profile),
- the steps to reproduce, and what an attacker gains.

Do not include real credentials, connection strings or personal data in a report.

## What to expect

- We confirm that we received the report within 3 working days.
- We tell you whether we accept it, and what we plan to do, within 10 working days.
- We publish the fix in a new release and credit the reporter in the advisory, unless you prefer otherwise.

Please give us the time to release a fix before you disclose the issue publicly.

## Supported versions

Security fixes are released for the latest major version of the `Moberg.Beacon.*` packages. Older major versions
receive fixes only for critical issues, at the maintainers' discretion.

## Scope

`Beacon.SampleProject` and `Beacon.SampleGateway` are sample hosts. They are not published and are not meant for
production use; a weakness that exists only in a sample host's own wiring is still welcome as a normal issue.
