# Security policy

## Reporting a vulnerability

Please do not open a public issue or pull request for a security problem.

Use GitHub's private reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. If that button is not shown, open a public
issue that says only "I have a security report, please tell me how to send it" (no details), and the maintainers will reply with a private channel.

Please include:

- what you found and which part it affects (server, host, viewer, same-network token mode, installer, macOS),
- the version or commit, and the operating system,
- steps to reproduce, or a proof of concept, and what an attacker gains,
- whether the problem is already public.

## What to expect

This is a best-effort project with no service-level guarantee. The maintainers aim to acknowledge a report within 7 days, to say whether they consider it a
vulnerability within 14 days, and to fix confirmed problems in a new release, crediting the reporter unless asked not to. There is no bounty programme.
Please allow time for a fix before publishing details; if a report stalls, say so in the same private thread.

## Supported versions

Only the latest release receives fixes.

## Scope

In scope: the Remotely server, the Windows host and app, the macOS host and viewer, the installers, and the protocols between them (device handshake, same-network token handshake, relay, file transfer).

Known limits are written down in [docs/security.md](docs/security.md), with the tests that cover each protection. In particular, the same-network token handshake is not a
password-authenticated key exchange: a party that impersonates the sharing PC while someone types the token can test guesses offline (the cost is calculated in that document).
Reports that only restate a limit listed there are welcome as suggestions but are not treated as new vulnerabilities.

Out of scope: attacks that need administrator access on the PC that is attacked, physical access to an unlocked machine, social engineering of the person who reads out a token,
and findings in third-party software that Remotely only uses (report those to their authors).

## Testing guidelines

Test only against PCs and servers you own or have permission to test. Do not access other people's data, and do not run denial-of-service tests against shared networks.
