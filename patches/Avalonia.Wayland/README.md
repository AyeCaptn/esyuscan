# Native Wayland crash fix

Avalonia 12.1.4 can deliver a queued `xdg_surface.configure` callback after the
window disconnects. `WXdgTopLevel.OnConfigureBatchComplete` then calculates the
maximum size using cleared globals, causing an unhandled `NullReferenceException`
on the Wayland worker and aborting esyuscan.

`stale-configure.patch` ignores callbacks whose sender is no longer the window's
current surface. This also prevents a serial from an old connection being used
for a replacement surface. Callbacks for the current surface are unchanged.

`make restore` downloads the revision recorded in `Source.props`, verifies its
SHA-256, and applies the patch under ignored `artifacts/avalonia-wayland/`.
The local projects compile only the Wayland backend and its upstream source
generators; the other Avalonia assemblies remain official NuGet packages.
No system packages or shared NuGet cache files are patched. Build prerequisites
include `curl`, `tar`, `sha256sum`, and `patch`.

The signed `tests/Wayland.Tests` assembly uses Avalonia's existing test-friend
access. It exercises actual configure dispatch for current, disconnected, and
replaced surfaces without starting a compositor. Normal check commands run it.
Formatting is restricted to app/test source so downloaded upstream code stays
unchanged apart from the patch.

When an upstream release fixes this issue, replace the local project reference
with the verified official package and remove this patch, bootstrap, and tests.
