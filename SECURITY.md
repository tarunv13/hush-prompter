# Security policy

## Reporting a vulnerability

Please report security problems privately through GitHub: open the **Security** tab of this repository and choose **Report a vulnerability**. Do not open a public issue for a vulnerability.

You can expect an acknowledgement within a week. Please include the version, the steps to reproduce, and the impact you see.

## Supported versions

Only the latest release receives fixes.

## Threat model in brief

Hush is a local desktop app. It has no server, no network listener and no account. The parts that handle untrusted input are:

| Input | Protection |
| :--- | :--- |
| Imported files (`.txt`, `.md`, `.rtf`, `.docx`, `.html`) | 25 MB file cap; DOCX read through a byte-limited stream (zip bombs) with DTDs and external entities disabled (XXE); HTML converted to text, never rendered |
| `settings.json` | 256 KB cap, recursion limit, every value clamped, unknown values reset to defaults |
| Note files | random ids as file names; any id with characters other than letters, digits or hyphens is rejected (no path traversal) |
| AI responses | shown as text for review and applied only on request; never executed |
| Script text sent to AI | wrapped in delimiters the script cannot close; the model is told to treat it as data |
| Claude Code CLI | absolute-path discovery only; constant arguments; tools, MCP servers and settings files disabled; empty sandbox working folder; input over stdin |
| Claude API key | DPAPI-encrypted for the current Windows user; HTTPS with TLS 1.2+ to `api.anthropic.com` only; redirects not followed |
| Ollama | loopback address `127.0.0.1` only |

Automated checks: the offline test suite includes hostile-input tests (XXE, zip bomb, path traversal, tampered settings and key blobs, argument quoting checked against `CommandLineToArgvW`), and CodeQL scans every push.
