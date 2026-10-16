# Signed updater fixtures

These two ZIPs contain an inert managed assembly with version `1.4.0.0`, an inert
native-file placeholder and a synthetic runtime certificate. They have no game,
account or capture data and must never be installed into a real game.

Both use the public release key embedded in the updater. `signed-update.zip` is
valid; `signed-invalid-contract.zip` has a valid signature and an unsupported
manifest schema. Keeping these signed inputs lets the checks exercise the
production verifier and transactions without the private signing key. Test
mutations of their manifest, signature or payload must fail validation.
