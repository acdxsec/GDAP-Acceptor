# Exact Windows signing fixtures

These are the exact EXE and application DLL delivered in the internally signed
0.4.0 ZIP on 2026-10-05, plus its public certificate and original unsigned EXE
as a negative control. No private key, account token, customer data, or CIPP
credentials are included. These fixtures are not a release or installer.

Source: `6e20f1a38621bdaf7a074789310dae016bc5c505`; locally built with
.NET SDK 10.0.401/runtime 10.0.12 and signed using osslsigncode 2.13 on Ubuntu.
The matching original unsigned EXE hash is checked by the test.

`tools/Test-WindowsSignature.ps1` uses Windows' Authenticode APIs, checks that
the signer is enumerable before trust, then validates after temporarily
trusting only the supplied certificate on an ephemeral CI runner. It rejects
unsigned and tampered controls and removes the temporary trust entries.

This test does not establish Explorer UI behavior on every Windows build or
approval by a customer's ASR/application-control policy. No app is launched.
