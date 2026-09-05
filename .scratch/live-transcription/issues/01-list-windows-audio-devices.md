# 01: List Windows audio devices from an agent-callable CLI

**What to build:** A Windows command-line executable that lets an agent discover the microphone and playback endpoints it can use for transcription. This first slice establishes the .NET solution, stable JSON command contract, production NAudio device discovery, and command-level test seam.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] The solution targets .NET 10 and includes production and automated test projects suitable for a Windows x64 command-line product.
- [ ] Running `devices` returns exactly one stable JSON result on standard output containing separate microphone and playback collections with durable endpoint IDs and human-readable names.
- [ ] The result identifies the current Windows communications defaults when Windows exposes them.
- [ ] Device discovery uses NAudio in production and a replaceable controlled adapter in command-level tests.
- [ ] Successful execution uses a zero exit code and does not mix diagnostics into standard output.
- [ ] Discovery or argument failures use a non-zero exit code, emit one machine-readable JSON result, and send any human-readable diagnostic detail to standard error.
- [ ] Windows command-level tests verify successful enumeration, empty collections, default identification, and device-discovery failure without requiring physical audio hardware.
