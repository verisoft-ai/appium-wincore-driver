using System.Runtime.CompilerServices;

// Lets DesktopDriverServer.Tests exercise internal seams directly (e.g. the
// cached-page-source / cached-XPath-model builders) without needing a mocking
// library for the IUIAutomationElement COM interop types.
[assembly: InternalsVisibleTo("DesktopDriverServer.Tests")]
